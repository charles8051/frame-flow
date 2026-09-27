using FrameFlow.Graph;
using FrameFlow.Inference;
using FrameFlow.Media;
using Xunit;
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Face.Tests;

/// <summary>
/// The detector as an <see cref="IImageModel{TResult}"/>: through <see cref="InferenceOperators"/>
/// it finds what <see cref="BlazeFaceDetector.Detect(IVideoFrame)"/> finds on the same frame (#436).
/// </summary>
public sealed class BlazeFaceInferenceModelTests
{
    [Fact]
    public async Task ThroughTheOperator_TheDetectorFindsWhatDetectFinds()
    {
        using var detector = BlazeFaceDetector.Create(new OneFaceSession());
        using var frame = FaceTestFrames.SolidBgra(64, 64, b: 10, g: 20, r: 30);
        var expected = detector.Detect(frame);
        Assert.Single(expected);

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

        var face = Assert.Single(Assert.Single(results).Result);
        var want = expected[0];
        Assert.Equal((want.Confidence, want.X, want.Y, want.Width, want.Height), (face.Confidence, face.X, face.Y, face.Width, face.Height));
        Assert.Equal(want.Keypoints, face.Keypoints);
    }

    /// <summary>Anchor 0 holds one confident 20-pixel face; every other score is off.</summary>
    private sealed class OneFaceSession : IInferenceSession
    {
        public IReadOnlyList<string> InputNames { get; } = ["input"];

        public IReadOnlyList<string> OutputNames { get; } = ["boxes", "scores"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [[1, 3, 128, 128]];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [[1, 896, 16], [1, 896, 1]];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            var boxes = ((CpuTensor<float>)outputs["boxes"]).Span;
            var scores = ((CpuTensor<float>)outputs["scores"]).Span;
            boxes.Clear();
            scores.Fill(-100f);
            boxes[2] = 20;
            boxes[3] = 20;
            scores[0] = 10;
        }

        public void Dispose()
        {
        }
    }
}
