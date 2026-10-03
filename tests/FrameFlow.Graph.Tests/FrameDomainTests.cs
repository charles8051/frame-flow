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
    public void ANodeThatEmitsNoDomain_EndsTheWalk()
    {
        // An inference node's results carry no frame, so a sink for them is not refused.
        var graph = new GraphRunner();
        var source = Source();
        graph
            .Pipeline(source)
            .Then(Op("infer", FrameDomainRule.Accepting(FrameMemoryDomains.Any, emits: FrameMemoryDomains.None)))
            .To(Sink("results", FrameDomainRule.CpuOnly));

        Assert.Null(graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any));
        Assert.Null(graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any, FrameDomainRule.CpuOnly));
    }

    [Fact]
    public void WithUndeclaredTakingCpu_AnUndeclaredNodeGpuFramesReach_IsNamed()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).Then(Op("gate", FrameDomainRule.Any)).Then(Op("tag", null)).To(Sink("present", FrameDomainRule.Any));

        Assert.Null(graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any));
        Assert.Equal(
            new FrameDomainMismatch("tag", FrameMemoryDomains.Gpu, FrameMemoryDomains.Cpu),
            graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any, FrameDomainRule.CpuOnly));
    }

    [Fact]
    public void WithUndeclaredTakingCpu_NodesAfterADownload_NeedNotDeclare()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph
            .Pipeline(source)
            .Then(Op("gate", FrameDomainRule.Any))
            .Then(Op("to-cpu", FrameDomainRule.ToCpu))
            .Then(Op("tag", null))
            .To(Sink("encode", null));

        Assert.Null(graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any, FrameDomainRule.CpuOnly));
    }

    [Fact]
    public void WithUndeclaredTakingCpu_AnUndeclaredSinkGpuFramesReach_IsNamed()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).To(Sink("present", null));

        Assert.Equal(
            "present",
            graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Any, FrameDomainRule.CpuOnly)?.Node);
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

    private const FrameMemoryDomains ReadsD3D =
        FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11 | FrameMemoryDomains.D3D12;

    [Fact]
    public void ANodeThatReadsSomeGpuApis_IsNamed_ForAnother_AndTheApisAreSpelledOut()
    {
        var graph = new GraphRunner();
        var source = Source();
        graph.Pipeline(source).To(Sink("present", FrameDomainRule.Accepting(ReadsD3D)));

        var mismatch = graph.FrameDomainMismatchFor(source.Output, FrameMemoryDomains.Cpu | FrameMemoryDomains.Cuda);

        Assert.Equal(new FrameDomainMismatch("present", FrameMemoryDomains.Cuda, ReadsD3D), mismatch);
        Assert.StartsWith(
            "'present' takes CPU, D3D11 and D3D12 frames, and CUDA frames can reach it. Put a node that downloads",
            mismatch!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheGpuDomainsAPathTakes_AreThoseEveryBranchTakes()
    {
        var graph = new GraphRunner();
        var source = Source();
        var head = graph.Pipeline(source).Then(Op("tag", FrameDomainRule.Any));
        head.Branch(EdgeOptions.LatestWins(1))
            .To(Sink("infer", FrameDomainRule.Accepting(FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D12)));
        head.To(Sink("present", FrameDomainRule.Accepting(ReadsD3D)));

        Assert.Equal(
            FrameMemoryDomains.D3D12,
            graph.FrameDomainsAcceptedFrom(source.Output, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly));
    }

    [Fact]
    public void ADownload_LetsEveryGpuDomainThrough_AndANodeThatSaysNothingLetsNone()
    {
        var downloading = new GraphRunner();
        var first = Source();
        downloading.Pipeline(first).Then(Op("to-cpu", FrameDomainRule.ToCpu)).To(Sink("scale", FrameDomainRule.CpuOnly));
        var undeclared = new GraphRunner();
        var second = Source();
        undeclared.Pipeline(second).Then(Op("tag", null)).To(Sink("present", FrameDomainRule.Accepting(ReadsD3D)));

        Assert.Equal(
            FrameMemoryDomains.Gpu,
            downloading.FrameDomainsAcceptedFrom(first.Output, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly));
        Assert.Equal(
            FrameMemoryDomains.None,
            undeclared.FrameDomainsAcceptedFrom(second.Output, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly));
        Assert.Equal(
            FrameMemoryDomains.D3D11 | FrameMemoryDomains.D3D12,
            undeclared.FrameDomainsAcceptedFrom(second.Output, FrameMemoryDomains.Gpu, FrameDomainRule.Any));
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
