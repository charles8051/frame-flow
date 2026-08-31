using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>
/// Verifies the two things that make the face preprocessor differ from
/// the YOLO one: the <c>[-1,1]</c> normalization, and that it samples
/// only inside the <see cref="FaceRoi"/> crop.
/// </summary>
public sealed class BlazeFacePreprocessorTests
{
    [Fact]
    public void Preprocess_NormalizesToMinusOneOne_InChwOrder()
    {
        // Solid red (B=0, G=0, R=255).
        using var frame = FaceTestFrames.SolidBgra(8, 8, b: 0, g: 0, r: 255);
        var pre = new BlazeFacePreprocessor(inputSize: 4);
        var dst = new float[pre.InputElementCount];

        pre.Preprocess(frame, FaceRoi.Full(frame), dst);

        int channelStride = 4 * 4;
        // CHW: R plane first (255 → +1), then G (0 → -1), then B (0 → -1).
        Assert.Equal(1f, dst[0], 3);                       // R
        Assert.Equal(-1f, dst[channelStride], 3);          // G
        Assert.Equal(-1f, dst[channelStride * 2], 3);      // B
    }

    [Fact]
    public void Preprocess_MidGrayMapsNearZero()
    {
        using var frame = FaceTestFrames.SolidBgra(4, 4, b: 128, g: 128, r: 128);
        var pre = new BlazeFacePreprocessor(inputSize: 2);
        var dst = new float[pre.InputElementCount];

        pre.Preprocess(frame, FaceRoi.Full(frame), dst);

        // 128 / 127.5 - 1 ≈ 0.0039 — close to zero, and identical across channels.
        Assert.All(dst, v => Assert.True(MathF.Abs(v) < 0.01f, $"expected ~0, got {v}"));
    }

    [Fact]
    public void Preprocess_SamplesOnlyInsideRoi()
    {
        // Left half red (R=255), right half black (R=0).
        using var frame = FaceTestFrames.Bgra(8, 8, (x, _) =>
            x < 4 ? ((byte)0, (byte)0, (byte)255, (byte)255)
                  : ((byte)0, (byte)0, (byte)0, (byte)255));
        var pre = new BlazeFacePreprocessor(inputSize: 4);
        var dst = new float[pre.InputElementCount];

        // ROI over the right (black) half only → R plane should be all -1.
        pre.Preprocess(frame, new FaceRoi(4, 0, 4, 8), dst);
        int channelStride = 4 * 4;
        for (int i = 0; i < channelStride; i++)
            Assert.Equal(-1f, dst[i], 3);

        // ROI over the left (red) half → R plane should be all +1.
        pre.Preprocess(frame, new FaceRoi(0, 0, 4, 8), dst);
        for (int i = 0; i < channelStride; i++)
            Assert.Equal(1f, dst[i], 3);
    }

    [Fact]
    public void Preprocess_Nhwc_InterleavesChannels()
    {
        // Solid red (B=0, G=0, R=255).
        using var frame = FaceTestFrames.SolidBgra(4, 4, b: 0, g: 0, r: 255);
        var pre = new BlazeFacePreprocessor(inputSize: 2, layout: BlazeFaceInputLayout.Nhwc);
        var dst = new float[pre.InputElementCount];

        pre.Preprocess(frame, FaceRoi.Full(frame), dst);

        // HWC: first pixel is R,G,B interleaved → +1, -1, -1.
        Assert.Equal(1f, dst[0], 3);   // R
        Assert.Equal(-1f, dst[1], 3);  // G
        Assert.Equal(-1f, dst[2], 3);  // B
        // Second pixel starts at index 3, also red.
        Assert.Equal(1f, dst[3], 3);   // R
    }

    [Fact]
    public void Preprocess_ThrowsOnUndersizedDestination()
    {
        using var frame = FaceTestFrames.SolidBgra(4, 4, 0, 0, 0);
        var pre = new BlazeFacePreprocessor(inputSize: 4);
        Assert.Throws<ArgumentException>(
            () => pre.Preprocess(frame, FaceRoi.Full(frame), new float[10]));
    }
}
