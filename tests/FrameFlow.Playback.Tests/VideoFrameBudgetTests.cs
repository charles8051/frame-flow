using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Video;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The frame budget of the player's video path (ADR-0081, decision 3), computed by the session
/// on the path it wires, before a decoder exists. The expected sums are counted out by hand in
/// each test.
/// </summary>
public sealed class VideoFrameBudgetTests
{
    [Fact]
    public void ThePlayersOwnPath_OverTheD3D11Sink()
    {
        // Pump 1; edge 1; gate 1; edge 1; sink node: the call 1 and the pacer, which is its
        // ring 3, the frame going out 1 and the D3D11 sink's 2.
        var budget = SubstrateSession.VideoFrameBudget(configurator: null, new Sink(kept: 2));

        Assert.Equal(11, budget.Frames);
    }

    [Fact]
    public void ThePlayersOwnPath_OverASinkThatDoesNotSay_IsUnbounded()
    {
        var budget = SubstrateSession.VideoFrameBudget(configurator: null, new Sink(kept: null));

        Assert.Null(budget.Frames);
        Assert.Equal("video-sink", budget.UnboundedHolder);
    }

    /// <summary>
    /// The LiveCaptioning example in GPU mode: a detection branch off the trunk that reads the
    /// GPU frame back, runs YOLO and rejoins the display path as the join's secondary.
    /// </summary>
    [Fact]
    public void LiveCaptioningInGpuMode()
    {
        var budget = SubstrateSession.VideoFrameBudget(LiveCaptioningGpu(declared: true), new Sink(kept: 2));

        // Pump 1, edge 1, gate 1.
        // Trunk: edge 1 into the join's primary, which holds 1.
        // Branch: LatestWins(1) edge 1, gpu-frames-only 1, edge 1, the readback 1, edge 1, and
        // the detector 1, which emits detections rather than frames, so the path ends there.
        // Join output: edge 1, sink node 7.
        Assert.Equal(19, budget.Frames);
    }

    [Fact]
    public void LiveCaptioningInGpuMode_WithAnUndeclaredNode_NamesIt()
    {
        var budget = SubstrateSession.VideoFrameBudget(LiveCaptioningGpu(declared: false), new Sink(kept: 2));

        Assert.Null(budget.Frames);
        Assert.Equal("gpu-frames-only", budget.UnboundedHolder);
    }

    /// <summary>
    /// The configurator in <c>examples/FrameFlow.Examples.LiveCaptioning</c>, GPU mode with a
    /// detector, with its own nodes declared as the example declares them.
    /// </summary>
    private static Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>> LiveCaptioningGpu(bool declared) =>
        head =>
        {
            var detections = head.Branch(EdgeOptions.LatestWins(1))
                .Then(
                    new OperatorNode<IVideoFrame, IVideoFrame>(
                        "gpu-frames-only",
                        (frame, _) => ValueTask.FromResult<IVideoFrame?>(frame),
                        holding: declared ? Holding.InFlight : null
                    )
                )
                .Then(VideoOperators.ToCpu("inference-readback"))
                .Then(
                    new OperatorNode<IVideoFrame, RefBox<TimeSpan>>(
                        "yolo-detect",
                        (frame, _) => ValueTask.FromResult<RefBox<TimeSpan>?>(RefBox.Of(frame.Pts)),
                        holding: Holding.Boundary
                    )
                );

            return head.Join(
                detections,
                new SyncJoinNode<IVideoFrame, RefBox<TimeSpan>, IVideoFrame>(
                    "detection-overlay",
                    (frame, _, _) => ValueTask.FromResult<IVideoFrame?>(frame),
                    new SyncJoinKeys<IVideoFrame, RefBox<TimeSpan>>(f => f.Pts, d => (d.Value, d.Value)),
                    SyncMatch.MostRecentAtOrBefore,
                    window: TimeSpan.FromSeconds(2),
                    maxStaleness: TimeSpan.FromSeconds(2)
                ),
                EdgeOptions.Default,
                EdgeOptions.Buffered(4)
            );
        };

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
}
