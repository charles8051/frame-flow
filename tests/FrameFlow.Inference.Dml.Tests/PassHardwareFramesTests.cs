using System.Collections.Concurrent;
using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// A pass that hands its graph hardware frames (#277): its decoder's pool sized for what the
/// path holds (#292), a path with no bound refused at build (#416), a model run on the frames
/// where they are, and the frames kept on the GPU without being asked when every node takes
/// them (#294).
/// </summary>
public sealed class PassHardwareFramesTests
{
    private const string Clip = "test-video-h264-yuv420p.mp4";

    private static readonly ImageToTensorOptions Options = new(64, 48);

    /// <summary>
    /// The decoder, the D3D12 stage and the DirectML session share one device. The sink receives
    /// GPU frames and every result took the device route, so nothing was read back.
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task APass_RunsTheModelOnD3D12VaFrames_AndReadsNothingBack()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        using var gpu = new DeviceAndQueue(device);
        using var stage = gpu.Stage(Options);
        using var session = DmlInferenceSession.OnDevice(
            OnnxModel.Negate(1, 3, Options.Height, Options.Width), gpu.Device.NativePointer, gpu.Queue.NativePointer);
        var model = new NegateModel(session, Options);
        var results = new ConcurrentQueue<InferenceResult<float[]>>();
        var sink = new RecordingSink(maxHeldFrames: 0);

        await using (var pass = await FrameFlowPass
            .Create(TestEnvironment.CorpusFile(Clip)!)
            .WithHardwareDevice(device)
            .WithHardwareFrames()
            .WithVideoSink(sink)
            .ConfigureVideo(chain => chain.Infer("negate", model, results.Enqueue, stage))
            .BuildAsync())
        {
            await pass.RunToCompletionAsync();
        }

        Assert.NotEmpty(sink.Domains);
        Assert.All(sink.Domains, domain => Assert.Equal(FrameMemoryDomain.Gpu, domain));
        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Equal(InferencePath.Device, result.Path));
    }

    /// <summary>
    /// Without <c>WithHardwareFrames</c>, the same pass keeps the frames on the GPU: the
    /// inference node and the sink both say they take GPU frames (#294).
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task APassUnasked_KeepsFramesOnTheGpu_WhenEveryNodeTakesThem()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        using var gpu = new DeviceAndQueue(device);
        using var stage = gpu.Stage(Options);
        using var session = DmlInferenceSession.OnDevice(
            OnnxModel.Negate(1, 3, Options.Height, Options.Width), gpu.Device.NativePointer, gpu.Queue.NativePointer);
        var model = new NegateModel(session, Options);
        var results = new ConcurrentQueue<InferenceResult<float[]>>();
        var sink = new RecordingSink(maxHeldFrames: 0);

        await using (var pass = await FrameFlowPass
            .Create(TestEnvironment.CorpusFile(Clip)!)
            .WithHardwareDevice(device)
            .WithVideoSink(sink)
            .ConfigureVideo(chain => chain.Infer("negate", model, results.Enqueue, stage))
            .BuildAsync())
        {
            await pass.RunToCompletionAsync();
        }

        Assert.NotEmpty(sink.Domains);
        Assert.All(sink.Domains, domain => Assert.Equal(FrameMemoryDomain.Gpu, domain));
        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Equal(InferencePath.Device, result.Path));
    }

    /// <summary>
    /// Unasked, a pass downloads the frames for a sink that has not said it takes GPU frames. It
    /// builds and runs on CPU frames (#294).
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task APassUnasked_DownloadsFrames_ForASinkThatSaysNothing()
    {
        var sink = new UndeclaredRecordingSink();
        await RunUnaskedAsync(sink, undeclaredNode: false);
        Assert.NotEmpty(sink.Domains);
        Assert.All(sink.Domains, domain => Assert.Equal(FrameMemoryDomain.Cpu, domain));
    }

    /// <summary>Unasked, the same for a node that has not said it takes GPU frames (#294).</summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task APassUnasked_DownloadsFrames_ForANodeThatSaysNothing()
    {
        var sink = new RecordingSink(maxHeldFrames: 0);
        await RunUnaskedAsync(sink, undeclaredNode: true);
        Assert.NotEmpty(sink.Domains);
        Assert.All(sink.Domains, domain => Assert.Equal(FrameMemoryDomain.Cpu, domain));
    }

    /// <summary>
    /// Unasked, a path that holds frames without a bound gets CPU frames, where the same path with
    /// <c>WithHardwareFrames</c> is refused (#294, #416).
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task APassUnasked_DownloadsFrames_ForASinkWithNoBound()
    {
        var sink = new RecordingSink(maxHeldFrames: null);
        await RunUnaskedAsync(sink, undeclaredNode: false);
        Assert.NotEmpty(sink.Domains);
        Assert.All(sink.Domains, domain => Assert.Equal(FrameMemoryDomain.Cpu, domain));
    }

    /// <summary>A D3D12VA pass over <paramref name="sink"/> with no <c>WithHardwareFrames</c>.</summary>
    private static async Task RunUnaskedAsync(IVideoSink sink, bool undeclaredNode)
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        var builder = FrameFlowPass
            .Create(TestEnvironment.CorpusFile(Clip)!)
            .WithHardwareDevice(device)
            .WithVideoSink(sink);
        if (undeclaredNode)
        {
            builder = builder.ConfigureVideo(chain => chain.Then(new OperatorNode<IVideoFrame, IVideoFrame>(
                "tag", (frame, _) => ValueTask.FromResult<IVideoFrame?>(frame), holding: FrameHolding.InFlight)));
        }

        await using var pass = await builder.BuildAsync();
        await pass.RunToCompletionAsync();
    }

    /// <summary>
    /// A D3D12VA pool grows by a surface for every frame held, so a sink that declares no bound
    /// is refused when the pass is built, before anything decodes (#292, #416).
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task APassWhoseSinkDeclaresNoBound_IsRefusedAtBuild()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => FrameFlowPass
            .Create(TestEnvironment.CorpusFile(Clip)!)
            .WithHardwareDevice(device)
            .WithHardwareFrames()
            .WithVideoSink(new RecordingSink(maxHeldFrames: null))
            .BuildAsync());

        Assert.Contains("'video-sink'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("grows", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A D3D11VA pool is fixed with 3 spare surfaces. A path that holds 8 opens the decoder with
    /// the 5 more it needs, so the pool's budget is the path's.
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D11Va, Clip)]
    public async Task APassSizesAFixedPool_ForWhatItsPathHolds()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D11Va);

        await using var pass = await FrameFlowPass
            .Create(TestEnvironment.CorpusFile(Clip)!)
            .WithHardwareDevice(device)
            .WithHardwareFrames()
            .WithVideoSink(new RecordingSink(maxHeldFrames: 0))
            .ConfigureVideo(chain => chain.Then(new OperatorNode<IVideoFrame, IVideoFrame>(
                "holds-four", (frame, _) => ValueTask.FromResult<IVideoFrame?>(frame), holding: FrameHolding.AtMost(4))))
            .BuildAsync();
        await pass.RunToCompletionAsync();

        // The pump's item, two edges, the operator's four and the sink's call.
        Assert.Equal(8, pass.VideoDecoder!.GetDiagnostics().HardwareFrameBudget);
    }

    /// <summary>
    /// A node that reads CPU pixels, on a pass that hands its graph D3D12VA frames, is refused
    /// when the pass is built and named, where it used to fail on the first frame (#435).
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task ACpuOnlyNode_OnAGpuPath_IsRefusedAtBuild()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => FrameFlowPass
            .Create(TestEnvironment.CorpusFile(Clip)!)
            .WithHardwareDevice(device)
            .WithHardwareFrames()
            .WithVideoSink(new RecordingSink(maxHeldFrames: 0))
            .ConfigureVideo(chain => chain.Then(Passing("scale", FrameDomainRule.CpuOnly)))
            .BuildAsync());

        Assert.Contains("'scale' takes CPU frames", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A download before the same node is what the refusal asks for, and it builds.</summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task ADownloadBeforeACpuOnlyNode_Builds()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);

        await using var pass = await FrameFlowPass
            .Create(TestEnvironment.CorpusFile(Clip)!)
            .WithHardwareDevice(device)
            .WithHardwareFrames()
            .WithVideoSink(new RecordingSink(maxHeldFrames: 0))
            .ConfigureVideo(chain => chain
                .Then(Passing("to-cpu", FrameDomainRule.ToCpu))
                .Then(Passing("scale", FrameDomainRule.CpuOnly)))
            .BuildAsync();
    }

    /// <summary>A node that forwards its input, declaring <paramref name="domains"/>. The pass is only built.</summary>
    private static OperatorNode<IVideoFrame, IVideoFrame> Passing(string id, FrameDomainRule domains) =>
        new(id, (frame, _) => ValueTask.FromResult<IVideoFrame?>(frame), holding: FrameHolding.InFlight, domains: domains);

    /// <summary>Records each frame's memory domain. It reads no pixels, so it takes either.</summary>
    private sealed class RecordingSink(int? maxHeldFrames) : IVideoSink
    {
        public ConcurrentQueue<FrameMemoryDomain> Domains { get; } = new();

        public int? MaxHeldFrames => maxHeldFrames;

        public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.Any;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            Domains.Enqueue(frame.MemoryDomain);
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records each frame's memory domain, and says nothing about which it takes.</summary>
    private sealed class UndeclaredRecordingSink : IVideoSink
    {
        public ConcurrentQueue<FrameMemoryDomain> Domains { get; } = new();

        public int? MaxHeldFrames => 0;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            Domains.Enqueue(frame.MemoryDomain);
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
