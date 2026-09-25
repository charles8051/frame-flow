using FrameFlow.Graph;
using Xunit;

namespace FrameFlow.Media.Tests;

/// <summary>
/// What a video sink declares it keeps, and what its graph node holds as a result (ADR-0081).
/// </summary>
public sealed class SinkHoldingTests
{
    [Fact]
    public void ASinkThatDoesNotSay_HoldsWithoutBound()
    {
        IVideoSink sink = new UndeclaredSink();

        Assert.Null(sink.MaxHeldFrames);
        Assert.Same(Holding.Unbounded, sink.AsSinkNode().Holding);
    }

    [Fact]
    public void ASinksNode_HoldsTheFrameInTheCallAndWhatTheSinkKeeps()
    {
        var node = new DeclaringSink(2).AsSinkNode();

        Assert.Equal(3, node.Holding.MaxHeld);
    }

    [Fact]
    public async Task SinksThatReleaseInTheCall_KeepNothing()
    {
        await using var headless = new HeadlessVideoSink();

        Assert.Equal(0, new NullVideoSink().MaxHeldFrames);
        Assert.Equal(0, headless.MaxHeldFrames);
        Assert.Equal(1, new NullVideoSink().AsSinkNode().Holding.MaxHeld);
    }

    private class UndeclaredSink : IVideoSink
    {
        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeclaringSink(int kept) : UndeclaredSink, IVideoSink
    {
        public int? MaxHeldFrames => kept;
    }
}
