// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// Bridges an FFmpeg D3D11VA decode texture, NV12 or P010 (#559), to a <b>ring of shared,
/// keyed-mutex BGRA textures</b> that Avalonia's compositor can import. Color-converts
/// YUV → BGRA on the GPU with an <b>HLSL pixel shader on the general 3D pipeline</b>
/// (a fullscreen-triangle draw), not the fixed-function <c>VideoProcessorBlt</c> block,
/// and owns <see cref="BufferCount"/> shared output textures. Created lazily once the
/// first frame's device + dimensions are known. Windows / D3D11 only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owns its own D3D11 device (ADR-0064 Decision 2).</b> Every durable
/// resource — the shader pipeline, the keyed-mutex BGRA ring the compositor imports, and
/// the shader-readable NV12 the draw samples — lives on a <i>private device this converter
/// creates and owns</i>, on the <i>same adapter</i> as the decoder so cross-device shared
/// textures resolve. It does <b>not</b> borrow FFmpeg's decode device for its lifetime
/// (the pre-ADR-0064 shape, which orphaned the converter on a warm-sink player swap when the
/// old decode device was disposed — investigation 2026-06-12 §6 step 5). Instead the
/// per-frame decode slice is bridged onto the own device through a shareable NV12 staging
/// texture (see <b>Bridging the decode slice</b>), and when the decode device changes — a
/// playlist item boundary over a warm presenter — only that thin per-decode-device bridge
/// is <see cref="TryRebindDecodeDevice">rebound</see>; the ring, its compositor imports,
/// and the shader pipeline are untouched. So a source swap costs a cheap rebind, not a full
/// converter rebuild + compositor re-import.
/// </para>
/// <para>
/// <b>Resizing.</b> Three parts are sized by the frame: the staging texture, the ring, and the
/// decode bridge that opens the staging texture by its handle. The own device and the shader
/// pipeline are not. <see cref="TryResize"/> replaces the three sized parts and keeps the rest, so
/// a playlist item of another size costs new textures and a re-import of the ring, not a new
/// device and a shader compile.
/// </para>
/// <para>
/// <b>Why a pixel shader, not <c>VideoProcessorBlt</c> (ADR-0063).</b> The previous
/// implementation converted via <c>ID3D11VideoContext.VideoProcessorBlt</c> — the
/// fixed-function VideoProcessor unit, the same single hardware block DWM uses for
/// overlays. On a weak shared iGPU (an Intel HD 620, say) two presenters issuing
/// concurrent <c>VideoProcessorBlt</c>s contend on that one unit and <b>hang in the
/// driver</b>, an unkillable UI-thread freeze (investigation 2026-06-12, §9). Every other
/// player (VLC, mpv, OBS, Chrome) does NV12→RGB with a pixel shader on the 3D pipeline,
/// which is fully concurrent — N streams = N draw calls — and uses <c>VideoProcessorBlt</c>
/// only as a legacy D3D11&lt;11.1 fallback. This converter is that shader path: the
/// hanging fixed-function call is gone, so concurrent streams are a non-event.
/// </para>
/// <para>
/// <b>Why a ring.</b> A single shared texture ping-ponged on one keyed mutex
/// stalls whenever the compositor's consume cadence differs from decode: the
/// producer's next <c>AcquireSync(0)</c> blocks until the compositor releases
/// key 0, and times out under divergence. With N buffers the producer rotates to a
/// <i>different</i> texture each frame and the caller only reuses a buffer once its
/// present has completed — so the acquire never contends with an in-flight present.
/// This is the shape of Avalonia's own <c>samples/GpuInterop</c> <c>SwapchainBase</c>.
/// </para>
/// <para>
/// Each ring buffer is created with <see cref="ResourceOptionFlags.SharedKeyedMutex"/>
/// (the <c>D3D11TextureGlobalSharedHandle</c> import path) on the converter's own device, so
/// the compositor opens each shared texture on its own device by handle and never shares a
/// device with either the decoder or this converter.
/// </para>
/// <para>
/// <b>Bridging the decode slice (the cross-device hop).</b> FFmpeg's D3D11VA decode pool is
/// created <c>D3D11_BIND_DECODER</c>-only (FrameFlow lets FFmpeg auto-allocate it), so its
/// slices cannot be bound as a shader resource and cannot be opened on another device. So
/// the converter owns a single <b>shareable</b> <c>D3D11_BIND_SHADER_RESOURCE</c> NV12
/// texture (<see cref="Staging.Texture"/>, <see cref="ResourceOptionFlags.SharedKeyedMutex"/>) on
/// its own device, and opens it <i>by shared handle on the current decode device</i>
/// (<see cref="DecodeBridge.Nv12"/>). Each frame the chosen decode array slice is
/// <c>CopySubresourceRegion</c>'d into that decode-side handle — a cheap same-device
/// copy-engine blit on the decoder's <c>ID3D11Multithread</c>-protected immediate context
/// (the serialization-with-decode the old <c>VideoProcessorBlt</c> relied on) — under the
/// staging texture's keyed mutex, which fences the write so the own device sees it. The
/// shader then samples <see cref="Staging.Texture"/> on the own device via two SRVs: the Y plane
/// as <see cref="Format.R8_UNorm"/> and the interleaved UV plane as
/// <see cref="Format.R8G8_UNorm"/>, or <see cref="Format.R16_UNorm"/> and
/// <see cref="Format.R16G16_UNorm"/> for P010, with the sample levels in a constant buffer
/// from <see cref="YuvLevels"/>, as the D3D12 converter takes them. (Making the decoder emit shareable shader-readable
/// textures to drop this copy is a deferred follow-up — investigation 2026-06-12 §6 step 5.)
/// </para>
/// <para>
/// <b>The video processor mode (#560).</b> Built with a <see cref="PresenterOutput"/> whose
/// <see cref="PresenterOutput.SuperResolution"/> is set, the converter scales the staging texture
/// into a ring at the output's size with <c>VideoProcessorBlt</c> on its own device, with NVIDIA's
/// super-resolution stream extension on. That mode needs the staging texture bound
/// <c>RenderTarget | ShaderResource</c>: the driver refuses a video-processor input view on one
/// bound <c>ShaderResource</c> only. If any video-processor object fails to set up, the converter
/// falls back to the shader at the frame's size and reports <see cref="SuperResolutionFailed"/>.
/// Built without it, the converter is exactly the shader converter ADR-0063 describes.
/// </para>
/// </remarks>
internal sealed class D3D11Nv12SharedConverter : IDisposable
{
    /// <summary>Number of shared BGRA textures in the ring.</summary>
    public const int BufferCount = 3;

    // NV12 → BGRA color convert on the 3D pipeline. A fullscreen-triangle vertex shader
    // (no vertex/index buffers — positions are synthesized from SV_VertexID) and a pixel
    // shader that samples the Y + UV planes and applies the BT.709 studio (limited) range
    // → full-range RGB matrix. This replicates exactly the colorspace the old VideoProcessor
    // path set: DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709 → RgbFullG22NoneP709. Input and
    // output share G22 transfer + P709 primaries, so there is no gamma/primary conversion —
    // only the YCbCr→RGB matrix with the limited→full range expansion folded in.
    private const string Hlsl = """
        cbuffer Levels : register(b0)
        {
            float YOffset, YScale, COffset, CScale;
        };

        Texture2D    LumaTex   : register(t0);   // R8_UNORM or R16_UNORM view of the Y plane
        Texture2D    ChromaTex : register(t1);   // R8G8_UNORM or R16G16_UNORM view of the UV plane
        SamplerState Samp      : register(s0);

        struct VSOut
        {
            float4 pos : SV_Position;
            float2 uv  : TEXCOORD0;
        };

        // Fullscreen triangle: uv (0,0)->top-left .. (1,1)->bottom-right, matching D3D
        // texture sampling (v=0 at the top row) so the top-left-origin output is upright.
        VSOut VSMain(uint vid : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((vid << 1) & 2, vid & 2);
            o.uv  = uv;
            o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            return o;
        }

        float4 PSMain(VSOut input) : SV_Target
        {
            float  yX   = LumaTex.Sample(Samp, input.uv).r;
            float2 cbcr = ChromaTex.Sample(Samp, input.uv).rg;

            // Studio (limited) range bias + scale for the texture's sample format (YuvLevels).
            float Y = (yX     - YOffset) * YScale;
            float U = (cbcr.x - COffset) * CScale;
            float V = (cbcr.y - COffset) * CScale;

            // BT.709 inverse matrix (Kr=0.2126, Kb=0.0722).
            float3 rgb;
            rgb.r = Y +               1.5748 * V;
            rgb.g = Y - 0.1873  * U - 0.4681 * V;
            rgb.b = Y + 1.8556  * U;

            // SV_Target writes RGBA semantics; the B8G8R8A8_UNORM render target's byte order
            // is handled by the output merger, so no manual BGRA swizzle here.
            return float4(saturate(rgb), 1.0);
        }
        """;

    // Keyed-mutex key for the cross-device NV12 staging texture (_staging). Unlike the
    // BGRA ring's 0→1 producer→compositor ping-pong, the staging texture's two users — the
    // decode device (writes the copy) and the own device (samples it) — run strictly
    // sequenced inside a single ConvertInto call, so one key suffices: each side acquires it,
    // does its work, and releases back to the same key, leaving the mutex re-armed for the
    // next frame. The acquire on the own side still inserts the cross-device GPU fence that
    // makes the decode-side copy visible. Using one key (not a 0/1 hand-off) means an aborted
    // frame can never strand the mutex at a key the next frame's first acquire would block on.
    private const ulong StagingKey = 0;

    // NVIDIA's post-processing extension interface and its super-resolution method (#560), the
    // call Chromium's swap_chain_presenter.cc and mpv's vf_d3d11vpp.c make.
    private static readonly Guid NvidiaPpeInterface = new("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7");
    private const uint NvidiaSuperResolutionMethod = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvidiaStreamExtension
    {
        public uint Version;
        public uint Method;
        public uint Enable;
    }

    private readonly ILogger _logger;

    // ── The converter's OWN device (ADR-0064 Decision 2) — stable across decode-device swaps and resizes ──
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _immediate; // own immediate context (sample + draw)
    private readonly ID3D11DeviceContext _deferred; // records the shader-pass command lists
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11Buffer _levels; // the shader's YuvLevels for the texture's sample format

    // ── Sized by the frame — replaced together by TryResize ──
    private Staging _staging; // SHAREABLE shader-readable copy target for the decode slice
    private RingBuffer[] _buffers;

    // ── The video processor mode (#560) — null in the shader mode ──
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext1? _videoContext;
    private ID3D11VideoProcessorEnumerator? _processorEnumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorInputView? _processorInput;
    private VideoProcessorStream[]? _processorStreams;
    private bool _blitsSubmitted;

    // ── Per-decode-device bridge — rebound (not rebuilt) when the decode device changes ──
    // Independent QI'd references to the CURRENT decode device + its multithread-protected
    // immediate context, plus the staging texture opened by shared handle on that device (the copy
    // destination). Replaced whole by RebindDecodeBridge on a device change, and by TryResize, whose
    // staging texture is a new one with a new handle.
    private DecodeBridge? _bridge;

    private bool _disposed;
    private bool _deviceLost;

    /// <summary>The frame's width: the staging texture's, and the per-frame copy's.</summary>
    public int Width { get; private set; }

    /// <summary>The frame's height.</summary>
    public int Height { get; private set; }

    /// <summary>The decode texture's format, NV12 or P010, which the staging texture and views are built for.</summary>
    public FrameFlow.Media.PixelFormat InputFormat { get; }

    /// <summary>
    /// What the ring was built for: the requested output, or the shader at the frame's size when the
    /// video processor failed to set up. A later blit failure leaves it as built and sets
    /// <see cref="SuperResolutionFailed"/>.
    /// </summary>
    public PresenterOutput Output { get; private set; }

    /// <summary>
    /// True when the video processor mode was requested and could not be used: it failed to set up,
    /// or a blit failed with something other than device loss, after which the shader fills the ring.
    /// </summary>
    public bool SuperResolutionFailed { get; private set; }

    /// <summary>The PCI vendor ID of the adapter the converter's device is on.</summary>
    public uint AdapterVendorId { get; }

    /// <summary>
    /// The native <c>ID3D11Device*</c> of the decode device this converter is <b>currently
    /// bound to</b> (the device its per-frame cross-device copy targets). Updated whenever
    /// <see cref="TryRebindDecodeDevice"/> rebinds the bridge to a new decode device. Exposed
    /// so the presenter can compare it against an incoming frame's device identity
    /// (<see cref="FrameFlow.Decoding.GpuVideoFrame.TryGetD3D11Texture"/>'s <c>device</c> out): when they differ,
    /// a warm-sink player swap brought a new decode device and the converter must rebind its
    /// decode bridge (ADR-0064). While bound, the converter holds COM references to this
    /// device (via <see cref="DecodeBridge.Nv12"/> / <see cref="DecodeBridge.Context"/>), so its
    /// pointer cannot be reused by a new device — the comparison is reuse-safe.
    /// </summary>
    public nint SourceDevicePointer => _bridge?.DevicePointer ?? nint.Zero;

    /// <summary>The legacy global shared handle for ring buffer <paramref name="index"/>,
    /// imported as <c>D3D11TextureGlobalSharedHandle</c>.</summary>
    public nint GetSharedHandle(int index) => _buffers[index].SharedHandle;

    /// <summary>
    /// <see langword="true"/> once a GPU device-loss / TDR (<c>DXGI_ERROR_DEVICE_REMOVED</c>
    /// or a sibling reset/hung) has been observed on this converter — set <b>reactively</b>
    /// when a keyed-mutex / GPU operation in <see cref="ConvertInto"/> throws a device-loss
    /// HRESULT (NOT a proactive per-frame <c>DeviceRemovedReason</c> poll: that false-positived
    /// on the borrowed FFmpeg device and starved the presenter into a rebuild storm). The
    /// presenter treats a lost converter as "drop the ring and recreate on the next frame"
    /// rather than blocking a doomed keyed-mutex acquire / device <c>Release</c> on the UI
    /// thread (investigation 2026-06-12, §6 step 6). Sticky: once observed it never clears —
    /// the owner rebuilds a fresh converter on the next frame.
    /// </summary>
    public bool IsDeviceLost => _deviceLost;

    private sealed class RingBuffer
    {
        public required ID3D11Texture2D Texture;
        public required ID3D11RenderTargetView RenderTargetView;
        public required ID3D11CommandList CommandList;
        public required IDXGIKeyedMutex KeyedMutex;
        public nint SharedHandle;
        public ID3D11VideoProcessorOutputView? ProcessorOutput;
    }

    /// <summary>
    /// The shareable, shader-readable texture the decode slice is copied into each frame, at the
    /// frame's size: its own-device keyed mutex, the global shared handle each decode device opens it
    /// by, and the two plane views the shader samples.
    /// </summary>
    private sealed class Staging
    {
        public required ID3D11Texture2D Texture;
        public required IDXGIKeyedMutex Mutex;
        public required nint SharedHandle;
        public required ID3D11ShaderResourceView Luma; // R8_UNORM or R16_UNORM view of the Y plane
        public required ID3D11ShaderResourceView Chroma; // R8G8_UNORM or R16G16_UNORM view of the UV plane
    }

    /// <summary>
    /// The staging texture opened on one decode device: independent references to that device and its
    /// <c>ID3D11Multithread</c>-protected immediate context, and the decode-side view of the staging
    /// texture with its keyed mutex.
    /// </summary>
    private sealed class DecodeBridge
    {
        public required ID3D11Device Device;
        public required ID3D11DeviceContext Context;
        public required ID3D11Texture2D Nv12;
        public required IDXGIKeyedMutex Mutex;
        public required nint DevicePointer;
    }

    /// <summary>
    /// The sized parts a resize replaced. The caller disposes it off the UI thread, as it disposes a
    /// dropped converter, after detaching the old ring's compositor imports.
    /// </summary>
    private sealed class Retired(D3D11Nv12SharedConverter owner, DecodeBridge? bridge, RingBuffer[] ring, Staging staging)
        : IDisposable
    {
        public void Dispose()
        {
            owner.ReleaseDecodeBridge(bridge);
            owner.ReleaseRing(ring);
            owner.ReleaseStaging(staging);
        }
    }

    /// <param name="nv12TexturePtr">The first frame's decode texture.</param>
    /// <param name="width">The frame's width.</param>
    /// <param name="height">The frame's height.</param>
    /// <param name="output">How to fill the ring, and at what size.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="superResolutionExtension">
    /// Whether the video processor mode turns the driver's extension on. Tests turn it off to
    /// compare the video processor's colours with the shader's.
    /// </param>
    public D3D11Nv12SharedConverter(
        nint nv12TexturePtr,
        int width,
        int height,
        PresenterOutput output,
        ILogger logger,
        bool superResolutionExtension = true)
    {
        // Each step is timed for the ready line. A rebuild runs on the presenter's UI thread, so what it
        // costs, and where, is worth seeing next to what a resize costs.
        long started = Stopwatch.GetTimestamp();
        _logger = logger;
        Width = width;
        Height = height;
        Output = output;

        // Borrow the first frame's texture: AddRef so disposing our wrapper is balanced
        // and FFmpeg's own reference is untouched. We use it only to (a) read the decode
        // device's adapter so our own device lands on the SAME adapter (cross-device shared
        // textures only resolve within one adapter), (b) read the NV12 format for the
        // staging texture, and (c) seed the initial decode-device bridge.
        Marshal.AddRef(nv12TexturePtr);
        using var nv12 = new ID3D11Texture2D(nv12TexturePtr);

        // The decode device wrapper is CACHED ON (and owned by) the nv12 wrapper — do NOT
        // dispose it; it dies with `using var nv12`. We only read its adapter + identity here
        // and QI independent references inside RebindDecodeBridge.
        var decodeDevice = nv12.Device;
        var srcFormat = nv12.Description.Format;
        var (samples, _, _) = YuvTextureFormats.For(srcFormat);
        InputFormat = samples == YuvSampleFormat.P010 ? FrameFlow.Media.PixelFormat.P010 : FrameFlow.Media.PixelFormat.Nv12;

        // OWN DEVICE on the decoder's adapter. DriverType.Unknown is required when an explicit
        // adapter is supplied. BgraSupport so the compositor can import our B8G8R8A8 ring.
        using (var dxgiDevice = decodeDevice.QueryInterface<IDXGIDevice>())
        {
            dxgiDevice.GetAdapter(out var adapter).CheckError();
            using (adapter)
            {
                AdapterVendorId = adapter.Description.VendorId;
                _device = CreateOwnDevice(adapter, output.SuperResolution);
            }
        }
        // The own device lives for the converter's lifetime, so its cached immediate-context
        // wrapper is valid for that lifetime — store it directly (the OWNERSHIP GOTCHA that
        // forced QI'd references in the old borrowed-device design does not apply to a device
        // WE own and keep). Mirrors D3D11BgraUploader, which also owns its device + context.
        _immediate = _device.ImmediateContext;
        long deviceDone = Stopwatch.GetTimestamp();

        // ── Shader pipeline objects (shared across all ring buffers) on the own device ──
        _vertexShader = _device.CreateVertexShader(CompileShader("VSMain", "vs_4_0"), null);
        _pixelShader = _device.CreatePixelShader(CompileShader("PSMain", "ps_4_0"), null);
        long shadersDone = Stopwatch.GetTimestamp();

        _sampler = _device.CreateSamplerState(
            new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear, // bilinear chroma upsample, like the VideoProcessor
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            }
        );

        // CullMode.None: the fullscreen triangle's winding flips between clip and screen
        // space; disabling culling renders it regardless of front-face convention.
        _rasterizer = _device.CreateRasterizerState(
            new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                FrontCounterClockwise = false,
                DepthClipEnable = true,
            }
        );

        // The staging texture is built with the binding the video processor needs for its input view
        // when that mode was asked for; the shader mode keeps the binding it always had.
        _staging = CreateStaging(width, height, srcFormat, videoProcessorInput: output.SuperResolution);

        _levels = CreateLevelsBuffer(_device, YuvLevels.For(samples));
        _deferred = _device.CreateDeferredContext();

        if (output.SuperResolution && !TrySetUpVideoProcessor(output, superResolutionExtension))
        {
            SuperResolutionFailed = true;
            Output = PresenterOutput.Shader(width, height);
        }

        _buffers = CreateRing(Output.Width, Output.Height, _staging);

        // One output view per ring buffer. If they fail, the ring is rebuilt at the frame's size for
        // the shader, as when the video processor fails to set up.
        if (_videoProcessor is not null && !TryCreateProcessorOutputs())
        {
            SuperResolutionFailed = true;
            ReleaseVideoProcessor();
            ReleaseRing(_buffers);
            Output = PresenterOutput.Shader(width, height);
            _buffers = CreateRing(Output.Width, Output.Height, _staging);
        }
        long resourcesDone = Stopwatch.GetTimestamp();

        // Seed the decode-device bridge for the first frame's decode device. After this the
        // converter is ready; subsequent frames on the same device reuse it, and a different
        // device triggers a rebind (not a rebuild).
        RebindDecodeBridge(decodeDevice);
        long bridgeDone = Stopwatch.GetTimestamp();

        double totalMs = Stopwatch.GetElapsedTime(started, bridgeDone).TotalMilliseconds;
        double deviceMs = Stopwatch.GetElapsedTime(started, deviceDone).TotalMilliseconds;
        double shaderMs = Stopwatch.GetElapsedTime(deviceDone, shadersDone).TotalMilliseconds;
        double resourceMs = Stopwatch.GetElapsedTime(shadersDone, resourcesDone).TotalMilliseconds;
        double bridgeMs = Stopwatch.GetElapsedTime(resourcesDone, bridgeDone).TotalMilliseconds;

        if (_videoProcessor is not null)
        {
            _logger.LogInformation(
                "D3D11 {Format}->BGRA converter ready (own device, ADR-0064) with driver super resolution (#560): "
                    + "{W}x{H} frames scaled to a {OW}x{OH} {N}-buffer shared keyed-mutex ring by VideoProcessorBlt; "
                    + "bound to decode device 0x{Dev:X}. Built in {BuildMs:F1} ms: device {DeviceMs:F1}, shaders "
                    + "{ShaderMs:F1}, resources {ResourceMs:F1}, bridge {BridgeMs:F1}.",
                InputFormat, width, height, Output.Width, Output.Height, BufferCount, SourceDevicePointer,
                totalMs, deviceMs, shaderMs, resourceMs, bridgeMs
            );
        }
        else
        {
            _logger.LogInformation(
                "D3D11 {Format}->BGRA shader converter ready (own device, ADR-0064): {W}x{H}, {N}-buffer shared "
                    + "keyed-mutex ring at {OW}x{OH}; bound to decode device 0x{Dev:X} via a shareable staging "
                    + "bridge (pixel-shader convert on the 3D pipeline, no VideoProcessorBlt). Built in {BuildMs:F1} ms: "
                    + "device {DeviceMs:F1}, shaders {ShaderMs:F1}, resources {ResourceMs:F1}, bridge {BridgeMs:F1}.",
                InputFormat, width, height, BufferCount, Output.Width, Output.Height, SourceDevicePointer,
                totalMs, deviceMs, shaderMs, resourceMs, bridgeMs
            );
        }
    }

    /// <summary>
    /// Resizes the converter in place for a frame of another size, as when a playlist moves to an item
    /// of another resolution. Only the parts the frame's size fixes are replaced: the staging texture
    /// and its plane views, the ring and its recorded passes, and the decode bridge, which opens the new
    /// staging texture on <paramref name="frameTexturePtr"/>'s decode device. The own device, the
    /// shaders, the sampler, the rasterizer and the levels buffer are kept. The ring is new, so the
    /// caller imports it again.
    /// </summary>
    /// <param name="frameTexturePtr">The new frame's decode texture.</param>
    /// <param name="width">The new frame's width.</param>
    /// <param name="height">The new frame's height.</param>
    /// <returns>
    /// The parts the resize replaced, for the caller to dispose off the UI thread once it has detached
    /// their compositor imports. <see langword="null"/> when the converter cannot be resized for this
    /// frame or allocating the new parts failed; the converter is then unchanged, and the caller
    /// rebuilds it.
    /// </returns>
    public IDisposable? TryResize(nint frameTexturePtr, int width, int height)
    {
        // The video processor mode is not resized: its processor and output views are built for one
        // input and output size, and the presenter picks that output per frame (#560). A converter
        // that fell back to the shader when it was built is in the shader mode.
        if (_disposed || _deviceLost || Output.SuperResolution)
            return null;

        long started = Stopwatch.GetTimestamp();
        Marshal.AddRef(frameTexturePtr);
        using var nv12 = new ID3D11Texture2D(frameTexturePtr);

        // The plane views and the kept levels buffer are built for one sample format (#559).
        var format = nv12.Description.Format;
        if (format != _staging.Texture.Description.Format)
            return null;

        Staging? staging = null;
        RingBuffer[]? ring = null;
        DecodeBridge bridge;
        long resourcesDone;
        try
        {
            staging = CreateStaging(width, height, format, videoProcessorInput: false);
            ring = CreateRing(width, height, staging);
            resourcesDone = Stopwatch.GetTimestamp();
            bridge = OpenDecodeBridge(nv12.Device, staging.SharedHandle);
        }
        catch (Exception ex)
        {
            if (D3D11DeviceLoss.IsDeviceLost(ex))
                _deviceLost = true;
            _logger.LogWarning(
                ex, "Converter could not resize to {W}x{H}; the presenter rebuilds it instead.", width, height);
            if (ring is not null)
                ReleaseRing(ring);
            if (staging is not null)
                ReleaseStaging(staging);
            return null;
        }

        var retired = new Retired(this, _bridge, _buffers, _staging);
        _staging = staging;
        _buffers = ring;
        _bridge = bridge;
        Width = width;
        Height = height;
        Output = PresenterOutput.Shader(width, height);

        long done = Stopwatch.GetTimestamp();
        _logger.LogInformation(
            "Converter resized in place to {W}x{H} in {ResizeMs:F1} ms: resources {ResourceMs:F1}, bridge "
                + "{BridgeMs:F1}. Device and shader pipeline kept; bound to decode device 0x{Dev:X}.",
            width, height,
            Stopwatch.GetElapsedTime(started, done).TotalMilliseconds,
            Stopwatch.GetElapsedTime(started, resourcesDone).TotalMilliseconds,
            Stopwatch.GetElapsedTime(resourcesDone, done).TotalMilliseconds,
            SourceDevicePointer
        );
        return retired;
    }

    /// <summary>
    /// The PCI vendor ID of the adapter <paramref name="texturePtr"/>'s device is on, read before a
    /// converter exists so the presenter can decide how to build one.
    /// </summary>
    public static uint AdapterVendorOf(nint texturePtr)
    {
        Marshal.AddRef(texturePtr);
        using var texture = new ID3D11Texture2D(texturePtr);
        using var dxgiDevice = texture.Device.QueryInterface<IDXGIDevice>();
        dxgiDevice.GetAdapter(out var adapter).CheckError();
        using (adapter)
            return adapter.Description.VendorId;
    }

    /// <summary>
    /// The converter's own device on <paramref name="adapter"/>. The video processor mode asks for
    /// video support, and falls back to a device without it when the adapter refuses the flag.
    /// </summary>
    private ID3D11Device CreateOwnDevice(IDXGIAdapter adapter, bool videoProcessor)
    {
        if (videoProcessor)
        {
            var withVideo = D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                null,
                out ID3D11Device? videoDevice);
            if (withVideo.Success && videoDevice is not null)
                return videoDevice;
            videoDevice?.Dispose();
            _logger.LogDebug("The adapter refused a video-support device (0x{Hr:X8}); creating one without.", withVideo.Code);
        }

        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device)
            .CheckError();
        return device!;
    }

    /// <summary>
    /// Creates the video processor that scales the staging texture to <paramref name="output"/>,
    /// with the colour spaces the shader implements and, when <paramref name="extension"/> is set,
    /// NVIDIA's super-resolution extension on. Returns <see langword="false"/>, with every
    /// partly created object released, if any step fails.
    /// </summary>
    private unsafe bool TrySetUpVideoProcessor(PresenterOutput output, bool extension)
    {
        try
        {
            _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _immediate.QueryInterface<ID3D11VideoContext1>();
            _processorEnumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(30, 1),
                InputWidth = (uint)Width,
                InputHeight = (uint)Height,
                OutputFrameRate = new Rational(30, 1),
                OutputWidth = (uint)output.Width,
                OutputHeight = (uint)output.Height,
                Usage = VideoUsage.PlaybackNormal,
            });

            var inputFormat = _staging.Texture.Description.Format;
            var input = _processorEnumerator.CheckVideoProcessorFormat(inputFormat);
            var bgra = _processorEnumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm);
            if ((input & VideoProcessorFormatSupport.Input) == 0 || (bgra & VideoProcessorFormatSupport.Output) == 0)
                throw new NotSupportedException($"The video processor reports {inputFormat} input {input} and BGRA output {bgra}.");

            _videoDevice.CreateVideoProcessor(_processorEnumerator, 0, out _videoProcessor).CheckError();
            _processorInput = _videoDevice.CreateVideoProcessorInputView(
                _staging.Texture,
                _processorEnumerator,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                });

            // The conversion the shader implements: BT.709 studio-range YCbCr to full-range RGB, both
            // G22, so the only difference between the modes is the scaler.
            var processor = _videoProcessor!;
            _videoContext.VideoProcessorSetStreamColorSpace1(processor, 0, ColorSpaceType.YcbcrStudioG22LeftP709);
            _videoContext.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.RgbFullG22NoneP709);
            _videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
            _videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new Vortice.RawRect(0, 0, Width, Height));
            _videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new Vortice.RawRect(0, 0, output.Width, output.Height));
            _videoContext.VideoProcessorSetOutputTargetRect(processor, true, new Vortice.RawRect(0, 0, output.Width, output.Height));

            if (extension)
            {
                var payload = new NvidiaStreamExtension
                {
                    Version = 1,
                    Method = NvidiaSuperResolutionMethod,
                    Enable = 1,
                };
                _videoContext.VideoProcessorSetStreamExtension(
                    processor, 0, NvidiaPpeInterface, (uint)sizeof(NvidiaStreamExtension), (nint)(&payload))
                    .CheckError();
            }

            _processorStreams = [new VideoProcessorStream { Enable = true, InputSurface = _processorInput }];
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The video processor could not be set up for driver super resolution; converting with the shader at "
                    + "the frame's size instead."
            );
            ReleaseVideoProcessor();
            return false;
        }
    }

    /// <summary>
    /// The shareable, shader-readable staging texture for frames of <paramref name="width"/> x
    /// <paramref name="height"/> in <paramref name="format"/>, with its keyed mutex, shared handle and
    /// plane views. <see cref="ResourceOptionFlags.SharedKeyedMutex"/> makes it openable by handle on the
    /// (changing) decode device so the copy can run there; <see cref="BindFlags.ShaderResource"/> lets
    /// the own device's shader sample it. Releases what it made if a step fails.
    /// </summary>
    private Staging CreateStaging(int width, int height, Format format, bool videoProcessorInput)
    {
        var (_, lumaFormat, chromaFormat) = YuvTextureFormats.For(format);
        ID3D11Texture2D? texture = null;
        IDXGIKeyedMutex? mutex = null;
        ID3D11ShaderResourceView? luma = null;
        try
        {
            texture = _device.CreateTexture2D(
                new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    // The video processor needs RenderTarget for its input view.
                    BindFlags = videoProcessorInput
                        ? BindFlags.RenderTarget | BindFlags.ShaderResource
                        : BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
                }
            );
            mutex = texture.QueryInterface<IDXGIKeyedMutex>();
            nint handle;
            using (var dxgi = texture.QueryInterface<IDXGIResource>())
                handle = dxgi.SharedHandle;

            luma = CreatePlaneView(texture, lumaFormat);
            var chroma = CreatePlaneView(texture, chromaFormat);
            return new Staging
            {
                Texture = texture,
                Mutex = mutex,
                SharedHandle = handle,
                Luma = luma,
                Chroma = chroma,
            };
        }
        catch
        {
            Release(() => luma?.Dispose());
            Release(() => mutex?.Dispose());
            Release(() => texture?.Dispose());
            throw;
        }
    }

    private ID3D11ShaderResourceView CreatePlaneView(ID3D11Texture2D texture, Format format) =>
        _device.CreateShaderResourceView(
            texture,
            new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
            }
        );

    /// <summary>
    /// A BGRA ring at <paramref name="width"/> x <paramref name="height"/>, each buffer with its shader
    /// pass over <paramref name="staging"/> recorded. Releases what it made if a step fails.
    /// </summary>
    private RingBuffer[] CreateRing(int width, int height, Staging staging)
    {
        var buffers = new RingBuffer[BufferCount];
        var made = new List<IDisposable>();
        try
        {
            for (var i = 0; i < BufferCount; i++)
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
                };
                var tex = _device.CreateTexture2D(desc);
                made.Add(tex);

                nint handle;
                using (var dxgi = tex.QueryInterface<IDXGIResource>())
                    handle = dxgi.SharedHandle;

                var rtv = _device.CreateRenderTargetView(tex, null);
                made.Add(rtv);
                var commandList = RecordConvertCommandList(rtv, width, height, staging);
                made.Add(commandList);
                var keyedMutex = tex.QueryInterface<IDXGIKeyedMutex>();
                made.Add(keyedMutex);

                buffers[i] = new RingBuffer
                {
                    Texture = tex,
                    RenderTargetView = rtv,
                    CommandList = commandList,
                    KeyedMutex = keyedMutex,
                    SharedHandle = handle,
                };
            }
            return buffers;
        }
        catch
        {
            for (var i = made.Count - 1; i >= 0; i--)
                Release(made[i].Dispose);
            throw;
        }
    }

    private bool TryCreateProcessorOutputs()
    {
        try
        {
            foreach (var buffer in _buffers)
            {
                buffer.ProcessorOutput = _videoDevice!.CreateVideoProcessorOutputView(
                    buffer.Texture,
                    _processorEnumerator!,
                    new VideoProcessorOutputViewDescription
                    {
                        ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                        Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                    });
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The video processor could not view the ring; converting with the shader at the frame's size "
                    + "instead (super resolution off)."
            );
            return false;
        }
    }

    private void ReleaseRing(RingBuffer[] buffers)
    {
        foreach (var b in buffers)
        {
            if (b is null)
                continue;
            Release(() => b.CommandList.Dispose());
            Release(() => b.RenderTargetView.Dispose());
            Release(() => b.KeyedMutex.Dispose());
            Release(() => b.Texture.Dispose());
        }
    }

    private void ReleaseStaging(Staging staging)
    {
        Release(() => staging.Chroma.Dispose());
        Release(() => staging.Luma.Dispose());
        Release(() => staging.Mutex.Dispose());
        Release(() => staging.Texture.Dispose());
    }

    private void ReleaseVideoProcessor()
    {
        if (_buffers is not null)
        {
            foreach (var buffer in _buffers)
            {
                if (buffer is null)
                    continue;
                var view = buffer.ProcessorOutput;
                buffer.ProcessorOutput = null;
                Release(() => view?.Dispose());
            }
        }

        Release(() => _processorInput?.Dispose());
        Release(() => _videoProcessor?.Dispose());
        Release(() => _processorEnumerator?.Dispose());
        Release(() => _videoContext?.Dispose());
        Release(() => _videoDevice?.Dispose());
        _processorStreams = null;
        _processorInput = null;
        _videoProcessor = null;
        _processorEnumerator = null;
        _videoContext = null;
        _videoDevice = null;
    }

    /// <summary>The shader's sample levels, as an immutable constant buffer on <paramref name="device"/>.</summary>
    private static unsafe ID3D11Buffer CreateLevelsBuffer(ID3D11Device device, YuvLevels levels) =>
        device.CreateBuffer(
            new BufferDescription((uint)sizeof(YuvLevels), BindFlags.ConstantBuffer, ResourceUsage.Immutable),
            new SubresourceData(&levels));

    /// <summary>
    /// Compiles one entry point of <see cref="Hlsl"/> at runtime via <c>D3DCompile</c> and
    /// returns the bytecode. Throws with the compiler's diagnostics on failure.
    /// </summary>
    private static byte[] CompileShader(string entryPoint, string profile)
    {
        var hr = Compiler.Compile(Hlsl, entryPoint, "Nv12ToBgra.hlsl", profile, out var blob, out var errorBlob);
        try
        {
            if (hr.Failure || blob is null)
            {
                var diagnostics =
                    errorBlob is null
                        ? "(no compiler diagnostics)"
                        : Marshal.PtrToStringAnsi(errorBlob.BufferPointer) ?? "(empty diagnostics)";
                throw new InvalidOperationException(
                    $"HLSL compile failed for {entryPoint} ({profile}): 0x{hr.Code:X8}. {diagnostics}"
                );
            }
            return blob.AsBytes();
        }
        finally
        {
            blob?.Dispose();
            errorBlob?.Dispose();
        }
    }

    /// <summary>
    /// Pre-records the NV12 → BGRA shader pass for one ring buffer into a deferred-context
    /// command list: bind the fixed pipeline (shaders, the two plane SRVs over
    /// <paramref name="staging"/>, sampler, rasterizer, a <paramref name="width"/> x
    /// <paramref name="height"/> viewport) with <paramref name="rtv"/> as the render target and draw
    /// the fullscreen triangle. Only the destination RTV varies per buffer (the SRVs are constant —
    /// they always view the staging texture, which <see cref="ConvertInto"/> re-copies each frame),
    /// so recording once per ring and replaying with <c>ExecuteCommandList</c> keeps the per-frame
    /// path allocation-free.
    /// </summary>
    private ID3D11CommandList RecordConvertCommandList(ID3D11RenderTargetView rtv, int width, int height, Staging staging)
    {
        _deferred.IASetInputLayout(null); // no vertex buffers — SV_VertexID synthesizes the triangle
        _deferred.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _deferred.VSSetShader(_vertexShader);
        _deferred.PSSetShader(_pixelShader);
        _deferred.PSSetShaderResources(0, new[] { staging.Luma, staging.Chroma });
        _deferred.PSSetSampler(0, _sampler);
        _deferred.PSSetConstantBuffer(0, _levels);
        _deferred.RSSetState(_rasterizer);
        _deferred.RSSetViewport(new Viewport(0, 0, width, height, 0.0f, 1.0f));
        _deferred.OMSetRenderTargets(rtv, null);
        _deferred.Draw(3, 0);
        // restoreDeferredContextState: false — each command list sets all of its own state,
        // so the next record starts from a clean default.
        return _deferred.FinishCommandList(false);
    }

    /// <summary>
    /// Rebinds the converter's per-decode-device bridge to <paramref name="frameTexturePtr"/>'s
    /// decode device if it differs from the one currently bound — the warm-sink player-swap path
    /// (ADR-0064). The own device, the keyed-mutex BGRA ring, its compositor imports, and the
    /// shader pipeline are <b>untouched</b>; only the cheap decode-side handle open of the shared
    /// NV12 staging texture (+ the decode immediate context) is reopened on the new device. So a
    /// source swap over a warm presenter costs this rebind, not a converter rebuild.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the bridge is bound to the incoming frame's device (either it
    /// already was, or it was rebound successfully); <see langword="false"/> if rebinding failed
    /// (e.g. the driver rejected opening the shared NV12 on this device) — in which case the
    /// caller should fall back to dropping + rebuilding the converter on the new device.
    /// </returns>
    public bool TryRebindDecodeDevice(nint frameTexturePtr)
    {
        if (_disposed || _deviceLost)
            return false;

        Marshal.AddRef(frameTexturePtr);
        using var nv12 = new ID3D11Texture2D(frameTexturePtr);
        return EnsureDecodeBridge(nv12);
    }

    /// <summary>
    /// Ensures the decode bridge is bound to <paramref name="nv12"/>'s device, rebinding if the
    /// device changed. Returns <see langword="false"/> (leaving the bridge cleared) if the rebind
    /// throws — the only place a decode-device change is acted on, so both the explicit
    /// <see cref="TryRebindDecodeDevice"/> and the defensive check at the top of
    /// <see cref="ConvertInto"/> share one code path.
    /// </summary>
    private bool EnsureDecodeBridge(ID3D11Texture2D nv12)
    {
        var devicePtr = nv12.Device.NativePointer;
        if (_bridge is { } bound && bound.DevicePointer == devicePtr)
            return true;

        try
        {
            RebindDecodeBridge(nv12.Device);
            _logger.LogInformation(
                "Converter rebound its decode bridge to device 0x{Dev:X} (warm-sink player swap, ADR-0064); "
                    + "ring + compositor imports kept warm, no converter rebuild.",
                SourceDevicePointer
            );
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Converter could not rebind its decode bridge to device 0x{Dev:X}; the presenter should fall back "
                    + "to a full converter rebuild on the new device.",
                devicePtr
            );
            DropDecodeBridge();
            return false;
        }
    }

    /// <summary>
    /// Releases the current decode-side bridge and opens a fresh one on <paramref name="decodeDevice"/>
    /// for the current staging texture.
    /// </summary>
    private void RebindDecodeBridge(ID3D11Device decodeDevice)
    {
        DropDecodeBridge();
        _bridge = OpenDecodeBridge(decodeDevice, _staging.SharedHandle);
    }

    /// <summary>
    /// Opens the staging texture with <paramref name="stagingHandle"/> on <paramref name="decodeDevice"/>:
    /// independent QI'd references to the device + its <c>ID3D11Multithread</c>-protected immediate
    /// context, and the staging texture opened by its global shared handle (the per-frame copy
    /// destination) plus that handle's decode-side keyed mutex. Releases what it opened if a step fails.
    /// </summary>
    private DecodeBridge OpenDecodeBridge(ID3D11Device decodeDevice, nint stagingHandle)
    {
        // Independent references we own and release ourselves. The decode-device wrapper handed
        // in is FFmpeg's (cached on the frame texture); QI gives us a ref whose lifetime we
        // control, which also pins the device so SourceDevicePointer stays reuse-safe while bound.
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        ID3D11Texture2D? nv12 = null;
        try
        {
            device = decodeDevice.QueryInterface<ID3D11Device>();
            context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
            nv12 = device.OpenSharedResource<ID3D11Texture2D>(stagingHandle);
            return new DecodeBridge
            {
                Device = device,
                Context = context,
                Nv12 = nv12,
                Mutex = nv12.QueryInterface<IDXGIKeyedMutex>(),
                DevicePointer = device.NativePointer,
            };
        }
        catch
        {
            Release(() => nv12?.Dispose());
            Release(() => context?.Dispose());
            Release(() => device?.Dispose());
            throw;
        }
    }

    private void DropDecodeBridge()
    {
        var bridge = _bridge;
        _bridge = null;
        ReleaseDecodeBridge(bridge);
    }

    private void ReleaseDecodeBridge(DecodeBridge? bridge)
    {
        if (bridge is null)
            return;
        Release(() => bridge.Mutex.Dispose());
        Release(() => bridge.Nv12.Dispose());
        Release(() => bridge.Context.Dispose());
        Release(() => bridge.Device.Dispose());
    }

    /// <summary>
    /// Color-converts the NV12 decode slice into ring buffer <paramref name="index"/>. Bridges the
    /// slice onto the own device through the shareable NV12 staging texture: the decode device
    /// copies the slice into the staging texture's decode-side handle (under the staging keyed
    /// mutex, which fences the write), then the own device samples it and draws into the ring buffer
    /// (the ring's keyed-mutex bracket: acquire key 0, release key 1; the compositor takes 1 → 0).
    /// The caller must only target a ring buffer whose previous present has completed, so the ring
    /// <c>AcquireSync(0)</c> never contends.
    /// </summary>
    /// <param name="index">The ring buffer to fill.</param>
    /// <param name="nv12TexturePtr">The frame's decode texture.</param>
    /// <param name="arraySlice">The frame's slice of the decode texture.</param>
    /// <returns>
    /// <see langword="true"/> if the buffer was filled and is ready to present;
    /// <see langword="false"/> if a GPU device-loss / TDR was observed mid-convert (in which case
    /// <see cref="IsDeviceLost"/> is now set), or the decode bridge could not be (re)bound to this
    /// frame's device — in either case the caller should drop this frame and rebuild the converter
    /// rather than presenting (step 6). Any non-device-loss GPU failure still throws.
    /// </returns>
    public bool ConvertInto(int index, nint nv12TexturePtr, int arraySlice)
    {
        // Short-circuit if a previous call already saw the device go away: the keyed-mutex
        // AcquireSync below would otherwise block / throw on a dead device.
        if (IsDeviceLost)
            return false;

        var buf = _buffers[index];

        Marshal.AddRef(nv12TexturePtr);
        using var nv12 = new ID3D11Texture2D(nv12TexturePtr);

        // Defensive: normally the presenter has already rebound via TryRebindDecodeDevice, so this
        // is a no-op; but ConvertInto is the single source of truth for the bridge matching this
        // frame's device, so a missed rebind self-heals here rather than copying onto a stale device.
        if (!EnsureDecodeBridge(nv12))
            return false;

        var bridge = _bridge!;
        var decodeContext = bridge.Context;
        var decodeSideNv12 = bridge.Nv12;
        var decodeSideMutex = bridge.Mutex;
        var sampleMutex = _staging.Mutex;

        var decodeAcquired = false;
        var sampleAcquired = false;
        var ringAcquired = false;
        try
        {
            // (1) DECODE DEVICE: copy the chosen decode-array slice into the shareable staging
            // texture's decode-side handle (copy engine, not the contended VideoProcessor), on the
            // decoder's ID3D11Multithread-protected immediate context — serialized with decode, the
            // protection the old path relied on. Bracketed by the staging keyed mutex so the write
            // is fenced for the own device. The explicit frame-sized Box is required: the decode
            // slice is macroblock-aligned (taller than the coded frame), so a full-subresource copy
            // would overflow the frame-sized staging texture — we take only the top-left region.
            decodeSideMutex.AcquireSync(StagingKey, 1000);
            decodeAcquired = true;
            decodeContext.CopySubresourceRegion(
                decodeSideNv12, 0, 0, 0, 0, nv12, (uint)arraySlice, new Box(0, 0, 0, Width, Height, 1));
            decodeSideMutex.ReleaseSync(StagingKey);
            decodeAcquired = false;
            FrameCopyMetrics.Record(FrameCopySite.PresenterGpuCopy);

            // (2) OWN DEVICE: acquire the staging texture (this acquire fences the decode-side copy
            // so the shader sees it), acquire the target ring buffer, replay the pre-recorded shader
            // pass into its RTV, then hand the ring buffer to the compositor (release key 1) and
            // re-arm the staging texture. restoreContextState: true isolates the own immediate
            // context's 3D pipeline state around the execute.
            sampleMutex.AcquireSync(StagingKey, 1000);
            sampleAcquired = true;
            buf.KeyedMutex.AcquireSync(0, 1000);
            ringAcquired = true;
            // The video processor when this converter has one; the shader otherwise, or after a
            // blit failure turned the video processor off.
            bool scaled = buf.ProcessorOutput is not null && TryProcessorBlt(buf);
            if (!scaled)
                _immediate.ExecuteCommandList(buf.CommandList, true);
            FrameCopyMetrics.Record(FrameCopySite.PresenterGpuConvert);
            buf.KeyedMutex.ReleaseSync(1);
            ringAcquired = false;
            sampleMutex.ReleaseSync(StagingKey);
            sampleAcquired = false;
        }
        catch (Exception ex) when (D3D11DeviceLoss.IsDeviceLost(ex))
        {
            // Genuine TDR / DEVICE_REMOVED (not the remote-desktop display transition,
            // which never sets a device-loss HRESULT). Mark lost and let the caller drop
            // the ring; do not rethrow into the per-frame present path.
            _deviceLost = true;
            _logger.LogWarning(ex, "GPU device lost during YUV->BGRA convert; dropping ring for rebuild.");
            return false;
        }
        finally
        {
            // Release anything still held, back to a re-armable key, so an aborted frame never
            // strands a mutex the next frame's first acquire would block on. ReleaseSync on a
            // just-lost device can itself throw; swallow it (the loss is already recorded).
            //
            // ringAcquired can only still be true here on the EXCEPTION path: the success path
            // explicitly ReleaseSync(1)'s to hand the buffer to the compositor and then clears the
            // flag. So this release is always the aborted-draw case — re-arm to key 0 (the producer's
            // acquire key), DISCARDING the partial write. Releasing to 1 here would be a bug: the
            // compositor is never told to acquire this buffer (UpdateWithKeyedMutexAsync is upstream,
            // not yet called), so key 1 would strand the slot and every later AcquireSync(0) on it
            // would time out — a permanent ring-slot leak after a non-device-loss GPU fault.
            if (ringAcquired)
                TryReleaseMutex(buf.KeyedMutex, 0); // aborted draw: re-arm for the next producer
            if (sampleAcquired)
                TryReleaseMutex(sampleMutex, StagingKey); // re-arm staging for the next frame
            if (decodeAcquired)
                TryReleaseMutex(decodeSideMutex, StagingKey); // re-arm staging for the next frame
        }

        return true;
    }

    /// <summary>
    /// Scales the staging texture into <paramref name="buffer"/> through the video processor.
    /// Returns <see langword="false"/> after a failure that is not device loss, having turned the
    /// video processor off for good, so the caller fills the buffer with the shader instead. A
    /// device-loss failure throws to the caller's device-loss handling.
    /// </summary>
    private bool TryProcessorBlt(RingBuffer buffer)
    {
        try
        {
            _blitsSubmitted = true; // a failing call may still have queued work
            _videoContext!.VideoProcessorBlt(_videoProcessor!, buffer.ProcessorOutput!, 0, 1, _processorStreams!).CheckError();
            return true;
        }
        catch (Exception ex) when (!D3D11DeviceLoss.IsDeviceLost(ex))
        {
            _logger.LogWarning(ex, "VideoProcessorBlt failed; the shader fills the ring from now on (super resolution off).");
            SuperResolutionFailed = true;
            ReleaseVideoProcessor();
            return false;
        }
    }

    /// <summary>
    /// Waits, for at most <paramref name="timeout"/>, until every <c>VideoProcessorBlt</c> this
    /// converter submitted has completed on the GPU. Returns <see langword="true"/> when none is left
    /// in flight: they completed, none was submitted, or the device is lost. Returns
    /// <see langword="false"/> after the whole timeout when they did not complete in it, or when the
    /// event query failed and completion could not be observed. Never throws. Tests use it to bound
    /// the blits in flight and to see a blit that never completes.
    /// </summary>
    public bool WaitForSubmittedBlits(TimeSpan timeout)
    {
        if (!_blitsSubmitted || _deviceLost || _disposed)
            return true;

        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        try
        {
            using var query = _device.CreateQuery(new QueryDescription(QueryType.Event));
            _immediate.End(query);
            _immediate.Flush();
            while (true)
            {
                var result = _immediate.GetData(query, nint.Zero, 0, AsyncGetDataFlags.None);
                if (result.Code == 0) // S_OK: the event, and every command before it, has completed
                    return true;
                if (result.Failure)
                    result.CheckError(); // to the handlers below
                if (Stopwatch.GetTimestamp() >= deadline)
                    return false;
                Thread.Sleep(1);
            }
        }
        catch (Exception ex) when (D3D11DeviceLoss.IsDeviceLost(ex))
        {
            _deviceLost = true;
            return true;
        }
        catch (Exception ex)
        {
            // Completion cannot be observed, so wait out the bound, as for blits that never finish.
            _logger.LogWarning(ex, "The event query on the video processor's blits failed; waiting out the timeout instead.");
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining > 0)
                Thread.Sleep(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
            return false;
        }
    }

    private void TryReleaseMutex(IDXGIKeyedMutex mutex, ulong key)
    {
        try
        {
            mutex.ReleaseSync(key);
        }
        catch (Exception ex) when (D3D11DeviceLoss.IsDeviceLost(ex))
        {
            _deviceLost = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignored fault releasing a keyed mutex on the convert error path.");
        }
    }

    /// <summary>
    /// Releases the ring textures + shader-pipeline objects, the shareable staging texture, the
    /// decode-device bridge, and the converter's own device.
    /// <para>
    /// <b>Threading contract.</b> The presenter must invoke this <i>off</i> the UI thread
    /// and only <i>after</i> the compositor has released the shared keyed mutex (presents
    /// drained, imported images disposed). Releasing the own device implicitly flushes its
    /// GPU work, which can block in the driver if a wedged compositor is gating the GPU queue
    /// (Mechanism B in the 2026-06-12 investigation) — but the own device's only GPU work is
    /// this converter's shader pass, and the compositor-facing ring is released by the
    /// compositor first, so running the dispose off the UI thread post-drain keeps it safe.
    /// Owning the device (rather than borrowing FFmpeg's) means the decode device is never the
    /// last reference released here — the bridge holds only QI'd references the decoder still
    /// backs. Each native release is wrapped so a concurrent device-loss cannot throw out of
    /// teardown.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Decode-side bridge first — references to the decode device (FFmpeg's), released while
        // the decoder still backs the device so this is not its final release.
        DropDecodeBridge();

        // The video processor's objects, then the rest we created on the own device.
        ReleaseVideoProcessor();
        ReleaseRing(_buffers);
        ReleaseStaging(_staging);
        Release(() => _levels.Dispose());
        Release(() => _rasterizer.Dispose());
        Release(() => _sampler.Dispose());
        Release(() => _pixelShader.Dispose());
        Release(() => _vertexShader.Dispose());
        Release(() => _deferred.Dispose());

        // The own immediate context + device. These releases implicitly flush; they are our
        // device's own GPU work only (no decode coupling) and run off the UI thread after the
        // compositor drained, so they cannot deadlock the UI. Guarded so a device-loss HRESULT
        // here is swallowed rather than escaping teardown.
        Release(() => _immediate.Dispose());
        Release(() => _device.Dispose());
    }

    private void Release(Action dispose)
    {
        try
        {
            dispose();
        }
        catch (Exception ex)
        {
            // A device-loss / already-removed device can fault a native Release; the
            // resource is gone regardless, so log once at debug and continue tearing down.
            _logger.LogDebug(ex, "Ignored fault releasing a D3D11 resource during converter teardown.");
        }
    }
}
