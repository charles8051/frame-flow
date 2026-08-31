using FrameFlow.Inference;
using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>
/// Covers the front-128 descriptor's shape contract and its shape-based
/// (not order-based) output identification, which is what lets it accept
/// community ONNX exports that disagree on output order.
/// </summary>
public sealed class BlazeFaceModelDescriptorTests
{
    [Fact]
    public void Front128_HasExpectedShape()
    {
        var d = BlazeFaceModelDescriptor.Front128;
        Assert.Equal(128, d.InputSize);
        Assert.Equal(896, d.NumBoxes);
        Assert.Equal(6, d.NumKeypoints);
        Assert.Equal(16, d.NumCoords);       // 4 + 6·2
        Assert.Equal(128f, d.CoordinateScale);
        Assert.Equal(896 * 16, d.BoxElementCount);
        Assert.Equal(896, d.ScoreElementCount);
        Assert.Equal(896, d.Anchors.Count);
    }

    [Fact]
    public void ValidateSession_AcceptsWellShapedSession()
    {
        var session = new FakeSession(
            input: [1, 3, 128, 128],
            outputs: [[1, 896, 16], [1, 896, 1]]);
        // Should not throw.
        BlazeFaceModelDescriptor.Front128.ValidateSession(session);
    }

    [Fact]
    public void IdentifyOutputs_ResolvesByShape_RegardlessOfOrder()
    {
        // Scores first, boxes second — the reverse of the "usual" order.
        var shapes = new IReadOnlyList<long>[] { [1, 896, 1], [1, 896, 16] };
        var (boxIdx, scoreIdx) = BlazeFaceModelDescriptor.Front128.IdentifyOutputs(shapes);
        Assert.Equal(1, boxIdx);
        Assert.Equal(0, scoreIdx);
    }

    [Fact]
    public void ValidateSession_ThrowsOnWrongAnchorCount()
    {
        var session = new FakeSession(
            input: [1, 3, 128, 128],
            outputs: [[1, 512, 16], [1, 512, 1]]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => BlazeFaceModelDescriptor.Front128.ValidateSession(session));
        Assert.Contains("anchor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSession_ThrowsOnSingleOutput()
    {
        var session = new FakeSession(
            input: [1, 3, 128, 128],
            outputs: [[1, 896, 16]]);
        Assert.Throws<InvalidOperationException>(
            () => BlazeFaceModelDescriptor.Front128.ValidateSession(session));
    }

    [Fact]
    public void FromSession_PicksNchwForChannelFirstInput()
    {
        var session = new FakeSession(
            input: [1, 3, 128, 128],
            outputs: [[1, 896, 16], [1, 896, 1]]);
        var d = BlazeFaceModelDescriptor.FromSession(session);
        Assert.Equal(BlazeFaceInputLayout.Nchw, d.InputLayout);
    }

    [Fact]
    public void FromSession_PicksNhwcForChannelLastInput()
    {
        // The Unity blaze_face_short_range.onnx layout.
        var session = new FakeSession(
            input: [1, 128, 128, 3],
            outputs: [[1, 896, 16], [1, 896, 1]]);
        var d = BlazeFaceModelDescriptor.FromSession(session);
        Assert.Equal(BlazeFaceInputLayout.Nhwc, d.InputLayout);
        Assert.Equal(896, d.NumBoxes); // same anchors as NCHW
    }

    [Fact]
    public void Front128Nhwc_ValidatesChannelLastSession()
    {
        var session = new FakeSession(
            input: [1, 128, 128, 3],
            outputs: [[1, 896, 1], [1, 896, 16]]); // outputs in reverse order too
        BlazeFaceModelDescriptor.Front128Nhwc.ValidateSession(session);
    }

    [Fact]
    public void Front128_RejectsChannelLastInput()
    {
        var session = new FakeSession(
            input: [1, 128, 128, 3],
            outputs: [[1, 896, 16], [1, 896, 1]]);
        Assert.Throws<InvalidOperationException>(
            () => BlazeFaceModelDescriptor.Front128.ValidateSession(session));
    }

    [Fact]
    public void FromSession_ThrowsOnUnknownInput()
    {
        var session = new FakeSession(
            input: [1, 3, 256, 256],
            outputs: [[1, 896, 16], [1, 896, 1]]);
        Assert.Throws<InvalidOperationException>(
            () => BlazeFaceModelDescriptor.FromSession(session));
    }

    [Fact]
    public void ValidateSession_ThrowsOnWrongInputSize()
    {
        var session = new FakeSession(
            input: [1, 3, 256, 256],
            outputs: [[1, 896, 16], [1, 896, 1]]);
        Assert.Throws<InvalidOperationException>(
            () => BlazeFaceModelDescriptor.Front128.ValidateSession(session));
    }

    private sealed class FakeSession(IReadOnlyList<long> input, IReadOnlyList<IReadOnlyList<long>> outputs)
        : IInferenceSession
    {
        public IReadOnlyList<string> InputNames { get; } = ["input"];

        public IReadOnlyList<string> OutputNames { get; } =
            outputs.Select((_, i) => $"output{i}").ToArray();

        public IReadOnlyList<IReadOnlyList<long>> InputShapes { get; } = [input];

        public IReadOnlyList<IReadOnlyList<long>> OutputShapes { get; } = outputs;

        public void Run(
            IReadOnlyDictionary<string, ICpuTensor> inputs,
            IReadOnlyDictionary<string, ICpuTensor> outputs)
        { }

        public void Dispose() { }
    }
}
