using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference;
using FrameFlow.Media;
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// A model whose input or output is fp16 runs in the detector as its session declares it (#10): the
/// input is written as halves, an fp16 output decodes as the fp32 one does, through
/// <see cref="Yolov8Detector.Detect"/> and through the inference operator. The session refuses a
/// tensor of another type than it declares, as ONNX Runtime does.
/// </summary>
public sealed class ElementTypeTests
{
    public static TheoryData<DType, DType> InputAndOutputTypes => new()
    {
        { DType.Float32, DType.Float32 },
        { DType.Float16, DType.Float16 },
        { DType.Float16, DType.Float32 },
        { DType.Float32, DType.Float16 },
    };

    [Theory]
    [MemberData(nameof(InputAndOutputTypes))]
    public void Detect_WritesTheDeclaredInput_AndDecodesTheDeclaredOutput(DType input, DType output)
    {
        var session = new TypedSession(input, output);
        using var detector = Yolov8Detector.Create(session);
        using var frame = Solid(128, 64);

        var detection = Assert.Single(detector.Detect(frame));

        Assert.Equal(OneBox, detection);
        Assert.Equal(input, session.LastInput);
        // The frame is white, so every input value is one, as a float or a half.
        Assert.Equal(1f, session.LastInputValue);
    }

    [Theory]
    [MemberData(nameof(InputAndOutputTypes))]
    public async Task ThroughTheOperator_ADetectorOfEitherType_TakesTheHostRoute_AndFindsWhatDetectFinds(DType input, DType output)
    {
        var session = new TypedSession(input, output);
        using var detector = Yolov8Detector.Create(session);
        using var frame = Solid(128, 64);
        IImageModel<IReadOnlyList<Detection>> model = detector;
        Assert.Equal(input, model.Input.Dtype);

        var results = new List<InferenceResult<IReadOnlyList<Detection>>>();
        bool sent = false;
        var graph = new GraphRunner();
        graph.Pipeline(new SourceNode<IVideoFrame>("frame", _ =>
            {
                var next = sent ? null : frame.AddRef();
                sent = true;
                return ValueTask.FromResult(next);
            }))
            .Infer("yolo", detector, results.Add)
            .To(new SinkNode<IVideoFrame>("trunk", (_, _) => ValueTask.CompletedTask, holding: FrameHolding.InFlight));
        await graph.RunAsync(CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(InferencePath.Host, result.Path);
        Assert.Equal(OneBox, Assert.Single(result.Result));
        Assert.Equal(input, session.LastInput);
    }

    [Theory]
    [InlineData(DType.UInt8, DType.Float32, "input 'images'")]
    [InlineData(DType.Float32, DType.Int32, "output 'output0'")]
    [InlineData(DType.Float32, DType.BFloat16, "output 'output0'")]
    public void AnyOtherElementType_IsRefusedWhenTheDetectorIsBuilt_NamingIt(DType input, DType output, string named)
    {
        var session = new TypedSession(input, output);

        var error = Assert.Throws<NotSupportedException>(() => Yolov8Detector.Create(session));

        Assert.Contains(named, error.Message, StringComparison.Ordinal);
        Assert.Contains(input == DType.Float32 ? output.ToString() : input.ToString(), error.Message, StringComparison.Ordinal);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AnFp16OutputThatIsNotFinite_FailsToLoad(float value)
    {
        var session = new TypedSession(DType.Float16, DType.Float16) { Fill = value };

        var error = Assert.Throws<InvalidOperationException>(() => Yolov8Detector.Create(session));

        Assert.Contains("not finite", error.Message, StringComparison.Ordinal);
        Assert.True(session.Disposed);
    }

    /// <summary>
    /// <see cref="TypedSession.Box"/> in a 128 by 64 frame: the 32-pixel input maps x by 4 and y by 2.
    /// </summary>
    private static readonly Detection OneBox = new(0, "class_0", 0.875f, (16 - 4) * 4, (16 - 2) * 2, 8 * 4, 4 * 2);

    private static CpuVideoFrame Solid(int width, int height) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32, width, height, TimeSpan.Zero, TimeSpan.FromMilliseconds(40), 0,
            static (planes, _) =>
            {
                for (int y = 0; y < planes.Height; y++)
                    planes.Y.Slice(y * planes.StrideY, planes.Width * 4).Fill(255);
            });

    /// <summary>
    /// A 32-pixel, 3-class head that declares the element types it is given and refuses tensors of
    /// any other. Each run writes <see cref="Box"/> at anchor 0, or <see cref="Fill"/> everywhere.
    /// </summary>
    private sealed class TypedSession(DType inputType, DType outputType) : IElementTypedSession
    {
        private const int Anchors = 21;

        /// <summary>Centre (16, 16), 8 by 4, class 0 at 0.875: each a half exactly.</summary>
        public static readonly float[] Box = [16, 16, 8, 4, 0.875f];

        public float? Fill { get; init; }

        public DType? LastInput { get; private set; }

        public float LastInputValue { get; private set; }

        public bool Disposed { get; private set; }

        public IReadOnlyList<string> InputNames { get; } = ["images"];

        public IReadOnlyList<string> OutputNames { get; } = ["output0"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [[1, 3, 32, 32]];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [[1, 7, Anchors]];

        public IReadOnlyList<DType> InputElementTypes => [inputType];

        public IReadOnlyList<DType> OutputElementTypes => [outputType];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            var input = inputs["images"];
            var output = outputs["output0"];
            if (input.Dtype != inputType || output.Dtype != outputType)
            {
                throw new InvalidOperationException(
                    $"The model takes {inputType} and returns {outputType}; it was given {input.Dtype} and {output.Dtype}.");
            }

            LastInput = input.Dtype;
            LastInputValue = input.Dtype == DType.Float16
                ? (float)MemoryMarshal.Cast<byte, Half>(input.Bytes.Span)[0]
                : MemoryMarshal.Cast<byte, float>(input.Bytes.Span)[0];

            var values = new float[7 * Anchors];
            if (Fill is { } fill)
            {
                Array.Fill(values, fill);
            }
            else
            {
                for (int channel = 0; channel < Box.Length; channel++)
                    values[channel * Anchors] = Box[channel];
            }

            var bytes = MemoryMarshal.AsMemory(output.Bytes).Span;
            if (output.Dtype == DType.Float16)
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

        public void Dispose() => Disposed = true;
    }
}
