using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference;
using FrameFlow.Media;
using Xunit;
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Face.Tests;

/// <summary>
/// A model whose input or outputs are fp16 runs in the detector as its session declares them (#10):
/// the input is written as halves, and fp16 boxes or scores decode as fp32 ones do, through
/// <see cref="BlazeFaceDetector.Detect(IVideoFrame)"/> and through the inference operator. The
/// session declares its scores before its boxes, and refuses a tensor of another type than it
/// declares, as ONNX Runtime does.
/// </summary>
public sealed class ElementTypeTests
{
    public static TheoryData<DType, DType, DType> Types => new()
    {
        { DType.Float16, DType.Float16, DType.Float16 },
        { DType.Float16, DType.Float32, DType.Float32 },
        { DType.Float32, DType.Float16, DType.Float32 },
        { DType.Float32, DType.Float32, DType.Float16 },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public void Detect_WritesTheDeclaredInput_AndDecodesTheDeclaredOutputs(DType input, DType boxes, DType scores)
    {
        var session = new TypedSession(input, boxes, scores);
        using var detector = BlazeFaceDetector.Create(session);
        using var frame = FaceTestFrames.SolidBgra(96, 96, 255, 255, 255);

        var face = Assert.Single(detector.Detect(frame));

        AssertSameFace(Fp32Face(frame), face);
        Assert.Equal(input, session.LastInput);
        // The frame is white, so the first input value is one, as a float or a half.
        Assert.Equal(1f, session.LastInputValue);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task ThroughTheOperator_TheDetectorTakesTheHostRoute_AndFindsWhatDetectFinds(DType input, DType boxes, DType scores)
    {
        var session = new TypedSession(input, boxes, scores);
        using var detector = BlazeFaceDetector.Create(session);
        using var frame = FaceTestFrames.SolidBgra(96, 96, 255, 255, 255);
        IImageModel<IReadOnlyList<FaceDetection>> model = detector;
        Assert.Equal(input, model.Input.Dtype);

        var results = new List<InferenceResult<IReadOnlyList<FaceDetection>>>();
        bool sent = false;
        var graph = new GraphRunner();
        graph.Pipeline(new SourceNode<IVideoFrame>("frame", _ =>
            {
                IVideoFrame? next = sent ? null : frame.AddRef();
                sent = true;
                return ValueTask.FromResult(next);
            }))
            .Infer("faces", detector, results.Add)
            .To(new SinkNode<IVideoFrame>("trunk", (_, _) => ValueTask.CompletedTask, holding: FrameHolding.InFlight));
        await graph.RunAsync(CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(InferencePath.Host, result.Path);
        AssertSameFace(Fp32Face(frame), Assert.Single(result.Result));
        Assert.Equal(input, session.LastInput);
    }

    [Theory]
    [InlineData(DType.UInt8, DType.Float32, DType.Float32, "input 'input'")]
    [InlineData(DType.Float32, DType.Float64, DType.Float32, "output 'boxes'")]
    [InlineData(DType.Float32, DType.Float32, DType.Int8, "output 'scores'")]
    public void AnyOtherElementType_IsRefusedWhenTheDetectorIsBuilt_NamingIt(DType input, DType boxes, DType scores, string named)
    {
        var session = new TypedSession(input, boxes, scores);

        var error = Assert.Throws<NotSupportedException>(() => BlazeFaceDetector.Create(session));

        Assert.Contains(named, error.Message, StringComparison.Ordinal);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData(DType.Float16, DType.Float32)]
    [InlineData(DType.Float32, DType.Float16)]
    public void AnFp16OutputThatIsNotFinite_FailsToLoad(DType boxes, DType scores)
    {
        var session = new TypedSession(DType.Float16, boxes, scores) { NaN = true };

        var error = Assert.Throws<InvalidOperationException>(() => BlazeFaceDetector.Create(session));

        Assert.Contains("not finite", error.Message, StringComparison.Ordinal);
        Assert.True(session.Disposed);
    }

    private static FaceDetection Fp32Face(IVideoFrame frame)
    {
        using var detector = BlazeFaceDetector.Create(new TypedSession(DType.Float32, DType.Float32, DType.Float32));
        return Assert.Single(detector.Detect(frame));
    }

    private static void AssertSameFace(FaceDetection expected, FaceDetection actual)
    {
        Assert.Equal(
            (expected.Confidence, expected.X, expected.Y, expected.Width, expected.Height),
            (actual.Confidence, actual.X, actual.Y, actual.Width, actual.Height));
        Assert.Equal(expected.Keypoints, actual.Keypoints);
    }

    /// <summary>
    /// The front-128 model, declaring the types it is given and refusing tensors of any other. Each
    /// run writes one confident 20-pixel face at anchor 0, every value a half exactly; with
    /// <see cref="NaN"/>, it writes NaN into whichever outputs are fp16.
    /// </summary>
    private sealed class TypedSession(DType inputType, DType boxType, DType scoreType) : IElementTypedSession
    {
        public bool NaN { get; init; }

        public DType? LastInput { get; private set; }

        public float LastInputValue { get; private set; }

        public bool Disposed { get; private set; }

        public IReadOnlyList<string> InputNames { get; } = ["input"];

        public IReadOnlyList<string> OutputNames { get; } = ["scores", "boxes"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [[1, 3, 128, 128]];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [[1, 896, 1], [1, 896, 16]];

        public IReadOnlyList<DType> InputElementTypes => [inputType];

        public IReadOnlyList<DType> OutputElementTypes => [scoreType, boxType];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            var input = inputs["input"];
            Require(input, inputType);
            Require(outputs["boxes"], boxType);
            Require(outputs["scores"], scoreType);

            LastInput = input.Dtype;
            LastInputValue = Read(input);

            var boxes = new float[896 * 16];
            var scores = new float[896];
            Array.Fill(scores, -100f);
            boxes[2] = 20;
            boxes[3] = 20;
            scores[0] = 10;
            if (NaN)
            {
                if (boxType == DType.Float16)
                    Array.Fill(boxes, float.NaN);
                if (scoreType == DType.Float16)
                    Array.Fill(scores, float.NaN);
            }

            Write(boxes, outputs["boxes"]);
            Write(scores, outputs["scores"]);
        }

        public void Dispose() => Disposed = true;

        private static void Require(ICpuTensor tensor, DType declared)
        {
            if (tensor.Dtype != declared)
                throw new InvalidOperationException($"The model declares {declared}; it was given {tensor.Dtype}.");
        }

        private static float Read(ICpuTensor tensor) =>
            tensor.Dtype == DType.Float16
                ? (float)MemoryMarshal.Cast<byte, Half>(tensor.Bytes.Span)[0]
                : MemoryMarshal.Cast<byte, float>(tensor.Bytes.Span)[0];

        private static void Write(float[] values, ICpuTensor tensor)
        {
            var bytes = MemoryMarshal.AsMemory(tensor.Bytes).Span;
            if (tensor.Dtype == DType.Float16)
            {
                var halves = MemoryMarshal.Cast<byte, Half>(bytes);
                for (int i = 0; i < values.Length; i++)
                    halves[i] = (Half)values[i];
            }
            else
            {
                values.CopyTo(MemoryMarshal.Cast<byte, float>(bytes));
            }
        }
    }
}
