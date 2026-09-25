using FrameFlow.Graph;
using FrameFlow.Media;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// What the player's own holders declare (ADR-0081): the pacer, the gate and the pace node.
/// Time does not count, so a holder that waits without limit while paused still declares a
/// count.
/// </summary>
public sealed class HoldingTests
{
    [Fact]
    public async Task ThePacer_HoldsItsRing_TheFrameOnItsWayOut_AndWhatItsInnerSinkKeeps()
    {
        await using var pacer = new ClockSelectVideoSink(new Sink(kept: 1), new StoppedClock(), capacity: 4);

        Assert.Equal(6, pacer.MaxHeldFrames);
    }

    [Fact]
    public async Task ThePacer_OverASinkThatDoesNotSay_HoldsWithoutBound()
    {
        await using var pacer = new ClockSelectVideoSink(new Sink(kept: null), new StoppedClock());

        Assert.Null(pacer.MaxHeldFrames);
    }

    [Fact]
    public void TheGateAndThePaceNode_HoldOneItemForAsLongAsTheyWait()
    {
        Assert.Same(FrameHolding.InFlight, new PausableGate<RefBox<int>>().AsOperator("gate").Holding);
        Assert.Same(
            FrameHolding.InFlight,
            PaceUntil.Create<RefBox<int>>("pace", new StoppedClock(), _ => TimeSpan.Zero).Holding
        );
    }

    private sealed class Sink(int? kept) : IVideoSink
    {
        public int? MaxHeldFrames => kept;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A clock that never moves, for tests that present nothing.</summary>
    private sealed class StoppedClock : IClockSource
    {
        public TimeSpan Latest => TimeSpan.Zero;

        public ValueTask WaitUntilAsync(TimeSpan target, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken));
    }
}
