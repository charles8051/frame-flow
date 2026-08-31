using FrameFlow.Graph;
using FrameFlow.Inference;
using Xunit;

namespace FrameFlow.Yolo.Tests;

public sealed class YoloModelDescriptorTests
{
    [Theory]
    [InlineData(640, 8400)]
    [InlineData(512, 5376)]
    [InlineData(416, 3549)]
    [InlineData(320, 2100)]
    public void AnchorsFor_MatchesYolov8StrideStructure(int inputSize, int expected)
        => Assert.Equal(expected, YoloModelDescriptor.AnchorsFor(inputSize));

    [Theory]
    [InlineData(0)]
    [InlineData(-32)]
    [InlineData(100)] // not a multiple of 32
    public void Ctor_RejectsBadInputSize(int inputSize)
        => Assert.Throws<ArgumentException>(() => new YoloModelDescriptor(inputSize, 80));

    [Fact]
    public void Ctor_RejectsClassNameCountMismatch()
        => Assert.Throws<ArgumentException>(
            () => new YoloModelDescriptor(640, 80, new[] { "a", "b" }));

    [Fact]
    public void CocoDefault_Is640And80Classes()
    {
        var d = YoloModelDescriptor.CocoDefault;
        Assert.Equal(640, d.InputSize);
        Assert.Equal(80, d.ClassCount);
        Assert.Equal(8400, d.AnchorCount);
        Assert.Equal(84 * 8400, d.OutputElementCount);
    }

    [Theory]
    [InlineData(640, 80, 8400)]
    [InlineData(320, 80, 2100)]
    [InlineData(416, 1, 3549)] // person-only: single-class head
    public void FromSession_InfersShape(int size, int classes, int anchors)
    {
        var session = new ShapeOnlySession(
            input: [1, 3, size, size],
            output: [1, 4 + classes, anchors]);

        var d = YoloModelDescriptor.FromSession(session);

        Assert.Equal(size, d.InputSize);
        Assert.Equal(classes, d.ClassCount);
        Assert.Equal(anchors, d.AnchorCount);
    }

    [Fact]
    public void FromSession_ThrowsOnDynamicInput()
    {
        var session = new ShapeOnlySession(input: [1, 3, -1, -1], output: [1, 84, 8400]);
        Assert.Throws<InvalidOperationException>(() => YoloModelDescriptor.FromSession(session));
    }

    [Fact]
    public void FromSession_ThrowsOnAnchorMismatch()
    {
        // 320px input but 8400 anchors (the 640 count) — incoherent head.
        var session = new ShapeOnlySession(input: [1, 3, 320, 320], output: [1, 84, 8400]);
        Assert.Throws<InvalidOperationException>(() => YoloModelDescriptor.FromSession(session));
    }

    [Fact]
    public void FromSession_ThrowsOnNmsFreeHead()
    {
        // yolov10-style [1, 300, 6] is not a transposed [1, 4+C, A] head (ADR-0050 §5).
        var session = new ShapeOnlySession(input: [1, 3, 640, 640], output: [1, 300, 6]);
        Assert.Throws<InvalidOperationException>(() => YoloModelDescriptor.FromSession(session));
    }

    [Fact]
    public void TryDescribe_ReturnsTrueForValidHead()
    {
        var session = new ShapeOnlySession(input: [1, 3, 320, 320], output: [1, 84, 2100]);

        var ok = YoloModelDescriptor.TryDescribe(session, out var descriptor, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(320, descriptor!.InputSize);
        Assert.Equal(80, descriptor.ClassCount);
    }

    [Fact]
    public void TryDescribe_ReturnsFalseWithReasonForBadHead()
    {
        // NMS-free [1,300,6] head — rejected, but without throwing.
        var session = new ShapeOnlySession(input: [1, 3, 640, 640], output: [1, 300, 6]);

        var ok = YoloModelDescriptor.TryDescribe(session, out var descriptor, out var error);

        Assert.False(ok);
        Assert.Null(descriptor);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>Minimal IInferenceSession that only carries declared shapes.</summary>
    private sealed class ShapeOnlySession(long[] input, long[] output) : IInferenceSession
    {
        public IReadOnlyList<string> InputNames { get; } = ["images"];
        public IReadOnlyList<string> OutputNames { get; } = ["output0"];
        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [input];
        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = [output];

        public void Run(
            IReadOnlyDictionary<string, ICpuTensor> inputs,
            IReadOnlyDictionary<string, ICpuTensor> outputs)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}
