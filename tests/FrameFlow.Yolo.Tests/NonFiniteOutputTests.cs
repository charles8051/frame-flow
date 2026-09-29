using System.Runtime.InteropServices;
using FrameFlow.Graph;
using FrameFlow.Inference;
using Xunit;

namespace FrameFlow.Yolo.Tests;

/// <summary>
/// A model output that is not a finite number is never a detection (#498). A 32 px, 3-class head
/// keeps each output hand-built: index = channel · anchors + anchor.
/// </summary>
public sealed class NonFiniteOutputTests
{
    private const int Size = 32;
    private static readonly YoloModelDescriptor Descriptor = new(Size, 3, ["a", "b", "c"]);

    [Fact]
    public void EveryValueNaN_DecodesToNothing()
    {
        var output = new float[Descriptor.OutputElementCount];
        Array.Fill(output, float.NaN);

        Assert.Empty(new Yolov8Postprocessor(Descriptor).Decode(output, 1f, 1f));
    }

    [Fact]
    public void FiniteBoxes_WithNaNScores_DecodeToNothing()
    {
        var output = Boxes();
        output.AsSpan(4 * Descriptor.AnchorCount).Fill(float.NaN);

        Assert.Empty(new Yolov8Postprocessor(Descriptor).Decode(output, 1f, 1f));
    }

    [Fact]
    public void ANaNInTheFirstClass_DoesNotHideAnotherClassesScore()
    {
        var output = Boxes();
        int a = Descriptor.AnchorCount;
        output[4 * a] = float.NaN;
        output[5 * a] = 0.8f;

        var detection = Assert.Single(new Yolov8Postprocessor(Descriptor).Decode(output, 1f, 1f));

        Assert.Equal(1, detection.ClassId);
        Assert.Equal(0.8f, detection.Confidence);
    }

    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void AnInfiniteScore_NeverWins(float infinity)
    {
        var output = Boxes();
        int a = Descriptor.AnchorCount;
        output[4 * a] = infinity;
        output[5 * a] = 0.8f;

        var detection = Assert.Single(new Yolov8Postprocessor(Descriptor).Decode(output, 1f, 1f));

        Assert.Equal(1, detection.ClassId);
        Assert.Equal(0.8f, detection.Confidence);
    }

    [Fact]
    public void OnlyInfiniteScores_DecodeToNothing()
    {
        var output = Boxes();
        output.AsSpan(4 * Descriptor.AnchorCount).Fill(float.PositiveInfinity);

        Assert.Empty(new Yolov8Postprocessor(Descriptor).Decode(output, 1f, 1f));
    }

    [Fact]
    public void ANegativeInfinityThreshold_StillDropsAnAnchorWithNoFiniteScore()
    {
        var output = Boxes();
        output.AsSpan(4 * Descriptor.AnchorCount).Fill(float.NaN);
        var post = new Yolov8Postprocessor(Descriptor) { ConfidenceThreshold = float.NegativeInfinity };

        Assert.Empty(post.Decode(output, 1f, 1f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void AFiniteScore_WithANaNBox_IsDropped(int boxChannel)
    {
        var output = Boxes();
        int a = Descriptor.AnchorCount;
        output[4 * a] = 0.9f;
        output[boxChannel * a] = float.NaN;

        Assert.Empty(new Yolov8Postprocessor(Descriptor).Decode(output, 1f, 1f));
    }

    [Fact]
    public void AModelThatReturnsNaN_FailsToLoad_NamingTheSession()
    {
        var session = new ScriptedSession(float.NaN);

        var error = Assert.Throws<InvalidOperationException>(() => Yolov8Detector.Create(session));

        Assert.Contains(nameof(ScriptedSession), error.Message);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void AModelThatReturnsNumbers_Loads()
    {
        using var detector = Yolov8Detector.Create(new ScriptedSession(0.5f));
    }

    /// <summary>A plausible box on every anchor and zero scores.</summary>
    private static float[] Boxes()
    {
        int a = Descriptor.AnchorCount;
        var output = new float[Descriptor.OutputElementCount];
        output.AsSpan(0 * a, a).Fill(16f);
        output.AsSpan(1 * a, a).Fill(16f);
        output.AsSpan(2 * a, a).Fill(8f);
        output.AsSpan(3 * a, a).Fill(8f);
        return output;
    }

    /// <summary>A stock 640, 80-class head that fills its output with one value on every run.</summary>
    private sealed class ScriptedSession(float value) : IInferenceSession
    {
        public bool Disposed { get; private set; }

        public IReadOnlyList<string> InputNames { get; } = ["images"];

        public IReadOnlyList<string> OutputNames { get; } = ["output0"];

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [[1, 3, 640, 640]];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [[1, 84, 8400]];

        public void Run(IReadOnlyDictionary<string, ICpuTensor> inputs, IReadOnlyDictionary<string, ICpuTensor> outputs) =>
            MemoryMarshal.Cast<byte, float>(MemoryMarshal.AsMemory(outputs["output0"].Bytes).Span).Fill(value);

        public void Dispose() => Disposed = true;
    }
}
