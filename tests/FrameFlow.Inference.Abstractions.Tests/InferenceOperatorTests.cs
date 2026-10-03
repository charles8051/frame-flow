using System.Numerics;
using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference.Core;
using FrameFlow.Media;
using Xunit;
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// The inference operator: which route a frame takes, what it declares it holds, and that the
/// branch it wires delivers a result per frame with the frame's timestamp while the trunk carries
/// every frame on. Fakes stand in for the session and the device stage.
/// </summary>
public sealed class InferenceOperatorTests
{
    [Theory]
    [InlineData(true, false, false, InferenceRoute.Host)]
    [InlineData(true, true, false, InferenceRoute.Host)]
    [InlineData(false, false, false, InferenceRoute.None)]
    [InlineData(false, true, false, InferenceRoute.None)]
    [InlineData(false, true, true, InferenceRoute.Device)]
    [InlineData(true, true, true, InferenceRoute.Device)]
    internal void TheRoute_PrefersTheDeviceAndFallsBackToTheCpuForCpuFrames(
        bool frameInCpuMemory, bool stageCanWrite, bool sessionCanBind, InferenceRoute expected) =>
        Assert.Equal(expected, InferenceRoutes.Choose(frameInCpuMemory, stageCanWrite, sessionCanBind));

    [Fact]
    public void ACpuFrame_IsPreparedOnTheCpuAndRunOnTheHost()
    {
        var session = new ChannelMeanSession();
        var runner = new InferenceRunner<float[]>(new ChannelMeanModel(session), stage: null);
        using var frame = Solid(20, 40, 60, TimeSpan.FromMilliseconds(40));

        var result = runner.Run(frame);

        Assert.Equal(InferencePath.Host, result.Path);
        Assert.Equal(TimeSpan.FromMilliseconds(40), result.Timestamp);
        Assert.Equal((4, 4), (result.Width, result.Height));
        // RGB planes of a solid BGRA (20, 40, 60) frame: red 60, green 40, blue 20.
        Assert.Equal([60 / 255f, 40 / 255f, 20 / 255f], result.Result, new ToleranceComparer(1e-6f));
    }

    /// <summary>
    /// The host input takes the element type the model's options name, so a model that takes bytes
    /// gets the frame's bytes with no second pass.
    /// </summary>
    [Theory]
    [InlineData(DType.Float32, new byte[] { 0, 0, 0x70, 0x42 })]
    [InlineData(DType.Float16, new byte[] { 0x80, 0x53 })]
    [InlineData(DType.UInt8, new byte[] { 60 })]
    public void TheHostInput_TakesTheModelsElementType(DType dtype, byte[] red)
    {
        var session = new InputRecordingSession();
        var model = new InputRecordingModel(session, new ImageToTensorOptions(2, 2)
        {
            Dtype = dtype,
            Normalization = TensorNormalization.Range(0, 255),
        });
        var runner = new InferenceRunner<int>(model, stage: null);
        using var frame = Solid(20, 40, 60, TimeSpan.Zero);

        runner.Run(frame);

        var input = session.LastInput!;
        Assert.Equal(dtype, input.Dtype);
        Assert.Equal(new TensorShape(1, 3, 2, 2), input.Shape);
        // The first red value: 60 as a float, a half, or a byte.
        Assert.Equal(red, input.Bytes[..red.Length]);
    }

    [Fact]
    public void AGpuFrameTheStageReads_IsPreparedOnTheDeviceAndBoundInPlace()
    {
        var session = new ChannelMeanSession();
        var model = new ChannelMeanModel(session);
        var stage = new FakeStage(model.Input);
        var runner = new InferenceRunner<float[]>(model, stage);
        using var frame = new FakeGpuFrame(TimeSpan.FromMilliseconds(80));

        var result = runner.Run(frame);

        Assert.Equal(InferencePath.Device, result.Path);
        Assert.Equal(TimeSpan.FromMilliseconds(80), result.Timestamp);
        Assert.Equal(RotatedRect.Whole(frame), stage.LastCrop);
        Assert.Equal(stage.DeviceTensor, session.LastDeviceInput);
        Assert.Equal(ChannelMeanSession.DeviceOutput, result.Result);
    }

    [Fact]
    public void AGpuFrameWhoseStageTheSessionCannotBind_HasNoRoute()
    {
        var session = new ChannelMeanSession { Binds = false };
        var model = new ChannelMeanModel(session);
        var runner = new InferenceRunner<float[]>(model, new FakeStage(model.Input));
        using var frame = new FakeGpuFrame(TimeSpan.Zero);

        var error = Assert.Throws<NotSupportedException>(() => runner.Run(frame));
        Assert.Contains("ToCpu", error.Message);
    }

    [Fact]
    public void AGpuFrameWithNoStage_HasNoRoute()
    {
        var runner = new InferenceRunner<float[]>(new ChannelMeanModel(new ChannelMeanSession()), stage: null);
        using var frame = new FakeGpuFrame(TimeSpan.Zero);

        Assert.Throws<NotSupportedException>(() => runner.Run(frame));
    }

    /// <summary>
    /// The outputs take the element types the session declares (#520), so a model with an fp16 output
    /// is not handed floats that ONNX Runtime refuses.
    /// </summary>
    [Fact]
    public void TheOutputs_TakeTheElementTypesTheSessionDeclares()
    {
        var session = new TypedOutputsSession([DType.Float16, DType.Int64]);
        var runner = new InferenceRunner<DType[]>(new OutputTypesModel(session), stage: null);
        using var frame = Solid(0, 0, 0, TimeSpan.Zero);

        Assert.Equal([DType.Float16, DType.Int64], runner.Run(frame).Result);
    }

    [Fact]
    public void AnOutputTypeNoHostTensorHolds_IsRefusedByName()
    {
        var session = new TypedOutputsSession([DType.Float32, DType.BFloat16]);
        var runner = new InferenceRunner<DType[]>(new OutputTypesModel(session), stage: null);
        using var frame = Solid(0, 0, 0, TimeSpan.Zero);

        var error = Assert.Throws<NotSupportedException>(() => runner.Run(frame));
        Assert.Contains("'b'", error.Message, StringComparison.Ordinal);
        Assert.Contains("BFloat16", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelWithADynamicOutput_IsRefusedByName()
    {
        var session = new ChannelMeanSession { OutputShape = [1, -1] };
        var runner = new InferenceRunner<float[]>(new ChannelMeanModel(session), stage: null);
        using var frame = Solid(0, 0, 0, TimeSpan.Zero);

        var error = Assert.Throws<NotSupportedException>(() => runner.Run(frame));
        Assert.Contains("'y'", error.Message);
    }

    [Fact]
    public void TheNode_DeclaresWhatTheStageHolds_AndEmitsNoFrame()
    {
        var model = new ChannelMeanModel(new ChannelMeanSession());

        var hostOnly = InferenceOperators.Infer("infer", model);
        var withStage = InferenceOperators.Infer("infer", model, new FakeStage(model.Input));

        Assert.Equal(FrameHolding.AtMost(1, forwardsStorage: false), hostOnly.Holding);
        Assert.Equal(FrameHolding.AtMost(3, forwardsStorage: false), withStage.Holding);
    }

    [Fact]
    public void WithoutADeviceStage_OrWithOneThatSaysNothing_TheNodeTakesCpuFramesOnly()
    {
        var model = new ChannelMeanModel(new ChannelMeanSession());

        // A GPU frame has no way in without a stage (#435).
        Assert.Equal(
            FrameDomainRule.Accepting(FrameMemoryDomains.Cpu, emits: FrameMemoryDomains.None),
            InferenceOperators.Infer("infer", model).Domains);
        // A stage that does not say which GPU domains it reads gets none (#566).
        Assert.Equal(
            FrameDomainRule.Accepting(FrameMemoryDomains.Cpu, emits: FrameMemoryDomains.None),
            InferenceOperators.Infer("infer", model, new FakeStage(model.Input)).Domains);
    }

    [Fact]
    public void WithADeviceStage_TheNodeTakesCpuFrames_AndTheGpuDomainsTheStageReads()
    {
        var model = new ChannelMeanModel(new ChannelMeanSession());

        // A stage that reads D3D12 frames does not make the node take CUDA ones (#566).
        Assert.Equal(
            FrameDomainRule.Accepting(FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D12, emits: FrameMemoryDomains.None),
            InferenceOperators.Infer("infer", model, new D3D12OnlyStage(model.Input)).Domains);
    }

    [Fact]
    public void AStageThatWritesAnotherTensor_IsRefused()
    {
        var model = new ChannelMeanModel(new ChannelMeanSession());
        var other = new FakeStage(new ImageToTensorOptions(8, 8));

        var error = Assert.Throws<ArgumentException>(() => InferenceOperators.Infer("infer", model, other));
        Assert.Equal("deviceStage", error.ParamName);
    }

    /// <summary>
    /// The D3D12 stage writes floats only, so a model that takes halves never gets its tensor on the
    /// device route: the node refuses a float stage for it, and its input is prepared on the CPU.
    /// </summary>
    [Fact]
    public void AFloatStage_IsRefusedForAModelThatTakesHalves()
    {
        var halves = new ImageToTensorOptions(2, 2) { Dtype = DType.Float16 };
        var model = new InputRecordingModel(new InputRecordingSession(), halves);

        var error = Assert.Throws<ArgumentException>(
            () => InferenceOperators.Infer("infer", model, new FakeStage(halves with { Dtype = DType.Float32 })));
        Assert.Equal("deviceStage", error.ParamName);
        Assert.Contains("Float16", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBranch_DeliversAResultPerFrame_WhileTheTrunkCarriesEveryFrame()
    {
        var graph = new GraphRunner();
        var results = new List<InferenceResult<float[]>>();
        var trunk = new List<TimeSpan>();
        var model = new ChannelMeanModel(new ChannelMeanSession());

        graph.Pipeline(Frames(3))
            .Infer("infer", model, result =>
            {
                lock (results)
                    results.Add(result);
            })
            .To(new SinkNode<IVideoFrame>("trunk", (frame, _) =>
            {
                trunk.Add(frame.Timestamp);
                return ValueTask.CompletedTask;
            }, holding: FrameHolding.InFlight));

        await graph.RunAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80)], trunk);
        // A latest-wins branch may drop a frame that arrives while a run is in progress, so the
        // results are an ordered subset of the frames, each carrying its own frame's timestamp.
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Contains(r.Timestamp, trunk));
        Assert.Equal(results.Select(r => r.Timestamp).Order(), results.Select(r => r.Timestamp));
        Assert.All(results, r => Assert.Equal(InferencePath.Host, r.Path));
    }

    /// <summary>
    /// The result sink's id is derived from the caller's, so two branches given one id collide on
    /// both of their nodes, and the graph names both ids (#500).
    /// </summary>
    [Fact]
    public async Task TwoBranchesWithOneId_AreRefusedBeforeTheRunStarts()
    {
        var graph = new GraphRunner();
        var model = new ChannelMeanModel(new ChannelMeanSession());

        graph.Pipeline(Frames(1))
            .Infer("infer", model, _ => { })
            .Infer("infer", model, _ => { })
            .To(Discarding("trunk"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None));

        Assert.Contains("2 nodes have the id 'infer'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 nodes have the id 'infer-results'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallersNodeOnTheResultSinksId_IsRefused()
    {
        var graph = new GraphRunner();
        var model = new ChannelMeanModel(new ChannelMeanSession());

        graph.Pipeline(Frames(1))
            .Infer("infer", model, _ => { })
            .To(Discarding("infer-results"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.RunAsync(CancellationToken.None));

        Assert.Contains("2 nodes have the id 'infer-results'", ex.Message, StringComparison.Ordinal);
    }

    private static SinkNode<IVideoFrame> Discarding(string id) =>
        new(id, (_, _) => ValueTask.CompletedTask, holding: FrameHolding.InFlight);

    private static SourceNode<IVideoFrame> Frames(int count)
    {
        int next = 0;
        return new SourceNode<IVideoFrame>("frames", _ =>
        {
            if (next >= count)
                return ValueTask.FromResult<IVideoFrame?>(null);
            return ValueTask.FromResult<IVideoFrame?>(Solid(10, 20, 30, TimeSpan.FromMilliseconds(40 * next++)));
        });
    }

    private static CpuVideoFrame Solid(byte b, byte g, byte r, TimeSpan pts) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32, 4, 4, pts, TimeSpan.FromMilliseconds(40), (b, g, r),
            static (planes, bgr) =>
            {
                for (int y = 0; y < planes.Height; y++)
                {
                    for (int x = 0; x < planes.Width; x++)
                    {
                        int o = (y * planes.StrideY) + (x * 4);
                        planes.Y[o] = bgr.b;
                        planes.Y[o + 1] = bgr.g;
                        planes.Y[o + 2] = bgr.r;
                        planes.Y[o + 3] = 255;
                    }
                }
            });

    /// <summary>A model whose output is its input's per-channel mean, read back as the result.</summary>
    private sealed class ChannelMeanModel(ChannelMeanSession session) : IImageModel<float[]>
    {
        public IInferenceSession Session => session;

        public string InputName => "x";

        public ImageToTensorOptions Input { get; } = new(2, 2);

        public RotatedRect CropFor(IVideoFrame frame) => RotatedRect.Whole(frame);

        public float[] Decode(IReadOnlyDictionary<string, ICpuTensor> outputs, TensorTransform transform, IVideoFrame frame) =>
            MemoryMarshal.Cast<byte, float>(outputs["y"].Bytes.Span).ToArray();
    }

    /// <summary>
    /// Host runs write each input channel's mean; device runs record the tensor and write
    /// <see cref="DeviceOutput"/>.
    /// </summary>
    private sealed class ChannelMeanSession : IDeviceInputSession
    {
        public static readonly float[] DeviceOutput = [42f, 43f, 44f];

        public bool Binds { get; init; } = true;

        public long[] OutputShape { get; init; } = [1, 3];

        public DeviceTensor? LastDeviceInput { get; private set; }

        public IReadOnlyList<string> InputNames => ["x"];

        public IReadOnlyList<string> OutputNames => ["y"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes => [new long[] { 1, 3, 2, 2 }];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes => [OutputShape];

        public bool CanBind(in DeviceTensor tensor) => Binds;

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            var input = MemoryMarshal.Cast<byte, float>(inputs["x"].Bytes.Span);
            var output = Writable(outputs["y"]);
            for (int c = 0; c < 3; c++)
                output[c] = (input[(c * 4) + 0] + input[(c * 4) + 1] + input[(c * 4) + 2] + input[(c * 4) + 3]) / 4;
        }

        public void Run(IReadOnlyDictionary<string, DeviceTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            LastDeviceInput = inputs["x"];
            DeviceOutput.CopyTo(Writable(outputs["y"]));
        }

        public void Dispose()
        {
        }

        private static Span<float> Writable(ICpuTensor tensor) =>
            MemoryMarshal.Cast<byte, float>(MemoryMarshal.AsMemory(tensor.Bytes).Span);
    }

    /// <summary>A model over <see cref="InputRecordingSession"/> whose result is always zero.</summary>
    private sealed class InputRecordingModel(InputRecordingSession session, ImageToTensorOptions input) : IImageModel<int>
    {
        public IInferenceSession Session => session;

        public string InputName => "x";

        public ImageToTensorOptions Input => input;

        public RotatedRect CropFor(IVideoFrame frame) => RotatedRect.Whole(frame);

        public int Decode(IReadOnlyDictionary<string, ICpuTensor> outputs, TensorTransform transform, IVideoFrame frame) => 0;
    }

    /// <summary>Keeps a copy of the input of each host run.</summary>
    private sealed class InputRecordingSession : IInferenceSession
    {
        public RecordedInput? LastInput { get; private set; }

        public IReadOnlyList<string> InputNames => ["x"];

        public IReadOnlyList<string> OutputNames => ["y"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes => [new long[] { 1, 3, 2, 2 }];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes => [new long[] { 1 }];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            var input = inputs["x"];
            LastInput = new RecordedInput(input.Dtype, input.Shape, input.Bytes.ToArray());
        }

        public void Dispose()
        {
        }
    }

    private sealed record RecordedInput(DType Dtype, TensorShape Shape, byte[] Bytes);

    /// <summary>A model whose result is the element type of each output the operator allocated.</summary>
    private sealed class OutputTypesModel(TypedOutputsSession session) : IImageModel<DType[]>
    {
        public IInferenceSession Session => session;

        public string InputName => "x";

        public ImageToTensorOptions Input { get; } = new(2, 2);

        public RotatedRect CropFor(IVideoFrame frame) => RotatedRect.Whole(frame);

        public DType[] Decode(IReadOnlyDictionary<string, ICpuTensor> outputs, TensorTransform transform, IVideoFrame frame) =>
            [.. session.OutputNames.Select(name => outputs[name].Dtype)];
    }

    /// <summary>Two outputs, <c>a</c> and <c>b</c>, of the types it is given. A run writes nothing.</summary>
    private sealed class TypedOutputsSession(DType[] outputTypes) : IElementTypedSession
    {
        public IReadOnlyList<string> InputNames => ["x"];

        public IReadOnlyList<string> OutputNames => ["a", "b"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes => [new long[] { 1, 3, 2, 2 }];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes => [new long[] { 1, 2 }, new long[] { 3 }];

        public IReadOnlyList<DType> InputElementTypes => [DType.Float32];

        public IReadOnlyList<DType> OutputElementTypes => outputTypes;

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeStage(ImageToTensorOptions options) : IDeviceImageToTensor
    {
        public ImageToTensorOptions Options => options;

        public int MaxHeldFrames => 3;

        public RotatedRect? LastCrop { get; private set; }

        public DeviceTensor DeviceTensor { get; } =
            new(DeviceTensorKind.D3D12, Buffer: 1, Device: 2, new TensorShape(1, 3, 2, 2), DType.Float32, ReadyFence: 3, ReadyValue: 4);

        public bool CanWrite(IVideoFrame frame) => frame is FakeGpuFrame;

        public TensorTransform Write(IVideoFrame frame, RotatedRect crop)
        {
            LastCrop = crop;
            return new TensorTransform(Matrix3x2.Identity);
        }
    }

    /// <summary>A <see cref="FakeStage"/> that says it reads D3D12 frames only.</summary>
    private sealed class D3D12OnlyStage(ImageToTensorOptions options) : IDeviceImageToTensor
    {
        private readonly FakeStage _inner = new(options);

        public ImageToTensorOptions Options => options;

        public int MaxHeldFrames => _inner.MaxHeldFrames;

        public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.D3D12;

        public DeviceTensor DeviceTensor => _inner.DeviceTensor;

        public bool CanWrite(IVideoFrame frame) => _inner.CanWrite(frame);

        public TensorTransform Write(IVideoFrame frame, RotatedRect crop) => _inner.Write(frame, crop);
    }

    private sealed class FakeGpuFrame(TimeSpan pts) : IVideoFrame
    {
        public int Width => 4;

        public int Height => 4;

        public TimeSpan Pts => pts;

        public TimeSpan Duration => TimeSpan.FromMilliseconds(40);

        public PixelFormat Format => PixelFormat.Nv12;

        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Gpu;

        public IVideoFrame AddRef() => this;

        public CpuFrameData ToCpu() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class ToleranceComparer(float tolerance) : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => Math.Abs(x - y) <= tolerance;

        public int GetHashCode(float obj) => 0;
    }
}
