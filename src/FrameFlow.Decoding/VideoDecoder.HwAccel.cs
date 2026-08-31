// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Internal;
using FrameFlow.Media;
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
    /// <summary>
    /// Identifies the hardware backend the decoder is currently bound to, or
    /// <see langword="null"/> when the software decoder is in use. Available
    /// after <see cref="Open(nint, int, HardwareDecodeOptions, HardwareDecodeCapabilities, ILoggerFactory?)"/>
    /// returns.
    /// </summary>
    public HardwareDecodeBackendKind? HardwareBackend { get; private set; }

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
            new SharedMemoryFramePool(),
            videoOptions,
            loggerFactory?.CreateLogger<VideoDecoder>()
        );

    /// <summary>
    /// Internal overload with an injectable buffer pool. Used by tests and the
    /// production factory.
    /// </summary>
    internal static VideoDecoder Open(
        nint formatContextPtr,
        int streamIndex,
        HardwareDecodeOptions? options,
        HardwareDecodeCapabilities? capabilities,
        IFrameBufferPool pool,
        VideoDecoderOptions? videoOptions = null,
        ILogger? logger = null
    )
    {
        logger ??= NullLogger.Instance;
        options ??= new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled };
        capabilities ??= HardwareDecodeCapabilities.Empty;
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

        int timeBaseNum = stream.TimeBaseNum;
        int timeBaseDen = stream.TimeBaseDen;

        nint codec = FFAvCodec.avcodec_find_decoder(codecId);
        if (codec == nint.Zero)
            throw new InvalidOperationException(
                $"No decoder found for codec ID {codecId} on stream {streamIndex}."
            );

        var codecName = FFAvCodec.avcodec_get_name(codecId);

        // Try hwaccel first (if requested), tracking attempts for diagnostics.
        var attempts = new List<HardwareDecodeAttempt>();
        HwAccelBinding? hwBinding = null;
        if (options.Mode != HardwareDecodeMode.Disabled)
        {
            hwBinding = TryBindHwAccel(
                codec,
                codecId,
                codecParPtr,
                options,
                capabilities,
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

        var decoder = new VideoDecoder(
            codecCtx,
            frame,
            packet,
            width,
            height,
            timeBaseNum,
            timeBaseDen,
            pool,
            packetQueueCapacity,
            logger
        );

        if (hwBinding is not null)
        {
            decoder.HardwareBackend = hwBinding.Backend;
            decoder._hwDeviceCtxRef = hwBinding.DeviceCtxRef; // ownership transfers
            decoder._hwPixelFormat = hwBinding.HwPixelFormat;
            decoder._swFrame = swFrame;
        }

        LogVideoDecoderOpened(decoder._logger, streamIndex, width, height, codecId);
        return decoder;
    }

    /// <summary>
    /// Iterates the codec's <c>AVCodecHWConfig</c> table, intersects with the
    /// initialised host capabilities, sorts by user preference and platform
    /// default order, and tries each candidate in turn. Returns the first
    /// binding that opens successfully, or <see langword="null"/> if none did.
    /// Mutates <paramref name="attempts"/> with one entry per attempt.
    /// </summary>
    private static HwAccelBinding? TryBindHwAccel(
        nint codec,
        int codecId,
        nint codecParPtr,
        HardwareDecodeOptions options,
        HardwareDecodeCapabilities capabilities,
        List<HardwareDecodeAttempt> attempts,
        ILogger logger
    )
    {
        // Build the candidate list: (kind, avDeviceType, hwPixelFormat).
        var candidates = EnumerateCandidates(codec, capabilities);
        if (candidates.Count == 0)
        {
            return null;
        }

        var sorted = SortByPolicy(candidates, options.PreferredBackends);

        foreach (var candidate in sorted)
        {
            var binding = TryBindSingle(candidate, codec, codecParPtr, attempts, logger);
            if (binding is not null)
                return binding;
        }

        return null;
    }

    private static List<HwAccelCandidate> EnumerateCandidates(
        nint codec,
        HardwareDecodeCapabilities capabilities
    )
    {
        // Build a lookup of which (kind) values have an initialised device on
        // this host. A backend listed in the bootstrap capabilities but with
        // Initialized=false is excluded — we already know the device wouldn't
        // open even if the codec advertises it.
        var initializedKinds = new HashSet<HardwareDecodeBackendKind>();
        foreach (var backend in capabilities.Available)
        {
            if (backend.Initialized)
                initializedKinds.Add(backend.Kind);
        }

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

            var kind = HardwareDecodeProbeBridge.ClassifyBackend(cfg.DeviceType);
            if (!initializedKinds.Contains(kind))
                continue;

            result.Add(new HwAccelCandidate(kind, cfg.DeviceType, cfg.PixelFormat));
        }

        return result;
    }

    /// <summary>
    /// Sorts the candidate set by user preference first, then platform default
    /// priority. Stable within each priority bucket so duplicate preferences
    /// are tolerated.
    /// </summary>
    private static List<HwAccelCandidate> SortByPolicy(
        List<HwAccelCandidate> candidates,
        IReadOnlyList<HardwareDecodeBackendKind> preferred
    )
    {
        var defaultOrder = PlatformDefaultOrder();
        int Rank(HardwareDecodeBackendKind kind)
        {
            // Preferred backends rank below 1000 in the order the user gave.
            for (int i = 0; i < preferred.Count; i++)
            {
                if (preferred[i] == kind)
                    return i;
            }
            // Then platform default order.
            for (int i = 0; i < defaultOrder.Length; i++)
            {
                if (defaultOrder[i] == kind)
                    return 1000 + i;
            }
            // Everything else (including Other) lands at the bottom.
            return 10_000;
        }

        var sorted = new List<HwAccelCandidate>(candidates);
        sorted.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)));
        return sorted;
    }

    private static HardwareDecodeBackendKind[] PlatformDefaultOrder()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                HardwareDecodeBackendKind.D3D11Va,
                HardwareDecodeBackendKind.D3D12Va,
                HardwareDecodeBackendKind.Dxva2,
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.Qsv,
            ];
        }
        if (OperatingSystem.IsMacOS())
        {
            return [HardwareDecodeBackendKind.VideoToolbox];
        }
        if (OperatingSystem.IsLinux())
        {
            return
            [
                HardwareDecodeBackendKind.VaApi,
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.Vdpau,
                HardwareDecodeBackendKind.Qsv,
                HardwareDecodeBackendKind.Vulkan,
                HardwareDecodeBackendKind.Drm,
            ];
        }
        return [];
    }

    /// <summary>
    /// Allocates a codec context, attaches the chosen hwaccel device context,
    /// and opens it. Returns the binding on success, or <see langword="null"/>
    /// and appends an <see cref="HardwareDecodeAttempt"/> on failure.
    /// </summary>
    private static HwAccelBinding? TryBindSingle(
        HwAccelCandidate candidate,
        nint codec,
        nint codecParPtr,
        List<HardwareDecodeAttempt> attempts,
        ILogger logger
    )
    {
        nint deviceCtxRef = nint.Zero;
        nint deviceRefForCtx = nint.Zero;
        CodecContextHandle? codecCtx = null;

        try
        {
            int rc = FFAvUtil.av_hwdevice_ctx_create(
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
                CodecCtx: codecCtx
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
    /// Mirrors the original <see cref="Open(nint, int, ILogger?)"/> path,
    /// factored so both the software-only entry and the HW fallback can
    /// share it.
    /// </summary>
    private static CodecContextHandle OpenSoftwareCodecContext(nint codec, nint codecParPtr)
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

        ret = FFAvCodec.avcodec_open2(ctxPtr, codec, nint.Zero);
        if (ret < 0)
        {
            codecCtx.Dispose();
            throw new InvalidOperationException($"avcodec_open2 failed with code {ret}.");
        }

        return codecCtx;
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

    /// <summary>
    /// Internal candidate descriptor for hwaccel binding.
    /// </summary>
    private sealed record HwAccelCandidate(
        HardwareDecodeBackendKind Kind,
        int AvHwDeviceType,
        int HwPixelFormat
    );

    /// <summary>
    /// Successful hwaccel bind: holds the device context ref (owned, must be
    /// freed by the decoder on dispose), the chosen codec context, and the
    /// hardware pixel format the decoder will produce.
    /// </summary>
    private sealed record HwAccelBinding(
        HardwareDecodeBackendKind Backend,
        int AvHwDeviceType,
        int HwPixelFormat,
        nint DeviceCtxRef,
        CodecContextHandle CodecCtx
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
}
