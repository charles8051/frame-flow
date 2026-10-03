using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback.Core;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Whether a video decoder keeps hardware frames on the GPU (#294): the decision on its own, then
/// on the player's own path, whose gate and pacer have to say they take GPU frames for any sink
/// to get them.
/// </summary>
public sealed class HardwareFrameChoiceTests
{
    private static readonly FrameDomainMismatch Undeclared =
        new("tag", FrameMemoryDomains.Gpu, FrameMemoryDomains.Cpu);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARequest_WinsOverWhatThePathSays(bool requested)
    {
        var decision = HardwareFrameChoice.Decide(
            requested, hardwareDecodeDisabled: false, Undeclared, FrameBudget.Unbounded("video-sink"));

        Assert.Equal(requested, decision.Yield);
    }

    [Fact]
    public void Unasked_FramesStayOnTheGpu_WhenEveryNodeTakesThem_AndThePathIsBounded()
    {
        var decision = HardwareFrameChoice.Decide(null, false, undeclared: null, FrameBudget.Of(8));

        Assert.True(decision.Yield);
    }

    [Fact]
    public void Unasked_ANodeThatHasNotSaid_DownloadsThem_AndIsNamed()
    {
        var decision = HardwareFrameChoice.Decide(null, false, Undeclared, FrameBudget.Of(8));

        Assert.False(decision.Yield);
        Assert.Contains("'tag'", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unasked_AnUnboundedPath_DownloadsThem_AndNamesTheHolder()
    {
        var decision = HardwareFrameChoice.Decide(null, false, undeclared: null, FrameBudget.Unbounded("video-sink"));

        Assert.False(decision.Yield);
        Assert.Contains("'video-sink'", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unasked_WithHardwareDecodeDisabled_DownloadsThem()
    {
        Assert.False(HardwareFrameChoice.Decide(null, hardwareDecodeDisabled: true, null, FrameBudget.Of(8)).Yield);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void APathCopy_IsBuilt_ToDecideOrToSizeARequestedPool(bool? requested, bool disabled, bool needed)
    {
        Assert.Equal(needed, HardwareFrameChoice.NeedsProbe(requested, disabled));
    }

    [Fact]
    public void ThePlayersOwnPath_OverASinkThatTakesGpuFrames_KeepsThem()
    {
        var (graph, source) = SubstrateSession.VideoProbe(configurator: null, new Sink(FrameMemoryDomains.Any));

        Assert.Null(graph!.FrameDomainMismatchFor(source!, FrameMemoryDomains.Any, FrameDomainRule.CpuOnly));
    }

    [Fact]
    public void ThePlayersOwnPath_OverASinkThatSaysNothing_NamesTheSink()
    {
        var (graph, source) = SubstrateSession.VideoProbe(configurator: null, new Sink(accepts: null));

        Assert.Equal(
            "video-sink",
            graph!.FrameDomainMismatchFor(source!, FrameMemoryDomains.Any, FrameDomainRule.CpuOnly)?.Node);
    }

    [Fact]
    public void ThePlayersOwnPath_WithAnUndeclaredNode_NamesIt()
    {
        var (graph, source) = SubstrateSession.VideoProbe(
            chain => chain.Then(new OperatorNode<IVideoFrame, IVideoFrame>(
                "tag", (frame, _) => ValueTask.FromResult<IVideoFrame?>(frame), holding: FrameHolding.InFlight)),
            new Sink(FrameMemoryDomains.Any));

        Assert.Equal(
            "tag",
            graph!.FrameDomainMismatchFor(source!, FrameMemoryDomains.Any, FrameDomainRule.CpuOnly)?.Node);
    }

    private const FrameMemoryDomains ReadsD3D =
        FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11 | FrameMemoryDomains.D3D12;

    private static readonly HardwareFrameDecision OnTheGpu = new(true, "every node on the video path takes GPU frames");

    private static readonly FrameDomainMismatch RefusesCuda = new("video-sink", FrameMemoryDomains.Cuda, ReadsD3D);

    [Fact]
    public void ABackendThePathDoesNotTake_IsDownloaded_NamingTheBackendAndTheNode()
    {
        var decision = HardwareFrameChoice.ForBackend(OnTheGpu, requested: null, HardwareDecodeBackendKind.Cuda, RefusesCuda);

        Assert.False(decision.Yield);
        Assert.Equal("the decoder bound Cuda, and 'video-sink' does not take its frames", decision.Reason);
    }

    [Fact]
    public void ABackendThePathTakes_KeepsTheDecision()
    {
        Assert.Equal(OnTheGpu, HardwareFrameChoice.ForBackend(OnTheGpu, null, HardwareDecodeBackendKind.D3D11Va, refusal: null));
    }

    [Fact]
    public void ARequest_IsNotNarrowed_SoThePathCheckRefusesIt()
    {
        var requested = new HardwareFrameDecision(true, "WithHardwareFrames(true)");

        Assert.Equal(requested, HardwareFrameChoice.ForBackend(requested, true, HardwareDecodeBackendKind.Cuda, RefusesCuda));
    }

    [Fact]
    public void ADownload_OrASoftwareDecode_KeepsTheDecision()
    {
        var download = new HardwareFrameDecision(false, "'tag' has not said it takes GPU frames");

        Assert.Equal(download, HardwareFrameChoice.ForBackend(download, null, HardwareDecodeBackendKind.Cuda, RefusesCuda));
        Assert.Equal(OnTheGpu, HardwareFrameChoice.ForBackend(OnTheGpu, null, bound: null, RefusesCuda));
    }

    [Fact]
    public void ThePlayersOwnPath_OverASinkThatReadsD3D_TakesD3D11AndD3D12Frames_AndRefusesCuda()
    {
        var (graph, source) = SubstrateSession.VideoProbe(configurator: null, new Sink(ReadsD3D));

        Assert.Equal(
            FrameMemoryDomains.D3D11 | FrameMemoryDomains.D3D12,
            graph!.FrameDomainsAcceptedFrom(source!, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly));
        Assert.Equal(
            "video-sink",
            graph.FrameDomainMismatchFor(source!, FrameMemoryDomains.Cuda, FrameDomainRule.CpuOnly)?.Node);
    }

    /// <summary>A bounded sink that declares <paramref name="accepts"/>, or nothing when it is null.</summary>
    private sealed class Sink(FrameMemoryDomains? accepts) : IVideoSink
    {
        public int? MaxHeldFrames => 2;

        public FrameMemoryDomains AcceptedDomains => accepts ?? ((IVideoSink)new Quiet()).AcceptedDomains;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A sink that declares nothing, for the interface's default.</summary>
    private sealed class Quiet : IVideoSink
    {
        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
