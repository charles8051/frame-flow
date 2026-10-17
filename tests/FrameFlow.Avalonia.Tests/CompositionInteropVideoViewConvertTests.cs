using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #781 on <see cref="CompositionInteropVideoView"/>: a frame is converted into the ring on the
/// thread pool, and the UI thread only finishes the present once the conversion has run. A frame
/// drawn into the ring is kept as the one presented last, one that was not is released, and a
/// teardown that comes while a conversion runs hands that frame, the newest the view took, back to
/// the sink.
/// </summary>
/// <remarks>
/// The headless backend has no GPU interop, so no present gets as far as a conversion and nothing
/// reaches the compositor. Each test starts the conversion a present would start, with a fill that
/// stands in for the GPU work, and lets the dispatcher run its completion.
/// </remarks>
public sealed class CompositionInteropVideoViewConvertTests
{
    [AvaloniaFact]
    public async Task AFrameDrawnIntoTheRing_IsKeptAsPresented_OnceItsConversionHasRun()
    {
        var (view, window, sink) = ShowWithItsOwnSink();
        var before = new CountedFrame();
        view.KeepPresented(before);
        var frame = new CountedFrame();
        var onUiThread = true;

        await view.ConvertForTest(frame, _ =>
        {
            onUiThread = Dispatcher.UIThread.CheckAccess();
            return true;
        });

        Assert.False(onUiThread);
        Assert.Equal(0, before.References);
        Assert.Equal(1, frame.References);
        Assert.NotNull(view.LastConvertDuration);
        await view.DisposeAsync();
        Assert.Equal(0, frame.References);
        Assert.False(sink.HasPendingFrame);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AFrameNotDrawn_IsReleased_AndTheFramePresentedBeforeStays()
    {
        // A fill that observes device loss draws nothing; the next present rebuilds the converter.
        var (view, window, _) = ShowWithItsOwnSink();
        var before = new CountedFrame();
        view.KeepPresented(before);
        var frame = new CountedFrame();

        await view.ConvertForTest(frame, _ => false);

        Assert.Equal(0, frame.References);
        Assert.Equal(1, before.References);
        await view.DisposeAsync();
        Assert.Equal(0, before.References);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AFillThatThrows_ReleasesItsFrame()
    {
        var (view, window, _) = ShowWithItsOwnSink();
        var frame = new CountedFrame();

        await view.ConvertForTest(frame, _ => throw new InvalidOperationException("the fill failed"));

        Assert.Equal(0, frame.References);
        await view.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task ATeardownDuringAConversion_HandsItsFrameBackToTheSink()
    {
        var (view, window, sink) = ShowWithItsOwnSink();
        var player = ((IClaimableSink)sink).TryClaim("a paused player")!;
        var before = new CountedFrame();
        view.KeepPresented(before);
        var converting = new CountedFrame();
        using var filling = new ManualResetEventSlim();
        var release = new TaskCompletionSource();

        var completed = view.ConvertForTest(converting, _ =>
        {
            filling.Set();
            release.Task.Wait();
            return true;
        });
        Assert.True(filling.Wait(TimeSpan.FromSeconds(30)));

        // The view leaves the tree while the conversion runs: the frame it converts is the newest
        // it took, so the sink takes that one back, not the one presented before.
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, before.References);
        Assert.Equal(2, converting.References); // the sink's, and the conversion's own

        release.SetResult();
        await completed;

        Assert.Equal(1, converting.References); // the conversion let go of its own; it presents nothing
        Assert.NotNull(view.LastConvertDuration); // and was timed
        Assert.Same(converting, sink.TakePendingFrame());
        converting.Dispose();
        player.Dispose();
        await view.DisposeAsync();
        window.Close();
    }

    private static (CompositionInteropVideoView View, Window Window, CompositionInteropVideoSink Sink) ShowWithItsOwnSink()
    {
        var view = new CompositionInteropVideoView();
        var window = new Window { Width = 200, Height = 100, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var sink = (CompositionInteropVideoSink)((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        return (view, window, sink);
    }

    /// <summary>A frame that counts its references, so a test sees the last one released.</summary>
    private sealed class CountedFrame : IVideoFrame
    {
        private int _references = 1;

        public int References => Volatile.Read(ref _references);

        public int Width => 16;
        public int Height => 16;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef()
        {
            Interlocked.Increment(ref _references);
            return this;
        }

        public void Dispose() => Interlocked.Decrement(ref _references);

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
