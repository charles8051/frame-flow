using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Who releases a frame the pacer passes to its sink when that fails (#701). The sink owns the
/// frame from <see cref="IVideoSink.PresentAsync"/> on, even when that throws, and the announcer
/// owns it before then, so the pacer releases nothing it has handed to either. Each test stops the
/// pacer before asserting: <see cref="ClockSelectVideoSink.DisposeAsync"/> awaits the delivery
/// loop, so whatever the loop does with the frame on the way out has happened by then.
/// </summary>
public sealed partial class ClockSelectVideoSinkTests
{
    [Fact]
    public async Task ASinkThatReleasesTheFrameAndThrows_IsNotReleasedAgain()
    {
        var sink = new FailingSink
        {
            ReleaseThenThrowOnPresent = new InvalidOperationException("present"),
        };
        var pacer = new ClockSelectVideoSink(sink, new FakeClock(), capacity: 4);
        var frame = new CountedFrame(TimeSpan.Zero);

        await pacer.PresentAsync(frame, default);
        await sink.Called;
        await pacer.DisposeAsync();

        Assert.Equal(0, frame.References);
    }

    [Fact]
    public async Task ASinkCancelledMidPresent_IsNotReleasedAgain()
    {
        // HeadlessVideoSink holds the frame while it pays its present cost, and when the token is
        // cancelled it releases the frame and then throws. The cost runs on a clock nobody
        // advances, so it is still being paid when the pacer stops, and stopping the pacer is the
        // cancellation.
        var headless = new HeadlessVideoSink(TimeSpan.FromMilliseconds(10), new FakeTimeProvider());
        var sink = new EnteredSink(headless);
        var pacer = new ClockSelectVideoSink(sink, new FakeClock(), capacity: 4);
        var frame = new CountedFrame(TimeSpan.Zero);

        await pacer.PresentAsync(frame, default);
        Assert.True(await sink.Entered, "the present finished before the pacer stopped");
        await pacer.DisposeAsync();

        Assert.Equal(1, headless.AbandonedCount);
        Assert.Equal(0, frame.References);
    }

    [Fact]
    public async Task AnAnnouncementThatThrows_ReleasesTheFrameOnce()
    {
        // The frame left the ring and never reached the sink: the announcer releases it, and the
        // pacer does not release it again.
        var sink = new FailingSink
        {
            ThrowOnAnnouncement = new InvalidOperationException("surface"),
        };
        var pacer = new ClockSelectVideoSink(sink, new FakeClock(), capacity: 4);
        var frame = new CountedFrame(TimeSpan.Zero);

        await pacer.PresentAsync(frame, default);
        await sink.Called;
        await pacer.DisposeAsync();

        Assert.Equal(0, sink.Presents);
        Assert.Equal(0, frame.References);
    }

    /// <summary>A sink whose announcement or present fails as the test says.</summary>
    private sealed class FailingSink : IVideoSink
    {
        private readonly TaskCompletionSource _called = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _presents;

        /// <summary>When set, the announcement throws it.</summary>
        public Exception? ThrowOnAnnouncement { get; init; }

        /// <summary>When set, the present releases the frame and then throws it.</summary>
        public Exception? ReleaseThenThrowOnPresent { get; init; }

        /// <summary>Completes on the first call that fails, just before it throws.</summary>
        public Task Called => _called.Task;

        public int Presents => Volatile.Read(ref _presents);

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            Interlocked.Increment(ref _presents);
            if (ReleaseThenThrowOnPresent is { } ex)
            {
                frame.Dispose();
                _called.TrySetResult();
                throw ex;
            }

            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
        {
            if (ThrowOnAnnouncement is { } ex)
            {
                _called.TrySetResult();
                throw ex;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Forwards to a sink, and signals once the first present has been handed to it, with whether
    /// it was still in progress there.
    /// </summary>
    private sealed class EnteredSink(IVideoSink inner) : IVideoSink
    {
        private readonly TaskCompletionSource<bool> _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<bool> Entered => _entered.Task;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            var present = inner.PresentAsync(frame, ct);
            _entered.TrySetResult(!present.IsCompleted);
            return present;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            inner.OnFormatChangedAsync(format, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
