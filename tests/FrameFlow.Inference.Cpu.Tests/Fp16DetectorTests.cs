using FrameFlow.Face;
using FrameFlow.Graph;
using FrameFlow.Inference.Dml.Tests;
using FrameFlow.Media;
using FrameFlow.Yolo;
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Inference.Cpu.Tests;

/// <summary>
/// The detectors run a model with fp16 inputs, outputs or both on ONNX Runtime (#10), as an
/// Ultralytics <c>half=True</c> export has, and find what the same model with fp32 ones finds. Each
/// model writes constant boxes and scores its first candidate by the mean of its input, in floats
/// inside, so a detection needs the input to have arrived.
/// </summary>
public sealed class Fp16DetectorTests
{
    public static TheoryData<ulong, ulong> Types => new()
    {
        { OnnxModel.Float16, OnnxModel.Float16 },
        { OnnxModel.Float16, OnnxModel.Float },
        { OnnxModel.Float, OnnxModel.Float16 },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Yolo_FindsWhatTheFp32ModelFinds_ThroughDetectAndTheOperator(ulong input, ulong output)
    {
        using var frame = Solid(96, 64, b: 0, g: 0, r: 255);
        var expected = DetectObject(Yolo(OnnxModel.Float, OnnxModel.Float), frame);
        // Red over three channels in [0, 1]: a mean of a third.
        Assert.Equal(1f / 3, expected.Confidence, 1e-6f);

        using var detector = Yolov8Detector.Create(new CpuInferenceSession(Yolo(input, output)));
        AssertClose(expected, Assert.Single(detector.Detect(frame)));
        AssertClose(expected, Assert.Single(await ThroughTheOperator(detector, frame)));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task BlazeFace_FindsWhatTheFp32ModelFinds_ThroughDetectAndTheOperator(ulong input, ulong output)
    {
        using var frame = Solid(128, 128, b: 0, g: 255, r: 255);
        var expected = DetectFace(Face(OnnxModel.Float, OnnxModel.Float), frame);
        // Yellow over three channels in [-1, 1]: a mean of a third, so a logit of -2 + 10 / 3.
        Assert.Equal(1 / (1 + MathF.Exp(2 - (10f / 3))), expected.Confidence, 1e-5f);

        using var detector = BlazeFaceDetector.Create(new CpuInferenceSession(Face(input, output)));
        AssertClose(expected, Assert.Single(detector.Detect(frame)));
        AssertClose(expected, Assert.Single(await ThroughTheOperator(detector, frame)));
    }

    /// <summary>
    /// A 32-pixel, 3-class YOLO head: anchor 0 is a box centred at (16, 16), 8 by 4, and its class 0
    /// scores the input's mean.
    /// </summary>
    private static byte[] Yolo(ulong input, ulong output)
    {
        const int Anchors = 21;
        var constant = new float[7 * Anchors];
        var scale = new float[7 * Anchors];
        constant[0 * Anchors] = 16;
        constant[1 * Anchors] = 16;
        constant[2 * Anchors] = 8;
        constant[3 * Anchors] = 4;
        scale[4 * Anchors] = 1;
        return OnnxModel.ConstantPlusMean(
            input, [1, 3, 32, 32], new OnnxModel.MeanOutput("output0", output, [1, 7, Anchors], constant, scale));
    }

    /// <summary>
    /// The front-128 BlazeFace head: box 0 is a 20-pixel face whose logit is <c>-2 + 10 * mean</c>, and
    /// every other box scores -100.
    /// </summary>
    private static byte[] Face(ulong input, ulong output)
    {
        var boxes = new float[896 * 16];
        boxes[2] = 20;
        boxes[3] = 20;
        var scores = new float[896];
        Array.Fill(scores, -100f);
        scores[0] = -2;
        var scoreScale = new float[896];
        scoreScale[0] = 10;
        return OnnxModel.ConstantPlusMean(
            input,
            [1, 3, 128, 128],
            new OnnxModel.MeanOutput("boxes", output, [1, 896, 16], boxes, new float[896 * 16]),
            new OnnxModel.MeanOutput("scores", output, [1, 896, 1], scores, scoreScale));
    }

    private static Detection DetectObject(byte[] model, IVideoFrame frame)
    {
        using var detector = Yolov8Detector.Create(new CpuInferenceSession(model));
        return Assert.Single(detector.Detect(frame));
    }

    private static FaceDetection DetectFace(byte[] model, IVideoFrame frame)
    {
        using var detector = BlazeFaceDetector.Create(new CpuInferenceSession(model));
        return Assert.Single(detector.Detect(frame));
    }

    private static async Task<IReadOnlyList<TResult>> ThroughTheOperator<TResult>(IImageModel<IReadOnlyList<TResult>> model, IVideoFrame frame)
    {
        var results = new List<InferenceResult<IReadOnlyList<TResult>>>();
        bool sent = false;
        var graph = new GraphRunner();
        graph.Pipeline(new SourceNode<IVideoFrame>("frame", _ =>
            {
                IVideoFrame? next = sent ? null : frame.AddRef();
                sent = true;
                return ValueTask.FromResult(next);
            }))
            .Infer("model", model, results.Add)
            .To(new SinkNode<IVideoFrame>("trunk", (_, _) => ValueTask.CompletedTask, holding: FrameHolding.InFlight));
        await graph.RunAsync(CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(InferencePath.Host, result.Path);
        return result.Result;
    }

    /// <summary>The same box; the score as close as an fp16 output rounds it.</summary>
    private static void AssertClose(Detection expected, Detection actual)
    {
        Assert.Equal(expected with { Confidence = 0 }, actual with { Confidence = 0 });
        Assert.Equal(expected.Confidence, actual.Confidence, 1e-3f);
    }

    /// <summary>
    /// The same face, to the rounding of the score-weighted mean that merges it; the score as close
    /// as an fp16 output rounds it.
    /// </summary>
    private static void AssertClose(FaceDetection expected, FaceDetection actual)
    {
        float[] Positions(FaceDetection face) =>
            [face.X, face.Y, face.Width, face.Height, .. face.Keypoints.SelectMany(k => new[] { k.X, k.Y })];

        Assert.Equal(Positions(expected), Positions(actual), (a, b) => Math.Abs(a - b) <= 1e-4f);
        Assert.Equal(expected.Confidence, actual.Confidence, 1e-3f);
    }

    private static CpuVideoFrame Solid(int width, int height, byte b, byte g, byte r) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32, width, height, TimeSpan.Zero, TimeSpan.FromMilliseconds(40), (b, g, r),
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
}
