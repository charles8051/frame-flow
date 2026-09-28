using Xunit;

using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// Where a source's memory domains can go, checked against what the nodes on the way accept
/// (#435). No graph runs except in the run tests.
/// </summary>
public sealed class FrameDomainTests
{
    [Fact]
    public void ACpuOnlyNode_ThatGpuFramesCanReach_IsNamed()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).Then(Op("scale", FrameDomainRule.CpuOnly)).To(Sink("sink", null));

        var mismatch = graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any);

        Assert.Equal(new FrameDomainMismatch("scale", FrameMemoryDomains.Gpu, FrameMemoryDomains.Cpu), mismatch);
        Assert.Contains("'scale'", mismatch!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromACpuSource_ACpuOnlyNode_IsFine()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).Then(Op("scale", FrameDomainRule.CpuOnly)).To(Sink("sink", FrameDomainRule.CpuOnly));

        Assert.Null(graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Cpu));
    }

    [Fact]
    public void ADownloadBeforeIt_LetsACpuOnlyNodeThrough()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph
            .Pipeline(source)
            .Then(Op("to-cpu", FrameDomainRule.ToCpu))
            .Then(Op("scale", FrameDomainRule.CpuOnly))
            .To(Sink("sink", FrameDomainRule.CpuOnly));

        Assert.Null(graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any));
    }

    [Fact]
    public void UndeclaredNodes_PassOnWhatArrived()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).Then(Op("tag", null)).Then(Op("stamp", null)).To(Sink("encode", FrameDomainRule.CpuOnly));

        Assert.Equal("encode", graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any)?.Node);
    }

    [Fact]
    public void ABranch_IsCheckedToo()
    {
        var graph = new GraphRunner();
        var source = Source();
        var head = graph.Pipeline(source);
        head.Branch(EdgeOptions.LatestWins(1)).To(Sink("detect", FrameDomainRule.CpuOnly));
        head.To(Sink("present", null));

        Assert.Equal("detect", graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any)?.Node);
    }

    [Fact]
    public void AGpuOnlyNode_ThatCpuFramesCanReach_IsNamed_WithTheUploadAdvice()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).To(Sink("present", FrameDomainRule.Accepting(FrameMemoryDomains.Gpu)));

        var mismatch = graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Cpu);

        Assert.Equal(new FrameDomainMismatch("present", FrameMemoryDomains.Cpu, FrameMemoryDomains.Gpu), mismatch);
        Assert.Contains("uploads", mismatch!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleThatAcceptsNothing_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => FrameDomainRule.Accepting(FrameMemoryDomains.None));
    }

    [Fact]
    public async Task ASourceThatDeclaresItsDomains_HasTheRunRefused_BeforeItProducesAnything()
    {
        int produced = 0;
        var graph = new GraphRunner();
        var source = new SourceNode<RefBox<int>>(
            "decoder",
            _ =>
            {
                produced++;
                return ValueTask.FromResult<RefBox<int>?>(null);
            },
            emits: () => FrameMemoryDomains.Any);
        graph.Pipeline(source).To(Sink("encode", FrameDomainRule.CpuOnly));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => graph.RunAsync(CancellationToken.None));

        Assert.Contains("'encode'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, produced);
    }

    [Fact]
    public async Task ASourceThatDeclaresNothing_RunsUnchecked()
    {
        var graph = new GraphRunner();
        graph.Pipeline(Source()).To(Sink("encode", FrameDomainRule.CpuOnly));

        await graph.RunAsync(CancellationToken.None);
    }

    private static SourceNode<RefBox<int>> Source() =>
        new("source", _ => ValueTask.FromResult<RefBox<int>?>(null));

    private static OperatorNode<RefBox<int>, RefBox<int>> Op(string id, FrameDomainRule? domains) =>
        new(id, (item, _) => ValueTask.FromResult<RefBox<int>?>(item), holding: FrameHolding.InFlight, domains: domains);

    private static SinkNode<RefBox<int>> Sink(string id, FrameDomainRule? domains) =>
        new(id, (_, _) => ValueTask.CompletedTask, holding: FrameHolding.InFlight, domains: domains);
}
