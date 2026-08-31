using FrameFlow.Graph;
using FrameFlow.Inference;
using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>
/// End-to-end wiring of <see cref="BlazeFaceDetector"/> over a no-op
/// two-output fake session: shape validation, the reused two-tensor
/// output binding, warmup, and the per-stage timing. No model file or
/// native runtime required.
/// </summary>
public sealed class BlazeFaceDetectorTests
{
    [Fact]
    public void Create_ValidatesWarmsUpAndDetects()
    {
        var session = new NegativeScoreSession();
        using var detector = BlazeFaceDetector.Create(session);

        // Warmup ran exactly one inference during construction.
        Assert.Equal(1, session.RunCount);

        using var frame = FaceTestFrames.SolidBgra(64, 64, b: 10, g: 20, r: 30);
        var faces = detector.Detect(frame, new FaceRoi(0, 0, 64, 64));

        // All scores forced negative → no faces survive the sigmoid gate.
        Assert.Empty(faces);
        Assert.Equal(2, session.RunCount);              // warmup + this Detect
        Assert.False(double.IsNaN(detector.LastInferenceMs));
        Assert.False(double.IsNaN(detector.LastPreprocessMs));
        Assert.False(double.IsNaN(detector.LastPostprocessMs));
    }

    [Fact]
    public void Create_AutoDetectsNhwcLayout_AndDetects()
    {
        // The Unity blaze_face_short_range.onnx layout: channel-last input.
        var session = new NegativeScoreSession(nhwc: true);
        using var detector = BlazeFaceDetector.Create(session);

        Assert.Equal(1, session.RunCount); // warmup

        using var frame = FaceTestFrames.SolidBgra(64, 64, b: 10, g: 20, r: 30);
        var faces = detector.Detect(frame, new FaceRoi(0, 0, 64, 64));

        Assert.Empty(faces);
        Assert.Equal(2, session.RunCount);
    }

    [Fact]
    public void Create_ThrowsAndDisposesSessionOnShapeMismatch()
    {
        // Single output → validation fails; the session must still be disposed.
        var session = new NegativeScoreSession(singleOutput: true);
        Assert.Throws<InvalidOperationException>(() => BlazeFaceDetector.Create(session));
        Assert.True(session.Disposed);
    }

    /// <summary>
    /// Two-output BlazeFace-shaped fake. Fills the score output with a
    /// large negative logit each Run so decode returns nothing, and counts
    /// runs so warmup + detect are observable.
    /// </summary>
    private sealed class NegativeScoreSession : IInferenceSession
    {
        private readonly bool _singleOutput;

        public NegativeScoreSession(bool singleOutput = false, bool nhwc = false)
        {
            _singleOutput = singleOutput;
            InputShapes = nhwc ? [[1, 128, 128, 3]] : [[1, 3, 128, 128]];
        }

        public int RunCount { get; private set; }

        public bool Disposed { get; private set; }

        public IReadOnlyList<string> InputNames { get; } = ["input"];

        public IReadOnlyList<string> OutputNames =>
            _singleOutput ? ["boxes"] : ["boxes", "scores"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; }

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes =>
            _singleOutput ? [[1, 896, 16]] : [[1, 896, 16], [1, 896, 1]];

        public void Run(
            IReadOnlyDictionary<string, ICpuTensor> inputs,
            IReadOnlyDictionary<string, ICpuTensor> outputs)
        {
            RunCount++;
            if (outputs.TryGetValue("scores", out var scores) && scores is CpuTensor<float> s)
                s.Span.Fill(-100f);
        }

        public void Dispose() => Disposed = true;
    }
}
