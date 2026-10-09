// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Core;
using FrameFlow.Decoding.Internal;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding;

/// <summary>
/// Hardware-decode selection and binding for <see cref="VideoDecoder"/>
/// (ADR-0033). Kept in a partial file so the core decode loop in
/// <c>VideoDecoder.cs</c> stays focused on the software path.
/// </summary>
public sealed partial class VideoDecoder
{
    /// <summary>No hardware backend. Sentinel for <see cref="_hardwareBackend"/>.</summary>
    private const int NoHardwareBackend = -1;

    /// <summary>
    /// The backend that <c>Open</c> bound, kept so engagement can be restored if
    /// FFmpeg renegotiates back onto it. Written at open, and cleared under <c>_codecSync</c>
    /// if an Auto decoder falls back to software on its first packet (#572). Read by the graph
    /// thread through <see cref="BoundBackend"/> while the decode worker can clear it, so it is
    /// <see langword="volatile"/> and read once.
    /// </summary>
    private volatile int _boundBackend = NoHardwareBackend;

    /// <summary>
    /// The backend currently producing frames, as an <see cref="int"/> so it can be
    /// written and read across threads without tearing.
    /// <see cref="NoHardwareBackend"/> means software.
    /// </summary>
    private volatile int _hardwareBackend = NoHardwareBackend;

    /// <summary>
    /// Identifies the hardware backend that is <b>decoding</b>, or
    /// <see langword="null"/> when the software decoder is in use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set when <see cref="Open(nint, int, HardwareDecodeOptions, HardwareDecodeCapabilities, ILoggerFactory, VideoDecoderOptions)"/>
    /// binds a backend, then tracked against every decoded frame. Binding is not
    /// engagement: FFmpeg accepts the device at open and can still refuse the hwaccel
    /// per stream in <c>get_format</c>, falling back to software. See
    /// <see cref="HwAccelEngagement"/>.
    /// </para>
    /// <para>
    /// Tracked rather than latched because FFmpeg calls <c>get_format</c> again when a
    /// stream changes coded format or dimensions, and can select differently the second
    /// time. A value fixed on the first frame would keep reporting software after a
    /// later renegotiation onto hardware, and hardware after a renegotiation off it.
    /// </para>
    /// </remarks>
    public HardwareDecodeBackendKind? HardwareBackend
    {
        get
        {
            // One read. Two would let a concurrent write land between the sentinel test
            // and the cast, returning (HardwareDecodeBackendKind)(-1) — a non-null value
            // outside the enum.
            var backend = _hardwareBackend;
            return backend == NoHardwareBackend ? null : (HardwareDecodeBackendKind)backend;
        }
    }

    /// <summary>
    /// Creates and opens a <see cref="VideoDecoder"/> applying the given
    /// hardware-decode policy (ADR-0033). When <paramref name="options"/> is
    /// <see langword="null"/>, this overload behaves identically to the
    /// software-only <see cref="Open(nint, int, VideoDecoderOptions?, ILogger?)"/> overload.
    /// </summary>
    /// <param name="videoOptions">
    /// Optional decoder configuration (e.g.
    /// <see cref="VideoDecoderOptions.PacketQueueCapacity"/>). When null the defaults
    /// from <see cref="VideoDecoderOptions"/> are used.
    /// </param>
    /// <param name="formatContextPtr">Open <c>AVFormatContext</c> the stream belongs to.</param>
    /// <param name="streamIndex">Index of the video stream to decode.</param>
    /// <param name="options">
    /// Hardware-decode policy. <see langword="null"/> makes this behave as the
    /// software-only overload.
    /// </param>
    /// <param name="capabilities">
    /// Backends the host was probed to support. <see langword="null"/> uses this process's
    /// hardware decode probe, running it first if nothing has yet, whenever
    /// <paramref name="options"/> asks for hardware. The first probe can take a second or
    /// so on a multi-GPU host; later ones are free. Pass
    /// <see cref="HardwareDecodeCapabilities.Empty"/> to force software decode.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory; silent when null.</param>
    /// <exception cref="HardwareDecodeUnavailableException">
    /// Thrown when <see cref="HardwareDecodeMode.Required"/> is configured and
    /// no candidate backend can be bound to the codec.
    /// </exception>
    public static VideoDecoder Open(
        nint formatContextPtr,
        int streamIndex,
        HardwareDecodeOptions? options,
        HardwareDecodeCapabilities? capabilities,
        ILoggerFactory? loggerFactory,
        VideoDecoderOptions? videoOptions = null
    ) =>
        Open(
            formatContextPtr,
            streamIndex,
            options,
            capabilities,
            videoOptions,
            loggerFactory?.CreateLogger<VideoDecoder>()
        );

    /// <summary>
    /// Internal overload taking a logger rather than a factory. Used by tests and the
    /// production factory.
    /// </summary>
    /// <remarks>
    /// <paramref name="refusals"/> are the backends <see cref="HardwareDecodeMode.Auto"/> does not
    /// bind for a codec (#574). <see langword="null"/> uses <see cref="KnownRefusals.Builtin"/>. A
    /// test passes none to reach a backend that is known to decode the codec wrongly.
    /// </remarks>
    internal static VideoDecoder Open(
        nint formatContextPtr,
        int streamIndex,
        HardwareDecodeOptions? options,
        HardwareDecodeCapabilities? capabilities,
        VideoDecoderOptions? videoOptions = null,
        ILogger? logger = null,
        IReadOnlyList<KnownRefusal>? refusals = null
    )
    {
        logger ??= NullLogger.Instance;
        options ??= new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled };
        // Null means "probe" (#181), as this overload and the controller factories document.
        // It used to become Empty, which is the set that forces software, so a caller asking
        // for Auto without capabilities never got hardware. The probe runs once per process
        // (HardwareDecodeProbe.GetOrRun), so honouring null costs nothing after the first
        // call, and a Disabled decoder never asks for it.
        capabilities ??=
            options.Mode == HardwareDecodeMode.Disabled
                ? HardwareDecodeCapabilities.Empty
                : HardwareDecodeProbe.GetOrRun(logger, probeUncatalogued: false, out _);
        // Common stream / codec parameter inspection.
        var fmtCtx = new AvFormatContextAccessor(formatContextPtr);
        nint streamPtr = fmtCtx.GetStream(streamIndex);
        var stream = new AvStreamAccessor(streamPtr);
        nint codecParPtr = stream.CodecPar;
        var codecPar = new AvCodecParAccessor(codecParPtr);

        int codecId = codecPar.CodecId;
        int width = codecPar.Width;
        int height = codecPar.Height;
        // Size the packet queue from the stream's frame rate so it holds more time than the
        // audio queue does. Both were fixed at 512 packets, which is ~10.9 s of AAC but only
        // ~8.5 s of 60 fps video — inverting the invariant that audio blocks the pump first
        // (#145). An explicit option still wins; see ReadAheadCapacity.
        int packetQueueCapacity =
            videoOptions?.PacketQueueCapacity
            ?? ReadAheadCapacity.ForVideo(
                stream.AvgFrameRateNum,
                stream.AvgFrameRateDen,
                ReadAheadCapacity.DefaultVideoReadAhead
            );

        int heldHardwareFrames = videoOptions?.HeldHardwareFrames ?? 0;

        int timeBaseNum = stream.TimeBaseNum;
        int timeBaseDen = stream.TimeBaseDen;

        nint codec = FFAvCodec.avcodec_find_decoder(codecId);
        if (codec == nint.Zero)
            throw new InvalidOperationException(
                $"No decoder found for codec ID {codecId} on stream {streamIndex}."
            );

        var codecName = FFAvCodec.avcodec_get_name(codecId);

        // Which hardware candidates to try, and in what order, is a pure decision.
        //
        // codec is the software candidate: what avcodec_find_decoder returns, and what Auto falls
        // back to. The hardware candidates can come from another decoder (#417): for AV1 that call
        // returns libdav1d, which has no hardware configs, and FFmpeg's own av1 decoder has them.
        string decoderName = DecoderNameOf(codec);
        var hardwareDecoders =
            options.Mode == HardwareDecodeMode.Disabled ? [] : HardwareDecoders(codecId, codec);
        var decision = DecoderChoice.Decide(
            codecId,
            codecName,
            decoderName,
            hardwareDecoders.SelectMany(d => ReadHwConfigs(d.Codec, d.Name)).ToList(),
            options.Mode,
            options.PreferredBackends,
            DecoderChoice.PlatformDefault(CurrentOs()),
            InitialisedBackends(capabilities),
            videoOptions?.Device?.AvHwDeviceType,
            refusals ?? KnownRefusals.Builtin,
            options.ExcludedCodecs
        );
        if (options.Mode != HardwareDecodeMode.Disabled)
        {
            // A refusal is not an attempt: nothing failed, so Required's exception and the "no
            // backend bound" warning do not describe it.
            foreach (var refusal in decision.Refused)
                LogHwRefused(logger, refusal.Backend.ToString(), codecName, refusal.Reason);
            LogHwChoice(logger, codecName, decision.Reason);
        }

        // An excluded codec has no hardware candidate. Required cannot be met, and says why.
        if (decision.Excluded && options.Mode == HardwareDecodeMode.Required)
        {
            throw new HardwareDecodeUnavailableException(
                codecId,
                codecName,
                attempts: [],
                reason: $"codec '{codecName}' is in HardwareDecodeOptions.ExcludedCodecs, so it "
                    + "does not decode on hardware."
            );
        }

        // Try hwaccel first (if requested), tracking attempts for diagnostics.
        var attempts = new List<HardwareDecodeAttempt>();
        HwAccelBinding? hwBinding = null;
        if (options.Mode != HardwareDecodeMode.Disabled && !decision.Excluded)
        {
            hwBinding = TryBindHwAccel(
                decision.Hardware,
                hardwareDecoders,
                codecParPtr,
                heldHardwareFrames,
                videoOptions?.Device,
                attempts,
                logger
            );
        }

        // Bind: hardware (if hwBinding != null) or software fallback.
        CodecContextHandle codecCtx;
        if (hwBinding is not null)
        {
            codecCtx = hwBinding.CodecCtx;
            LogHwBindSuccess(logger, codecName, hwBinding.Backend.ToString());
        }
        else
        {
            if (options.Mode == HardwareDecodeMode.Required)
            {
                throw new HardwareDecodeUnavailableException(codecId, codecName, attempts);
            }

            if (options.Mode == HardwareDecodeMode.Auto && attempts.Count > 0)
            {
                // We tried hwaccel and it didn't bind. Log clearly so this isn't silent.
                LogHwBindFellBack(logger, codecName, attempts.Count);
            }

            codecCtx = OpenSoftwareCodecContext(codec, codecParPtr);
        }

        // Allocate the reusable decode frame + packet.
        nint framePtr = FFAvUtil.av_frame_alloc();
        if (framePtr == nint.Zero)
        {
            DisposeBinding(hwBinding);
            codecCtx.Dispose();
            throw new InvalidOperationException("av_frame_alloc returned null (out of memory).");
        }
        var frame = new FrameHandle(framePtr);

        nint packetPtr = FFAvCodec.av_packet_alloc();
        if (packetPtr == nint.Zero)
        {
            frame.Dispose();
            DisposeBinding(hwBinding);
            codecCtx.Dispose();
            throw new InvalidOperationException("av_packet_alloc returned null (out of memory).");
        }
        var packet = new PacketHandle(packetPtr);

        // If we are running hwaccel, allocate a second AVFrame to hold the
        // CPU-side copy produced by av_hwframe_transfer_data.
        FrameHandle? swFrame = null;
        if (hwBinding is not null)
        {
            nint swFramePtr = FFAvUtil.av_frame_alloc();
            if (swFramePtr == nint.Zero)
            {
                packet.Dispose();
                frame.Dispose();
                DisposeBinding(hwBinding);
                codecCtx.Dispose();
                throw new InvalidOperationException(
                    "av_frame_alloc returned null while allocating the SW transfer frame."
                );
            }
#pragma warning disable CA2000 // Ownership transfers to the decoder via the field assignment below.
            swFrame = new FrameHandle(swFramePtr);
#pragma warning restore CA2000
        }

        // Auto falls back to software when the bound hardware decoder rejects the first packet
        // (#572). Required keeps the fault, and a decoder already on software has nothing to
        // fall back from. It is opened from the software candidate, never from the decoder that
        // bound: FFmpeg's native av1 decoder has no software path to fall back to.
#pragma warning disable CA2000 // Ownership transfers to the decoder via the field assignment below.
        CodecContextHandle? softwareFallback =
            hwBinding is not null && options.Mode == HardwareDecodeMode.Auto
                ? TryPrepareSoftwareFallback(codec, codecParPtr, logger)
                : null;
#pragma warning restore CA2000

        var decoder = new VideoDecoder(
            codecCtx,
            frame,
            packet,
            width,
            height,
            timeBaseNum,
            timeBaseDen,
            packetQueueCapacity,
            logger
        );
        decoder._containerSampleAspectRatio = stream.SampleAspectRatio;
        decoder._codecSampleAspectRatio = codecPar.SampleAspectRatio;
        decoder._rotation = DisplayGeometry.RotationOf(codecPar.DisplayRotationDegrees);

        if (hwBinding is not null)
        {
            decoder._boundBackend = (int)hwBinding.Backend;
            decoder._hardwareBackend = (int)hwBinding.Backend;
            decoder._hwDeviceCtxRef = hwBinding.DeviceCtxRef; // ownership transfers
            decoder._hwPixelFormat = hwBinding.HwPixelFormat;
            decoder._extraHwFrames = hwBinding.ExtraHwFrames;
            decoder._swFrame = swFrame;
            decoder._softwareFallbackCtx = softwareFallback;
            decoder._softwareFallbackCodec = codec;
        }

        LogVideoDecoderOpened(decoder._logger, streamIndex, width, height, codecId);
        return decoder;
    }

    /// <summary>
    /// Tries each of the decision's hardware candidates in turn. Returns the first binding that
    /// opens successfully, or <see langword="null"/> if none did. Mutates
    /// <paramref name="attempts"/> with one entry per attempt.
    /// </summary>
    private static HwAccelBinding? TryBindHwAccel(
        IReadOnlyList<HwAccelCandidate> candidates,
        IReadOnlyList<HardwareDecoder> decoders,
        nint codecParPtr,
        int heldHardwareFrames,
        HardwareDevice? device,
        List<HardwareDecodeAttempt> attempts,
        ILogger logger
    )
    {
        if (candidates.Count == 0)
        {
            if (device is not null)
            {
                attempts.Add(
                    new HardwareDecodeAttempt(
                        device.Backend,
                        "the codec has no hardware configuration for the borrowed device's backend"
                    )
                );
            }
            return null;
        }

        foreach (var candidate in candidates)
        {
            var binding = TryBindSingle(
                candidate,
                decoders.First(d => d.Name == candidate.Decoder).Codec,
                codecParPtr,
                heldHardwareFrames,
                device,
                attempts,
                logger
            );
            if (binding is not null)
                return binding;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="codecId"/> advertises a hardware config for any backend
    /// whose device initialised on this host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-codec question <see cref="HardwareDecodeCapabilities"/> cannot answer: it
    /// reports which devices opened, and a device opening says nothing about whether a
    /// given codec can use it. On one machine H.264, HEVC and VP9 bind D3D11VA while AV1
    /// falls back to software against the same initialised device.
    /// </para>
    /// <para>
    /// It reports what the codec <i>advertises</i>, not what the driver will manage for a
    /// particular stream: profile, bit depth and resolution all still decide, and
    /// <c>TryBindSingle</c> is where that is found out. Stronger than "a device opened",
    /// weaker than "this clip decodes on hardware".
    /// </para>
    /// <para>
    /// Internal and narrow on purpose. Surfacing the codec dimension on the public
    /// capabilities surface is #355; this is the slice the decode path already computes,
    /// exposed so a test can gate on the same question the decoder asks.
    /// </para>
    /// </remarks>
    internal static bool HasHardwareCandidate(
        int codecId,
        HardwareDecodeCapabilities capabilities
    )
    {
        nint codec = FFAvCodec.avcodec_find_decoder(codecId);
        if (codec == nint.Zero)
            return false;

        var initialised = InitialisedBackends(capabilities);
        return HardwareDecoders(codecId, codec)
            .SelectMany(d => ReadHwConfigs(d.Codec, d.Name))
            .Any(c => initialised.Contains(c.Kind));
    }

    /// <summary>
    /// The names of the registered decoders for <paramref name="codecId"/> that can run on hardware
    /// (#417), for the tests that check which decoder a codec's hardware candidates come from.
    /// </summary>
    internal static IReadOnlyList<string> HardwareDecoderNames(int codecId)
    {
        nint codec = FFAvCodec.avcodec_find_decoder(codecId);
        return codec == nint.Zero
            ? []
            : HardwareDecoders(codecId, codec).Select(d => d.Name).ToList();
    }

    /// <summary>A registered decoder that can run on hardware, and the name it is registered under.</summary>
    private readonly record struct HardwareDecoder(string Name, nint Codec);

    // AV_CODEC_CAP_* bits from libavcodec/codec.h that mark a decoder not to offer as a hardware
    // candidate: one that is experimental, and the wrappers around a vendor's own decoder
    // (h264_cuvid, h264_qsv), which ADR-0033 rejected in favour of the hwaccel configs. The flags
    // are what identifies the wrappers: on the bundled 9.0 build every decoder carrying HARDWARE or
    // HYBRID is a _cuvid, _qsv or _amf wrapper (23 of them), and every one of them also has a
    // device-context config, so the config check alone would admit av1_cuvid for AV1.
    private const int CodecCapExperimental = 1 << 9;
    private const int CodecCapHardware = 1 << 18;
    private const int CodecCapHybrid = 1 << 19;

    /// <summary>
    /// The registered decoders for <paramref name="codecId"/> that can run on hardware: the default
    /// decoder when it has a hardware config, otherwise the others that have one (#417).
    /// </summary>
    /// <remarks>
    /// <c>avcodec_find_decoder</c> returns the first decoder registered for a codec, and for AV1
    /// that is <c>libdav1d</c>, which has no hardware configs. FFmpeg's own <c>av1</c> decoder has
    /// them. Looking past the default only when it has none leaves every codec whose default decoder
    /// already has configs (H.264, HEVC, VP8, VP9) exactly as it was. On the bundled build AV1 is the
    /// only codec whose answer differs from its default decoder.
    /// </remarks>
    private static unsafe List<HardwareDecoder> HardwareDecoders(int codecId, nint defaultCodec)
    {
        if (HasHwConfig(defaultCodec))
            return [new HardwareDecoder(DecoderNameOf(defaultCodec), defaultCodec)];

        var result = new List<HardwareDecoder>();
        nint opaque = nint.Zero;
        while (FFAvCodec.av_codec_iterate(ref opaque) is var candidate && candidate != nint.Zero)
        {
            if (candidate == defaultCodec || FFAvCodec.av_codec_is_decoder(candidate) == 0)
                continue;

            var codec = (AVCodec*)candidate;
            if ((int)codec->id != codecId)
                continue;
            if ((codec->capabilities & (CodecCapExperimental | CodecCapHardware | CodecCapHybrid)) != 0)
                continue;
            if (!HasHwConfig(candidate))
                continue;

            result.Add(new HardwareDecoder(DecoderNameOf(candidate), candidate));
        }

        return result;
    }

    private static bool HasHwConfig(nint codec)
    {
        for (int i = 0; ; i++)
        {
            nint cfgPtr = FFAvCodec.avcodec_get_hw_config(codec, i);
            if (cfgPtr == nint.Zero)
                return false;

            if ((new AvCodecHwConfigAccessor(cfgPtr).Methods & FFAvUtil.AvCodecHwConfigMethodHwDeviceCtx) != 0)
                return true;
        }
    }

    /// <summary>
    /// Every device-context hardware config of <paramref name="codec"/>, whether or not its device
    /// initialised. Choosing among them is <see cref="DecoderChoice.Decide"/>.
    /// </summary>
    private static List<HwAccelCandidate> ReadHwConfigs(nint codec, string decoderName)
    {
        var result = new List<HwAccelCandidate>();
        for (int i = 0; ; i++)
        {
            nint cfgPtr = FFAvCodec.avcodec_get_hw_config(codec, i);
            if (cfgPtr == nint.Zero)
                break;

            var cfg = new AvCodecHwConfigAccessor(cfgPtr);

            // We only support the AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX path
            // for v1 (ADR-0033). Other methods (HW_FRAMES_CTX, INTERNAL,
            // AD_HOC) require different setup that is out of scope.
            if ((cfg.Methods & FFAvUtil.AvCodecHwConfigMethodHwDeviceCtx) == 0)
                continue;

            result.Add(
                new HwAccelCandidate(
                    decoderName,
                    HardwareDecodeProbeBridge.ClassifyBackend(cfg.DeviceType),
                    cfg.DeviceType,
                    cfg.PixelFormat
                )
            );
        }

        return result;
    }

    /// <summary>
    /// The backends whose device initialised on this host. A backend listed in the bootstrap
    /// capabilities with Initialized=false is excluded: the device would not open even if the codec
    /// advertises it.
    /// </summary>
    private static HashSet<HardwareDecodeBackendKind> InitialisedBackends(
        HardwareDecodeCapabilities capabilities
    )
    {
        var kinds = new HashSet<HardwareDecodeBackendKind>();
        foreach (var backend in capabilities.Available)
        {
            if (backend.Initialized)
                kinds.Add(backend.Kind);
        }

        return kinds;
    }

    private static OsFamily CurrentOs() =>
        OperatingSystem.IsWindows() ? OsFamily.Windows
        : OperatingSystem.IsMacOS() ? OsFamily.MacOs
        : OperatingSystem.IsLinux() ? OsFamily.Linux
        : OsFamily.Other;

    /// <summary>The name FFmpeg registers <paramref name="codec"/> under, such as <c>libdav1d</c>.</summary>
    private static unsafe string DecoderNameOf(nint codec) =>
        Marshal.PtrToStringUTF8((nint)((AVCodec*)codec)->name) ?? "unknown";

    /// <summary>
    /// Allocates a codec context, attaches the chosen hwaccel device context,
    /// sizes its pool for <paramref name="heldHardwareFrames"/>, and opens it. Returns the
    /// binding on success, or <see langword="null"/> and appends an
    /// <see cref="HardwareDecodeAttempt"/> on failure.
    /// </summary>
    private static HwAccelBinding? TryBindSingle(
        HwAccelCandidate candidate,
        nint codec,
        nint codecParPtr,
        int heldHardwareFrames,
        HardwareDevice? device,
        List<HardwareDecodeAttempt> attempts,
        ILogger logger
    )
    {
        nint deviceCtxRef = nint.Zero;
        nint deviceRefForCtx = nint.Zero;
        CodecContextHandle? codecCtx = null;
        // What the pool's spare surfaces do not cover of the frames the caller holds (#384).
        // FFmpeg adds it to a fixed pool's size and ignores it for a growable one.
        int extraHwFrames = DecodePoolGuard.ExtraSurfacesFor(candidate.Kind, heldHardwareFrames);

        try
        {
            // A borrowed device is shared: this decoder owns one reference, as it would own a
            // device it created, and the device lives on after the decoder releases it.
            int rc = 0;
            if (device is not null)
                deviceCtxRef = device.Borrow();
            else
                rc = FFAvUtil.av_hwdevice_ctx_create(
                    out deviceCtxRef,
                    candidate.AvHwDeviceType,
                    device: null,
                    opts: nint.Zero,
                    flags: 0
                );
            if (rc != 0 || deviceCtxRef == nint.Zero)
            {
                attempts.Add(
                    new HardwareDecodeAttempt(
                        candidate.Kind,
                        $"av_hwdevice_ctx_create returned {rc}"
                    )
                );
                return null;
            }

            nint ctxPtr = FFAvCodec.avcodec_alloc_context3(codec);
            if (ctxPtr == nint.Zero)
            {
                attempts.Add(
                    new HardwareDecodeAttempt(
                        candidate.Kind,
                        "avcodec_alloc_context3 returned null"
                    )
                );
                return null;
            }
#pragma warning disable CA2000 // Ownership tracked manually; disposed in the finally block or transferred to HwAccelBinding on success.
            codecCtx = new CodecContextHandle(ctxPtr);
#pragma warning restore CA2000

            int rcParams = FFAvCodec.avcodec_parameters_to_context(ctxPtr, codecParPtr);
            if (rcParams < 0)
            {
                attempts.Add(
                    new HardwareDecodeAttempt(
                        candidate.Kind,
                        $"avcodec_parameters_to_context returned {rcParams}"
                    )
                );
                return null;
            }

            // Attach the hwaccel device context by adding a ref to the buffer
            // and writing it to AVCodecContext.hw_device_ctx. FFmpeg owns the
            // ref we hand it; we keep the original ref to dispose later.
            deviceRefForCtx = FFAvUtil.av_buffer_ref(deviceCtxRef);
            if (deviceRefForCtx == nint.Zero)
            {
                attempts.Add(
                    new HardwareDecodeAttempt(candidate.Kind, "av_buffer_ref returned null")
                );
                return null;
            }

            unsafe
            {
                ref AVCodecContext ctx = ref Unsafe.AsRef<AVCodecContext>((void*)ctxPtr);
                ctx.hw_device_ctx = (AVBufferRef*)deviceRefForCtx;
                if (extraHwFrames > 0)
                    ctx.extra_hw_frames = extraHwFrames;
            }

            int rcOpen = FFAvCodec.avcodec_open2(ctxPtr, codec, nint.Zero);
            if (rcOpen < 0)
            {
                attempts.Add(
                    new HardwareDecodeAttempt(candidate.Kind, $"avcodec_open2 returned {rcOpen}")
                );
                // FFmpeg now owns deviceRefForCtx via the codec context's
                // hw_device_ctx field; the codec context dispose path will
                // release it via av_buffer_unref.
                deviceRefForCtx = nint.Zero;
                return null;
            }

            // Success path — transfer ownership of deviceCtxRef + codecCtx to
            // the binding; clear locals so the catch/finally don't free them.
            var binding = new HwAccelBinding(
                Backend: candidate.Kind,
                AvHwDeviceType: candidate.AvHwDeviceType,
                HwPixelFormat: candidate.HwPixelFormat,
                DeviceCtxRef: deviceCtxRef,
                CodecCtx: codecCtx,
                ExtraHwFrames: extraHwFrames
            );
            deviceCtxRef = nint.Zero;
            deviceRefForCtx = nint.Zero;
            codecCtx = null;
            return binding;
        }
        catch (Exception ex)
        {
            attempts.Add(
                new HardwareDecodeAttempt(
                    candidate.Kind,
                    $"exception: {ex.GetType().Name}: {ex.Message}"
                )
            );
            return null;
        }
        finally
        {
            // If we did not reach the success path, release any references
            // that we still own. The ref we wrote to ctx.hw_device_ctx, if
            // any, is owned by FFmpeg once avcodec_open2 succeeded; if open
            // failed, FFmpeg's codec_close (called via CodecContextHandle
            // dispose) handles it.
            if (deviceRefForCtx != nint.Zero)
            {
                // Open did not succeed; AVCodecContext still references the
                // buffer ref. Its dispose path will unref it once we dispose
                // codecCtx below. Nothing to do here.
            }
            if (codecCtx is not null)
            {
                codecCtx.Dispose();
            }
            if (deviceCtxRef != nint.Zero)
            {
                FFAvUtil.av_buffer_unref(ref deviceCtxRef);
            }
        }
    }

    /// <summary>
    /// Opens a software codec context for the given codec/parameters.
    /// Mirrors the software-only <see cref="Open(nint, int, VideoDecoderOptions, ILogger)"/> path,
    /// factored so both the software-only entry and the HW fallback can
    /// share it.
    /// </summary>
    private static CodecContextHandle OpenSoftwareCodecContext(nint codec, nint codecParPtr)
    {
        var codecCtx = PrepareSoftwareCodecContext(codec, codecParPtr);
        int ret = FFAvCodec.avcodec_open2(codecCtx.DangerousGetHandle(), codec, nint.Zero);
        if (ret < 0)
        {
            codecCtx.Dispose();
            throw new InvalidOperationException($"avcodec_open2 failed with code {ret}.");
        }

        return codecCtx;
    }

    /// <summary>
    /// Allocates a software codec context and copies the stream's parameters into it, without
    /// opening it. The context holds the parameters (extradata included), so a later
    /// <c>avcodec_open2</c> needs nothing from the demuxer.
    /// </summary>
    private static CodecContextHandle PrepareSoftwareCodecContext(nint codec, nint codecParPtr)
    {
        nint ctxPtr = FFAvCodec.avcodec_alloc_context3(codec);
        if (ctxPtr == nint.Zero)
            throw new InvalidOperationException(
                "avcodec_alloc_context3 returned null (out of memory)."
            );

        var codecCtx = new CodecContextHandle(ctxPtr);

        int ret = FFAvCodec.avcodec_parameters_to_context(ctxPtr, codecParPtr);
        if (ret < 0)
        {
            codecCtx.Dispose();
            throw new InvalidOperationException(
                $"avcodec_parameters_to_context failed with code {ret}."
            );
        }

        return codecCtx;
    }

    /// <summary>
    /// Prepares the software context an <see cref="HardwareDecodeMode.Auto"/> decoder falls back
    /// to when its hardware decoder rejects the first packet (#572). Returns
    /// <see langword="null"/> when it cannot be prepared, which leaves the decoder without a
    /// fallback and no worse off than before.
    /// </summary>
    private static CodecContextHandle? TryPrepareSoftwareFallback(
        nint codec,
        nint codecParPtr,
        ILogger logger
    )
    {
        try
        {
            return PrepareSoftwareCodecContext(codec, codecParPtr);
        }
        catch (InvalidOperationException ex)
        {
            LogSoftwareFallbackUnavailable(logger, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Replaces the hardware codec context with the prepared software one after the hardware
    /// decoder rejected the first packet (#572). Called under <c>_codecSync</c> by
    /// <c>SendCurrentInput</c>, which then resends the same packet.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the decoder now decodes in software;
    /// <see langword="false"/> when the fallback would not open, in which case the decoder is
    /// unchanged and the fault stands.
    /// </returns>
    private bool TryFallBackToSoftware()
    {
        if (_softwareFallbackCtx is not { } fallback)
            return false;

        _softwareFallbackCtx = null;

        int ret = FFAvCodec.avcodec_open2(
            fallback.DangerousGetHandle(),
            _softwareFallbackCodec,
            nint.Zero
        );
        if (ret < 0)
        {
            fallback.Dispose();
            LogSoftwareFallbackUnavailable(_logger, $"avcodec_open2 failed with code {ret}.");
            return false;
        }

        // The discard level lives on the context and nowhere else, so it is carried across.
        unsafe
        {
            ref AVCodecContext oldCtx = ref Unsafe.AsRef<AVCodecContext>(
                (void*)_codecCtx.DangerousGetHandle()
            );
            ref AVCodecContext newCtx = ref Unsafe.AsRef<AVCodecContext>(
                (void*)fallback.DangerousGetHandle()
            );
            newCtx.skip_frame = oldCtx.skip_frame;
        }

        var backend = ((HardwareDecodeBackendKind)_boundBackend).ToString();

        _codecCtx.Dispose();
        _codecCtx = fallback;

        _swFrame?.Dispose();
        _swFrame = null;
        if (_hwDeviceCtxRef != nint.Zero)
            FFAvUtil.av_buffer_unref(ref _hwDeviceCtxRef);
        _hwPixelFormat = -1;
        _extraHwFrames = 0;

        // The bound backend is the one the decoder opened on, and it no longer is on it.
        _boundBackend = NoHardwareBackend;
        _hardwareBackend = NoHardwareBackend;

        LogHwFellBackToSoftware(_logger, backend);
        return true;
    }

    private static void DisposeBinding(HwAccelBinding? binding)
    {
        if (binding is null)
            return;

        binding.CodecCtx.Dispose();

        nint deviceCtxRef = binding.DeviceCtxRef;
        if (deviceCtxRef != nint.Zero)
            FFAvUtil.av_buffer_unref(ref deviceCtxRef);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Hardware decode bound: codec '{Codec}' on {Backend}."
    )]
    private static partial void LogHwBindSuccess(ILogger logger, string codec, string backend);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Hardware decode requested for codec '{Codec}' but no backend bound after {AttemptCount} attempt(s); falling back to software."
    )]
    private static partial void LogHwBindFellBack(ILogger logger, string codec, int attemptCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Hardware decode bound to {Backend} but a frame arrived in format {FrameFormat}, not the backend's {HwFormat}; FFmpeg refused the hwaccel for this stream and is decoding in software. Reporting software."
    )]
    private static partial void LogHwDisengaged(
        ILogger logger,
        string backend,
        int frameFormat,
        int hwFormat
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Hardware decode candidates for codec '{Codec}': {Reason}."
    )]
    private static partial void LogHwChoice(ILogger logger, string codec, string reason);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Hardware decode on {Backend} skipped for codec '{Codec}': {Reason}."
    )]
    private static partial void LogHwRefused(
        ILogger logger,
        string backend,
        string codec,
        string reason
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Hardware decoder on {Backend} rejected the first packet; reopened on the software decoder."
    )]
    private static partial void LogHwFellBackToSoftware(ILogger logger, string backend);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No software fallback for the hardware decoder: {Reason}"
    )]
    private static partial void LogSoftwareFallbackUnavailable(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Hardware decode on {Backend} engaged; frames are arriving in format {HwFormat}."
    )]
    private static partial void LogHwEngaged(ILogger logger, string backend, int hwFormat);

    /// <summary>
    /// Successful hwaccel bind: holds the device context ref (owned, must be
    /// freed by the decoder on dispose), the chosen codec context, the
    /// hardware pixel format the decoder will produce, and the
    /// <c>extra_hw_frames</c> it opened with.
    /// </summary>
    private sealed record HwAccelBinding(
        HardwareDecodeBackendKind Backend,
        int AvHwDeviceType,
        int HwPixelFormat,
        nint DeviceCtxRef,
        CodecContextHandle CodecCtx,
        int ExtraHwFrames
    );
}

/// <summary>
/// Internal shim that re-exports
/// <c>FrameFlow.Native.HardwareDecodeProbe.ClassifyBackend</c> from the
/// <c>FrameFlow.Decoding</c> assembly. <c>HardwareDecodeProbe</c> is
/// <c>internal</c> to <c>FrameFlow.Native</c>, and we want to keep its
/// visibility scoped — this bridge calls the public-by-extension classification
/// via <see cref="FrameFlow.Native.Interop.FFAvUtil"/> constants.
/// </summary>
internal static class HardwareDecodeProbeBridge
{
    internal static HardwareDecodeBackendKind ClassifyBackend(int avHwDeviceType) =>
        avHwDeviceType switch
        {
            FFAvUtil.AvHwDeviceTypeCuda => HardwareDecodeBackendKind.Cuda,
            FFAvUtil.AvHwDeviceTypeVaApi => HardwareDecodeBackendKind.VaApi,
            FFAvUtil.AvHwDeviceTypeD3D11Va => HardwareDecodeBackendKind.D3D11Va,
            FFAvUtil.AvHwDeviceTypeDxva2 => HardwareDecodeBackendKind.Dxva2,
            FFAvUtil.AvHwDeviceTypeVideoToolbox => HardwareDecodeBackendKind.VideoToolbox,
            FFAvUtil.AvHwDeviceTypeQsv => HardwareDecodeBackendKind.Qsv,
            FFAvUtil.AvHwDeviceTypeMediaCodec => HardwareDecodeBackendKind.MediaCodec,
            FFAvUtil.AvHwDeviceTypeVulkan => HardwareDecodeBackendKind.Vulkan,
            FFAvUtil.AvHwDeviceTypeDrm => HardwareDecodeBackendKind.Drm,
            FFAvUtil.AvHwDeviceTypeVdpau => HardwareDecodeBackendKind.Vdpau,
            FFAvUtil.AvHwDeviceTypeD3D12Va => HardwareDecodeBackendKind.D3D12Va,
            FFAvUtil.AvHwDeviceTypeOpenCl => HardwareDecodeBackendKind.OpenCl,
            _ => HardwareDecodeBackendKind.Other,
        };

    /// <summary>The <c>AVHWDeviceType</c> for <paramref name="kind"/>, or null for one FFmpeg has no device type for.</summary>
    internal static int? AvDeviceTypeOf(HardwareDecodeBackendKind kind) =>
        kind switch
        {
            HardwareDecodeBackendKind.Cuda => FFAvUtil.AvHwDeviceTypeCuda,
            HardwareDecodeBackendKind.VaApi => FFAvUtil.AvHwDeviceTypeVaApi,
            HardwareDecodeBackendKind.D3D11Va => FFAvUtil.AvHwDeviceTypeD3D11Va,
            HardwareDecodeBackendKind.Dxva2 => FFAvUtil.AvHwDeviceTypeDxva2,
            HardwareDecodeBackendKind.VideoToolbox => FFAvUtil.AvHwDeviceTypeVideoToolbox,
            HardwareDecodeBackendKind.Qsv => FFAvUtil.AvHwDeviceTypeQsv,
            HardwareDecodeBackendKind.MediaCodec => FFAvUtil.AvHwDeviceTypeMediaCodec,
            HardwareDecodeBackendKind.Vulkan => FFAvUtil.AvHwDeviceTypeVulkan,
            HardwareDecodeBackendKind.Drm => FFAvUtil.AvHwDeviceTypeDrm,
            HardwareDecodeBackendKind.Vdpau => FFAvUtil.AvHwDeviceTypeVdpau,
            HardwareDecodeBackendKind.D3D12Va => FFAvUtil.AvHwDeviceTypeD3D12Va,
            HardwareDecodeBackendKind.OpenCl => FFAvUtil.AvHwDeviceTypeOpenCl,
            _ => null,
        };
}
