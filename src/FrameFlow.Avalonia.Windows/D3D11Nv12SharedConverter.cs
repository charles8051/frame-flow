// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// Bridges an FFmpeg D3D11VA NV12 decode texture to a <b>ring of shared,
/// keyed-mutex BGRA textures</b> that Avalonia's compositor can import. Color-converts
/// NV12 → BGRA on the GPU with an <b>HLSL pixel shader on the general 3D pipeline</b>
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
/// texture (<see cref="_sampleNv12"/>, <see cref="ResourceOptionFlags.SharedKeyedMutex"/>) on
/// its own device, and opens it <i>by shared handle on the current decode device</i>
/// (<see cref="_decodeSideNv12"/>). Each frame the chosen decode array slice is
/// <c>CopySubresourceRegion</c>'d into that decode-side handle — a cheap same-device
/// copy-engine blit on the decoder's <c>ID3D11Multithread</c>-protected immediate context
/// (the serialization-with-decode the old <c>VideoProcessorBlt</c> relied on) — under the
/// staging texture's keyed mutex, which fences the write so the own device sees it. The
/// shader then samples <see cref="_sampleNv12"/> on the own device via two SRVs: the Y plane
/// as <see cref="Format.R8_UNorm"/> and the interleaved UV plane as
/// <see cref="Format.R8G8_UNorm"/>. (Making the decoder emit shareable shader-readable
/// textures to drop this copy is a deferred follow-up — investigation 2026-06-12 §6 step 5.)
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
        Texture2D    LumaTex   : register(t0);   // R8_UNORM   view of the NV12 Y plane
        Texture2D    ChromaTex : register(t1);   // R8G8_UNORM view of the NV12 UV plane
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

            // Studio (limited) range bias + scale: Y in [16,235], CbCr in [16,240] over 8-bit.
            float Y = (yX     - 16.0  / 255.0) * (255.0 / 219.0);
            float U = (cbcr.x - 128.0 / 255.0) * (255.0 / 224.0);
            float V = (cbcr.y - 128.0 / 255.0) * (255.0 / 224.0);

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

    // Keyed-mutex key for the cross-device NV12 staging texture (_sampleNv12). Unlike the
    // BGRA ring's 0→1 producer→compositor ping-pong, the staging texture's two users — the
    // decode device (writes the copy) and the own device (samples it) — run strictly
    // sequenced inside a single ConvertInto call, so one key suffices: each side acquires it,
    // does its work, and releases back to the same key, leaving the mutex re-armed for the
    // next frame. The acquire on the own side still inserts the cross-device GPU fence that
    // makes the decode-side copy visible. Using one key (not a 0/1 hand-off) means an aborted
    // frame can never strand the mutex at a key the next frame's first acquire would block on.
    private const ulong StagingKey = 0;

    private readonly ILogger _logger;

    // ── The converter's OWN device (ADR-0064 Decision 2) — stable across decode-device swaps ──
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _immediate; // own immediate context (sample + draw)
    private readonly ID3D11DeviceContext _deferred; // records the shader-pass command lists
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11Texture2D _sampleNv12; // SHAREABLE shader-readable copy target for the decode slice
    private readonly IDXGIKeyedMutex _sampleMutex; // own-device view of _sampleNv12's keyed mutex
    private readonly nint _sampleSharedHandle; // global shared handle, opened on each decode device
    private readonly ID3D11ShaderResourceView _srvY; // R8_UNORM   luma view of _sampleNv12
    private readonly ID3D11ShaderResourceView _srvUV; // R8G8_UNORM chroma view of _sampleNv12
    private readonly RingBuffer[] _buffers;

    // ── Per-decode-device bridge — rebound (not rebuilt) when the decode device changes ──
    // Independent QI'd references to the CURRENT decode device + its multithread-protected
    // immediate context, plus _sampleNv12 opened by shared handle on that device (the copy
    // destination). All four are released + reopened by RebindDecodeBridge on a device change.
    private ID3D11Device? _decodeDevice;
    private ID3D11DeviceContext? _decodeContext;
    private ID3D11Texture2D? _decodeSideNv12;
    private IDXGIKeyedMutex? _decodeSideMutex;
    private nint _boundDecodeDevicePointer;

    private bool _disposed;
    private bool _deviceLost;

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// The native <c>ID3D11Device*</c> of the decode device this converter is <b>currently
    /// bound to</b> (the device its per-frame cross-device copy targets). Updated whenever
    /// <see cref="TryRebindDecodeDevice"/> rebinds the bridge to a new decode device. Exposed
    /// so the presenter can compare it against an incoming frame's device identity
    /// (<see cref="GpuVideoFrame.TryGetD3D11Texture"/>'s <c>device</c> out): when they differ,
    /// a warm-sink player swap brought a new decode device and the converter must rebind its
    /// decode bridge (ADR-0064). While bound, the converter holds COM references to this
    /// device (via <see cref="_decodeSideNv12"/> / <see cref="_decodeContext"/>), so its
    /// pointer cannot be reused by a new device — the comparison is reuse-safe.
    /// </summary>
    public nint SourceDevicePointer => _boundDecodeDevicePointer;

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
    }

    public D3D11Nv12SharedConverter(nint nv12TexturePtr, int width, int height, ILogger logger)
    {
        _logger = logger;
        Width = width;
        Height = height;

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

        // OWN DEVICE on the decoder's adapter. DriverType.Unknown is required when an explicit
        // adapter is supplied. BgraSupport so the compositor can import our B8G8R8A8 ring.
        using (var dxgiDevice = decodeDevice.QueryInterface<IDXGIDevice>())
        {
            dxgiDevice.GetAdapter(out var adapter).CheckError();
            using (adapter)
            {
                D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    null,
                    out _device!
                ).CheckError();
            }
        }
        // The own device lives for the converter's lifetime, so its cached immediate-context
        // wrapper is valid for that lifetime — store it directly (the OWNERSHIP GOTCHA that
        // forced QI'd references in the old borrowed-device design does not apply to a device
        // WE own and keep). Mirrors D3D11BgraUploader, which also owns its device + context.
        _immediate = _device.ImmediateContext;

        // ── Shader pipeline objects (shared across all ring buffers) on the own device ──
        _vertexShader = _device.CreateVertexShader(CompileShader("VSMain", "vs_4_0"), null);
        _pixelShader = _device.CreatePixelShader(CompileShader("PSMain", "ps_4_0"), null);

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

        // SHAREABLE private shader-readable NV12 the decode slice is copied into each frame.
        // SharedKeyedMutex makes it openable by handle on the (changing) decode device so the
        // copy can run there; BindFlags.ShaderResource lets the own device's shader sample it.
        _sampleNv12 = _device.CreateTexture2D(
            new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = srcFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
            }
        );
        _sampleMutex = _sampleNv12.QueryInterface<IDXGIKeyedMutex>();
        using (var dxgi = _sampleNv12.QueryInterface<IDXGIResource>())
            _sampleSharedHandle = dxgi.SharedHandle;

        _srvY = _device.CreateShaderResourceView(
            _sampleNv12,
            new ShaderResourceViewDescription
            {
                Format = Format.R8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
            }
        );
        _srvUV = _device.CreateShaderResourceView(
            _sampleNv12,
            new ShaderResourceViewDescription
            {
                Format = Format.R8G8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
            }
        );

        _deferred = _device.CreateDeferredContext();

        _buffers = new RingBuffer[BufferCount];
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

            nint handle;
            using (var dxgi = tex.QueryInterface<IDXGIResource>())
                handle = dxgi.SharedHandle;

            var rtv = _device.CreateRenderTargetView(tex, null);

            _buffers[i] = new RingBuffer
            {
                Texture = tex,
                RenderTargetView = rtv,
                CommandList = RecordConvertCommandList(rtv),
                KeyedMutex = tex.QueryInterface<IDXGIKeyedMutex>(),
                SharedHandle = handle,
            };
        }

        // Seed the decode-device bridge for the first frame's decode device. After this the
        // converter is ready; subsequent frames on the same device reuse it, and a different
        // device triggers a rebind (not a rebuild).
        RebindDecodeBridge(decodeDevice);

        _logger.LogInformation(
            "D3D11 NV12->BGRA shader converter ready (own device, ADR-0064): {W}x{H}, {N}-buffer shared "
                + "keyed-mutex ring; bound to decode device 0x{Dev:X} via a shareable NV12 staging bridge "
                + "(pixel-shader convert on the 3D pipeline, no VideoProcessorBlt).",
            width, height, BufferCount, _boundDecodeDevicePointer
        );
    }

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
    /// <see cref="_sampleNv12"/>, sampler, rasterizer, viewport) with <paramref name="rtv"/>
    /// as the render target and draw the fullscreen triangle. Only the destination RTV varies
    /// per buffer (the SRVs are constant — they always view <see cref="_sampleNv12"/>, which
    /// <see cref="ConvertInto"/> re-copies each frame), so recording once at construction and
    /// replaying with <c>ExecuteCommandList</c> keeps the per-frame path allocation-free.
    /// </summary>
    private ID3D11CommandList RecordConvertCommandList(ID3D11RenderTargetView rtv)
    {
        _deferred.IASetInputLayout(null); // no vertex buffers — SV_VertexID synthesizes the triangle
        _deferred.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _deferred.VSSetShader(_vertexShader);
        _deferred.PSSetShader(_pixelShader);
        _deferred.PSSetShaderResources(0, new[] { _srvY, _srvUV });
        _deferred.PSSetSampler(0, _sampler);
        _deferred.RSSetState(_rasterizer);
        _deferred.RSSetViewport(new Viewport(0, 0, Width, Height, 0.0f, 1.0f));
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
        if (devicePtr == _boundDecodeDevicePointer && _decodeSideNv12 is not null)
            return true;

        try
        {
            RebindDecodeBridge(nv12.Device);
            _logger.LogInformation(
                "Converter rebound its decode bridge to device 0x{Dev:X} (warm-sink player swap, ADR-0064); "
                    + "ring + compositor imports kept warm, no converter rebuild.",
                _boundDecodeDevicePointer
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
            ReleaseDecodeBridge();
            return false;
        }
    }

    /// <summary>
    /// Releases the current decode-side bridge and opens a fresh one on <paramref name="decodeDevice"/>:
    /// independent QI'd references to the device + its <c>ID3D11Multithread</c>-protected immediate
    /// context, and <see cref="_sampleNv12"/> opened by its global shared handle (the per-frame copy
    /// destination) plus that handle's decode-side keyed mutex.
    /// </summary>
    private void RebindDecodeBridge(ID3D11Device decodeDevice)
    {
        ReleaseDecodeBridge();

        // Independent references we own and release ourselves. The decode-device wrapper handed
        // in is FFmpeg's (cached on the frame texture); QI gives us a ref whose lifetime we
        // control, which also pins the device so SourceDevicePointer stays reuse-safe while bound.
        _decodeDevice = decodeDevice.QueryInterface<ID3D11Device>();
        _decodeContext = _decodeDevice.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        _decodeSideNv12 = _decodeDevice.OpenSharedResource<ID3D11Texture2D>(_sampleSharedHandle);
        _decodeSideMutex = _decodeSideNv12.QueryInterface<IDXGIKeyedMutex>();
        _boundDecodeDevicePointer = _decodeDevice.NativePointer;
    }

    private void ReleaseDecodeBridge()
    {
        Release(() => _decodeSideMutex?.Dispose());
        Release(() => _decodeSideNv12?.Dispose());
        Release(() => _decodeContext?.Dispose());
        Release(() => _decodeDevice?.Dispose());
        _decodeSideMutex = null;
        _decodeSideNv12 = null;
        _decodeContext = null;
        _decodeDevice = null;
        _boundDecodeDevicePointer = nint.Zero;
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

        var decodeContext = _decodeContext!;
        var decodeSideNv12 = _decodeSideNv12!;
        var decodeSideMutex = _decodeSideMutex!;

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

            // (2) OWN DEVICE: acquire the staging texture (this acquire fences the decode-side copy
            // so the shader sees it), acquire the target ring buffer, replay the pre-recorded shader
            // pass into its RTV, then hand the ring buffer to the compositor (release key 1) and
            // re-arm the staging texture. restoreContextState: true isolates the own immediate
            // context's 3D pipeline state around the execute.
            _sampleMutex.AcquireSync(StagingKey, 1000);
            sampleAcquired = true;
            buf.KeyedMutex.AcquireSync(0, 1000);
            ringAcquired = true;
            _immediate.ExecuteCommandList(buf.CommandList, true);
            buf.KeyedMutex.ReleaseSync(1);
            ringAcquired = false;
            _sampleMutex.ReleaseSync(StagingKey);
            sampleAcquired = false;
        }
        catch (Exception ex) when (D3D11DeviceLoss.IsDeviceLost(ex))
        {
            // Genuine TDR / DEVICE_REMOVED (not the remote-desktop display transition,
            // which never sets a device-loss HRESULT). Mark lost and let the caller drop
            // the ring; do not rethrow into the per-frame present path.
            _deviceLost = true;
            _logger.LogWarning(ex, "GPU device lost during NV12->BGRA convert; dropping ring for rebuild.");
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
                TryReleaseMutex(_sampleMutex, StagingKey); // re-arm staging for the next frame
            if (decodeAcquired)
                TryReleaseMutex(decodeSideMutex, StagingKey); // re-arm staging for the next frame
        }

        return true;
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
        ReleaseDecodeBridge();

        // Objects we created on the own device.
        foreach (var b in _buffers)
        {
            if (b is null)
                continue;
            Release(() => b.CommandList.Dispose());
            Release(() => b.RenderTargetView.Dispose());
            Release(() => b.KeyedMutex.Dispose());
            Release(() => b.Texture.Dispose());
        }
        Release(() => _srvUV.Dispose());
        Release(() => _srvY.Dispose());
        Release(() => _sampleMutex.Dispose());
        Release(() => _sampleNv12.Dispose());
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
