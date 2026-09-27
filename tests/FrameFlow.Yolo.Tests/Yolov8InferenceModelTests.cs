using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference;
using FrameFlow.Media;
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// The detector as an <see cref="IImageModel{TResult}"/>: through <see cref="InferenceOperators"/>
/// it finds what <see cref="Yolov8Detector.Detect"/> finds on the same frame (#436).
/// </summary>
public sealed class Yolov8InferenceModelTests
{
    [Fact]
    public async Task ThroughTheOperator_TheDetectorFindsWhatDetectFinds()
    {
        using var detector = Yolov8Detector.Create(new OneBoxSession());
        using var frame = Blank(1280, 720);
        var expected = detector.Detect(frame);
        Assert.Single(expected);

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
        Assert.Equal(expected, result.Result);
    }

    private static CpuVideoFrame Blank(int width, int height) =>
        CpuVideoFrame.Create(PixelFormat.Bgra32, width, height, TimeSpan.Zero, TimeSpan.Zero, 0, static (_, _) => { });

    /// <summary>
    /// A stock YOLOv8 head whose output holds one box: anchor 0 centred at (320, 320) in the
    /// 640-pixel input, 100 by 50, class 0 at 0.9.
    /// </summary>
    private sealed class OneBoxSession : IInferenceSession
    {
        public IReadOnlyList<string> InputNames { get; } = ["images"];

        public IReadOnlyList<string> OutputNames { get; } = ["output0"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [[1, 3, 640, 640]];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [[1, 84, 8400]];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            var output = MemoryMarshal.Cast<byte, float>(MemoryMarshal.AsMemory(outputs["output0"].Bytes).Span);
            output.Clear();
            const int Anchors = 8400;
            output[(0 * Anchors) + 0] = 320;
            output[(1 * Anchors) + 0] = 320;
            output[(2 * Anchors) + 0] = 100;
            output[(3 * Anchors) + 0] = 50;
            output[(4 * Anchors) + 0] = 0.9f;
        }

        public void Dispose()
        {
        }
    }
}
