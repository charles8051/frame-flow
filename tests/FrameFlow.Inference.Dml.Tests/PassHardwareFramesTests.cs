using System.Collections.Concurrent;
using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;
using FrameFlow.Player;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>
/// A pass that hands its graph hardware frames (#277): its decoder's pool sized for what the
/// path holds (#292), a path with no bound refused at build (#416), and a model run on the frames
/// where they are.
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

    private sealed class RecordingSink(int? maxHeldFrames) : IVideoSink
    {
        public ConcurrentQueue<FrameMemoryDomain> Domains { get; } = new();

        public int? MaxHeldFrames => maxHeldFrames;

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
