using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FrameFlow.Avalonia.Windows;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0086 decision 3 on <see cref="CompositionInteropVideoView"/>'s work in flight: a producer
/// it drops while presenting is disposed only once the presents that used its ring have drained, a
/// D3D12 converter dropped that way holds back the next attachment as one a teardown took does, the
/// teardown waits for what was dropped, and it waits for a stall check already running.
/// </summary>
/// <remarks>
/// <para>
/// The headless backend has no GPU interop, so it makes no present and no converter. A present's
/// compositor hand-off is a completion source handed to <c>TrackHandOff</c>, as the present hands it
/// the real one, and a producer is a <see cref="RecordingProducer"/> handed to
/// <c>RetireProducers</c>, as the view's converter drops hand it theirs. What runs from there is the
/// view's own: the ring detached, the release built from the hand-offs, the reaper, the gates.
/// </para>
/// <para>
/// The wait's warning runs on a <see cref="FakeTimeProvider"/>. It is raised only while the dropped
/// producer waits on the release, so it is the signal that the view gated the producer on it.
/// </para>
/// </remarks>
public sealed class CompositionInteropVideoViewRetirementTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // SlowWorkWarning.DefaultInterval, which the reaper waits before it says what it waits for.
    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(5);

    [AvaloniaFact]
    public async Task AProducerDroppedWhilePresenting_IsDisposedOnlyOnceThePresentsThatUsedItHaveDrained()
    {
        var time = new FakeTimeProvider();
        var view = new CompositionInteropVideoView { RetirementTime = time };
        var log = new RecordingLoggerFactory();
        var window = Show(view);
        ((IVideoSurface)view).AttachSink(log);
        var handOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.TrackHandOff(0, handOff.Task);
        var producer = new RecordingProducer(handOff.Task);

        var retired = view.RetireProducers(holdsDecoderFrames: false, producer);
        time.Advance(WarningInterval);

        Assert.Equal(1, Count(log, "waits for the compositor to release the ring"));
        Assert.False(producer.Disposed);

        handOff.SetResult();
        await retired.WaitAsync(Bound);

        Assert.True(producer.Disposed);
        Assert.True(producer.DisposedAfterRelease);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AD3D12ConverterDroppedWhilePresenting_HoldsBackTheNextAttachment_UntilItIsDisposed()
    {
        // The interop is what the presents wait on; the headless backend has none, and says so
        // once the attachment stops waiting.
        const string NoInterop = "ICompositionGpuInterop unavailable";
        var view = new CompositionInteropVideoView();
        var log = new RecordingLoggerFactory();
        var window = Show(view);
        ((IVideoSurface)view).AttachSink(log);
        var handOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.TrackHandOff(0, handOff.Task);
        var retired = view.RetireProducers(holdsDecoderFrames: true, new RecordingProducer(handOff.Task));
        Detach(window);
        var before = Count(log, NoInterop);

        Reattach(window, view);

        Assert.Equal(before, Count(log, NoInterop));
        Assert.Equal(1, Count(log, "waits for the previous attachment's D3D12 converter"));

        handOff.SetResult();
        await retired.WaitAsync(Bound);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + 1, Count(log, NoInterop));
        Assert.Equal(1, Count(log, "Presenter resumes"));
        window.Close();
    }

    [AvaloniaFact]
    public void AD3D11ProducerDroppedWhilePresenting_DoesNotHoldBackTheNextAttachment()
    {
        // It holds no decoder frames, so the next attachment has nothing to wait for: it goes on to
        // ask for the interop, which the headless backend reports it lacks.
        const string NoInterop = "ICompositionGpuInterop unavailable";
        var view = new CompositionInteropVideoView();
        var log = new RecordingLoggerFactory();
        var window = Show(view);
        ((IVideoSurface)view).AttachSink(log);
        var handOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.TrackHandOff(0, handOff.Task);
        view.RetireProducers(holdsDecoderFrames: false, new RecordingProducer(handOff.Task));
        Detach(window);
        var before = Count(log, NoInterop);

        Reattach(window, view);

        Assert.Equal(before + 1, Count(log, NoInterop));
        Assert.Equal(0, Count(log, "waits for the previous attachment's D3D12 converter"));
        handOff.SetResult();
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDispose_WaitsWithItsDrain_ForAProducerDroppedWhilePresenting()
    {
        // The hand-off is held past the drain's bound, so the teardown cannot drain cleanly if it
        // waits for the dropped producer: it hands its own to the reaper, and the dropped one is
        // still undisposed when the dispose returns.
        var view = new CompositionInteropVideoView();
        var log = new RecordingLoggerFactory();
        var window = Show(view);
        ((IVideoSurface)view).AttachSink(log);
        var handOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.TrackHandOff(0, handOff.Task);
        var producer = new RecordingProducer(handOff.Task);
        var retired = view.RetireProducers(holdsDecoderFrames: false, producer);

        await view.DisposeAsync();

        Assert.Equal(1, Count(log, "did not drain in-flight presents"));
        Assert.Equal(0, Count(log, "Presenter teardown completed cleanly"));
        Assert.False(producer.Disposed);

        handOff.SetResult();
        await retired.WaitAsync(Bound);
        Assert.True(producer.DisposedAfterRelease);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDispose_WaitsForAStallCheckInFlight()
    {
        var view = new CompositionInteropVideoView();
        using var check = new HeldCheck();
        view.StallSampling = check.Sample;
        var window = Show(view);
        ((IVideoSurface)view).AttachSink(new RecordingLoggerFactory());
        await check.Entered.WaitAsync(Bound);

        var disposing = view.DisposeAsync().AsTask();

        // The watchdog's check is held in the view's sampler, so a teardown that waits for it
        // cannot have ended.
        Assert.False(disposing.IsCompleted);

        check.Release();
        await disposing.WaitAsync(Bound);
        Assert.False(check.InFlight);
        window.Close();
    }

    private static int Count(RecordingLoggerFactory log, string text) =>
        log.MessagesFrom<CompositionInteropVideoView>().Count(m => m.Contains(text, StringComparison.Ordinal));

    private static Window Show(Control view)
    {
        var window = new Window { Width = 200, Height = 100, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Detach(Window window)
    {
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
    }

    private static void Reattach(Window window, Control view)
    {
        window.Content = view;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Holds the stall watchdog's first check of the view, on the timer's thread, until
    /// <see cref="Release"/>. A later one returns at once.
    /// </summary>
    private sealed class HeldCheck : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _inFlight;

        /// <summary>Completes once the first check is held.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Whether a check is in the sampler now.</summary>
        public bool InFlight => Volatile.Read(ref _inFlight) != 0;

        public void Sample()
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    _entered.TrySetResult();

                    // The bound only turns a release that never comes into a failure.
                    _released.Task.Wait(Bound);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void Release() => _released.TrySetResult();

        // Releases a check still held, so a failing test does not leave the timer's thread blocked.
        public void Dispose() => Release();
    }
}
