using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference;
using FrameFlow.Media;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// The thresholds a detector is built with reach its decode (#23). The session writes a fixed head
/// output, so each case is exact: a 640x640 frame maps model pixels to frame pixels one to one.
/// </summary>
public sealed class YoloThresholdsTests
{
    private const int Anchors = 8400;
    private const int Channels = 84;

    [Fact]
    public void TheDefaults_AreThePostprocessorsDefaults()
    {
        var postprocessor = new Yolov8Postprocessor();

        Assert.Equal(postprocessor.ConfidenceThreshold, YoloThresholds.Default.Confidence);
        Assert.Equal(postprocessor.IoUThreshold, YoloThresholds.Default.IoU);
        Assert.Equal(postprocessor.MaxDetections, YoloThresholds.Default.MaxDetections);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void AConfidenceOutsideZeroToOne_IsRefused(float value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloThresholds { Confidence = value });

    [Theory]
    [InlineData(0f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void AnIoUOutsideZeroToOne_IsRefused(float value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloThresholds { IoU = value });

    [Fact]
    public void NoBoxes_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloThresholds { MaxDetections = 0 });

    [Fact]
    public void ByDefault_ABoxBelowAQuarter_IsDropped()
    {
        using var detector = Detector([new(100, 100, 50, 50, 0.15f)], thresholds: null);

        Assert.Empty(Detect(detector));
    }

    [Fact]
    public void ALowerFloor_KeepsIt()
    {
        using var detector = Detector([new(100, 100, 50, 50, 0.15f)], new YoloThresholds { Confidence = 0.1f });

        var box = Assert.Single(Detect(detector));
        Assert.Equal(0.15f, box.Confidence);
        Assert.Equal(new RectangleF(75, 75, 50, 50), new RectangleF(box.X, box.Y, box.Width, box.Height));
    }

    [Fact]
    public void AHigherIoU_KeepsTwoOverlappingBoxes()
    {
        // Two 100x100 boxes 25 px apart overlap by 75x100: IoU 7500 / 12500 = 0.6.
        Candidate[] overlapping = [new(200, 200, 100, 100, 0.9f), new(225, 200, 100, 100, 0.8f)];

        using (var byDefault = Detector(overlapping, thresholds: null))
            Assert.Single(Detect(byDefault));

        using var looser = Detector(overlapping, new YoloThresholds { IoU = 0.7f });
        Assert.Equal([0.9f, 0.8f], Detect(looser).Select(d => d.Confidence));
    }

    [Fact]
    public void MaxDetections_KeepsTheStrongest()
    {
        Candidate[] apart = [new(100, 100, 20, 20, 0.5f), new(300, 300, 20, 20, 0.9f), new(500, 500, 20, 20, 0.7f)];

        using var detector = Detector(apart, new YoloThresholds { MaxDetections = 2 });

        Assert.Equal([0.9f, 0.7f], Detect(detector).Select(d => d.Confidence));
    }

    [Fact]
    public async Task CreateAsync_PassesThemOn()
    {
        using var detector = await Yolov8Detector.CreateAsync(
            sessionFactory: _ => new ScriptedSession([new(100, 100, 50, 50, 0.15f)]),
            overrideModelPath: "model.onnx",
            thresholds: new YoloThresholds { Confidence = 0.1f });

        Assert.Single(Detect(detector));
    }

    private static Yolov8Detector Detector(Candidate[] candidates, YoloThresholds? thresholds) =>
        Yolov8Detector.Create(new ScriptedSession(candidates), thresholds: thresholds);

    private static List<Detection> Detect(Yolov8Detector detector)
    {
        using var frame = CpuVideoFrame.Create(
            PixelFormat.Bgra32, 640, 640, TimeSpan.Zero, TimeSpan.Zero, 0, static (_, _) => { });
        return detector.Detect(frame);
    }

    /// <summary>One anchor of the head: a centre-size box in model pixels and its class-0 score.</summary>
    private readonly record struct Candidate(float Cx, float Cy, float W, float H, float Score);

    private readonly record struct RectangleF(float X, float Y, float Width, float Height);

    /// <summary>A stock 640, 80-class head whose every run writes <c>candidates</c> into its first anchors.</summary>
    private sealed class ScriptedSession(Candidate[] candidates) : IInferenceSession
    {
        public IReadOnlyList<string> InputNames { get; } = ["images"];

        public IReadOnlyList<string> OutputNames { get; } = ["output0"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [[1, 3, 640, 640]];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [[1, Channels, Anchors]];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            var head = MemoryMarshal.Cast<byte, float>(MemoryMarshal.AsMemory(outputs["output0"].Bytes).Span);
            head.Clear();
            for (int anchor = 0; anchor < candidates.Length; anchor++)
            {
                var c = candidates[anchor];
                head[0 * Anchors + anchor] = c.Cx;
                head[1 * Anchors + anchor] = c.Cy;
                head[2 * Anchors + anchor] = c.W;
                head[3 * Anchors + anchor] = c.H;
                head[4 * Anchors + anchor] = c.Score;
            }
        }

        public void Dispose() { }
    }
}
