using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Pins the <see cref="GpuVideoFrame"/> ref-counting contract that makes
/// hardware-frame fan-out (multi-pane / multicast) possible (ADR-0038).
/// A GPU frame must behave like every other refcounted frame/buffer in the
/// codebase: <see cref="GpuVideoFrame.AddRef"/> returns the <i>same</i>
/// instance and bumps an atomic count; the wrapped <c>AVFrame</c> (and the
/// D3D11VA decode-texture slice it pins) survives until the final matching
/// <see cref="GpuVideoFrame.Dispose"/>.
/// </summary>
/// <remarks>
/// Uses a bare <c>av_frame_alloc</c>'d frame (no device buffers) cloned into
/// a <see cref="GpuVideoFrame"/> — the ref-count machinery is independent of
/// the pixel payload, so no real hwaccel decode is needed. Liveness is
/// observed through the internal <see cref="GpuVideoFrame.NativeAvFrame"/>
/// pointer, which goes to <c>nint.Zero</c> exactly when the final release
/// frees the handle. Requires FFmpeg shared libraries for the alloc/free
/// P/Invokes; skips cleanly without them.
/// </remarks>
public sealed class GpuVideoFrameRefCountTests : IClassFixture<FfmpegBootstrapFixture>
{
    public GpuVideoFrameRefCountTests(FfmpegBootstrapFixture _) { }

    /// <summary>
    /// Mints a <see cref="GpuVideoFrame"/> at ref count 1 that owns a bare
    /// <c>av_frame_alloc</c>'d frame. The ref-count machinery is independent of
    /// the pixel payload, so an empty (buffer-less) AVFrame is sufficient — and
    /// it frees cleanly at the final release. (A bare frame can't be
    /// <c>av_frame_clone</c>'d, which is why the test adopts it directly via
    /// <see cref="GpuVideoFrame.FromOwnedAvFrame"/> rather than going through
    /// <c>CloneFrom</c>.)
    /// </summary>
    private static GpuVideoFrame NewFrame()
    {
        nint src = FFAvUtil.av_frame_alloc();
        Assert.NotEqual(nint.Zero, src);
        return GpuVideoFrame.FromOwnedAvFrame(
            src,
            width: 1920,
            height: 1080,
            softwareFormat: PixelFormat.Nv12,
            pts: TimeSpan.Zero,
            duration: TimeSpan.FromMilliseconds(33),
            backend: HardwareDecodeBackendKind.D3D11Va
        );
    }

    [RequiresFfmpegFact]
    public void AddRef_ReturnsSameInstance()
    {
        var frame = NewFrame();

        IVideoFrame bumped = frame.AddRef();

        // The codebase-wide contract: AddRef hands back THIS frame, not a
        // wrapper or a clone. The graph's fan-out relies on reference equality.
        Assert.Same(frame, bumped);

        frame.Dispose(); // 2 -> 1
        frame.Dispose(); // 1 -> 0 (freed)
    }

    [RequiresFfmpegFact]
    public void Frame_StaysAlive_UntilFinalRelease()
    {
        var frame = NewFrame();
        Assert.NotEqual(nint.Zero, frame.NativeAvFrame); // refcount 1 — alive

        var a = frame.AddRef(); // 2
        var b = frame.AddRef(); // 3
        Assert.NotEqual(nint.Zero, frame.NativeAvFrame);

        frame.Dispose(); // 3 -> 2
        Assert.NotEqual(nint.Zero, frame.NativeAvFrame); // a still holds a ref

        a.Dispose(); // 2 -> 1
        Assert.NotEqual(nint.Zero, frame.NativeAvFrame); // b still holds a ref

        b.Dispose(); // 1 -> 0 — final release frees the AVFrame
        Assert.Equal(nint.Zero, frame.NativeAvFrame);
    }

    [RequiresFfmpegFact]
    public void OverDispose_IsNoOp()
    {
        var frame = NewFrame();

        frame.Dispose(); // 1 -> 0 (freed)
        Assert.Equal(nint.Zero, frame.NativeAvFrame);

        // Disposing again past zero must not throw or double-free.
        frame.Dispose();
        frame.Dispose();
        Assert.Equal(nint.Zero, frame.NativeAvFrame);
    }

    [RequiresFfmpegFact]
    public void AddRef_AfterFinalRelease_Throws()
    {
        var frame = NewFrame();
        frame.Dispose(); // 1 -> 0 (freed)

        Assert.Throws<ObjectDisposedException>(() => frame.AddRef());
    }

    [RequiresFfmpegFact]
    public void ConcurrentAddRefDispose_BalancesToZero_FreesExactlyOnce()
    {
        var frame = NewFrame(); // owner ref = 1, held for the whole parallel phase

        const int workers = 8;
        const int iterations = 2000;

        // Each worker repeatedly AddRefs then Disposes — net zero per worker.
        // Because the owner ref is never released here, the count never reaches
        // zero mid-flight, so the frame must never be freed during the loop.
        Parallel.For(0, workers, _ =>
        {
            for (int i = 0; i < iterations; i++)
            {
                IVideoFrame r = frame.AddRef();
                Assert.Same(frame, r);
                r.Dispose();
            }
        });

        // Owner ref survived every concurrent add/release pair.
        Assert.NotEqual(nint.Zero, frame.NativeAvFrame);

        frame.Dispose(); // final owner release -> freed exactly once
        Assert.Equal(nint.Zero, frame.NativeAvFrame);
    }
}
