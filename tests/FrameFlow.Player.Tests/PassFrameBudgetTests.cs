using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Player.Tests;

/// <summary>
/// A pass's video budget is computed on the path it runs, before its decoder opens, so the
/// decoder's pool can be sized for it (#292). Pure: a stand-in source and no FFmpeg.
/// </summary>
public sealed class PassFrameBudgetTests
{
    [Fact]
    public void ThePathWithNoConfigurator_HoldsThePumpItemTheEdgeAndWhatTheSinkKeeps()
    {
        // The pump's item, the edge's one, and the sink's call plus the two it keeps.
        Assert.Equal(5, MediaPass.VideoFrameBudget(null, new Sink(maxHeldFrames: 2)).Frames);
    }

    [Fact]
    public void TheConfiguratorsNodes_AreCounted()
    {
        var budget = MediaPass.VideoFrameBudget(
            chain => chain.Then(Operator("scale", FrameHolding.AtMost(1))),
            new Sink(maxHeldFrames: 2)
        );

        // The pump's item, two edges, the operator's one and the sink's three.
        Assert.Equal(7, budget.Frames);
    }

    [Fact]
    public void AnUndeclaredNode_LeavesThePathUnbounded_AndIsNamed()
    {
        var budget = MediaPass.VideoFrameBudget(
            chain => chain.Then(Operator("undeclared", holding: null)),
            new Sink(maxHeldFrames: 2)
        );

        Assert.Null(budget.Frames);
        Assert.Equal("undeclared", budget.UnboundedHolder);
    }

    [Fact]
    public void ASinkThatDeclaresNothing_LeavesThePathUnbounded()
    {
        var budget = MediaPass.VideoFrameBudget(null, new Sink(maxHeldFrames: null));

        Assert.Null(budget.Frames);
        Assert.Equal("video-sink", budget.UnboundedHolder);
    }

    private static OperatorNode<IVideoFrame, IVideoFrame> Operator(string id, FrameHolding? holding) =>
        new(id, (frame, _) => ValueTask.FromResult<IVideoFrame?>(frame), holding: holding);

    private sealed class Sink(int? maxHeldFrames) : IVideoSink
    {
        public int? MaxHeldFrames => maxHeldFrames;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
