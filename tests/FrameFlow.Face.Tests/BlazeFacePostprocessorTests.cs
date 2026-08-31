using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>
/// Exercises the SSD decode against hand-built box/score tensors for the
/// front-128 model: anchor-relative box math, the six keypoints, the
/// sigmoid score gate, the ROI → source-pixel mapping, and greedy NMS.
/// </summary>
public sealed class BlazeFacePostprocessorTests
{
    private static readonly BlazeFaceModelDescriptor D = BlazeFaceModelDescriptor.Front128;

    [Fact]
    public void Decode_SingleFace_MapsBoxAndKeypointsToSource()
    {
        var boxes = new float[D.BoxElementCount];
        var scores = AllNegative();

        // Anchor 0 is at normalized centre (0.03125, 0.03125), unit size.
        scores[0] = 100f;                       // sigmoid → ~1
        SetBox(boxes, 0, dx: 0, dy: 0, w: 32, h: 32, keypointRaw: 0);
        // normW = 32 / scale(128) = 0.25.

        var roi = new FaceRoi(X: 10, Y: 20, Width: 100, Height: 50);
        var faces = new BlazeFacePostprocessor(D).Decode(boxes, scores, roi);

        var face = Assert.Single(faces);
        Assert.True(face.Confidence > 0.99f, $"confidence was {face.Confidence}");

        // Box: centre stays at the anchor centre (dx=dy=0), size 0.25 of the ROI.
        Assert.Equal(25f, face.Width, 3);       // 0.25 · 100
        Assert.Equal(12.5f, face.Height, 3);    // 0.25 · 50
        Assert.Equal(0.625f, face.X, 3);        // 10 + (0.03125-0.125)·100
        Assert.Equal(15.3125f, face.Y, 3);      // 20 + (0.03125-0.125)·50

        // All six keypoints were raw-0 → each sits at the anchor centre mapped to source.
        Assert.Equal(FaceDetection.KeypointCount, face.Keypoints.Count);
        var nose = face.Keypoint(FaceKeypoint.Nose);
        Assert.Equal(13.125f, nose.X, 3);       // 10 + 0.03125·100
        Assert.Equal(21.5625f, nose.Y, 3);      // 20 + 0.03125·50
    }

    [Fact]
    public void Decode_KeypointColumnsMapToNamedLandmarks()
    {
        var boxes = new float[D.BoxElementCount];
        var scores = AllNegative();
        scores[0] = 100f;
        SetBox(boxes, 0, dx: 0, dy: 0, w: 16, h: 16, keypointRaw: 0);

        // Give the LeftEye (keypoint index 1 → columns 6,7) a distinct offset.
        int b = 0 * D.NumCoords;
        boxes[b + 4 + 1 * 2 + 0] = 64f;         // kx raw → 64/128 = +0.5 from anchor x
        boxes[b + 4 + 1 * 2 + 1] = 0f;

        var roi = new FaceRoi(0, 0, 128, 128);
        var face = Assert.Single(new BlazeFacePostprocessor(D).Decode(boxes, scores, roi));

        var rightEye = face.Keypoint(FaceKeypoint.RightEye); // raw 0 → anchor centre
        var leftEye = face.Keypoint(FaceKeypoint.LeftEye);   // shifted +0.5 in x
        Assert.Equal(4f, rightEye.X, 3);                     // 0.03125·128
        Assert.Equal((0.03125f + 0.5f) * 128f, leftEye.X, 2);
    }

    [Fact]
    public void Decode_DropsFacesBelowScoreThreshold()
    {
        var boxes = new float[D.BoxElementCount];
        var scores = AllNegative();
        scores[0] = -1f;                        // sigmoid(-1) ≈ 0.269 < 0.5 default
        SetBox(boxes, 0, dx: 0, dy: 0, w: 32, h: 32, keypointRaw: 0);

        var faces = new BlazeFacePostprocessor(D).Decode(boxes, scores, FullRoi());
        Assert.Empty(faces);
    }

    [Fact]
    public void Decode_SuppressesOverlappingDuplicatesViaNms()
    {
        var boxes = new float[D.BoxElementCount];
        var scores = AllNegative();

        // Anchors 0 and 1 share the same cell centre; give them identical
        // boxes → IoU 1 → NMS must keep exactly one.
        scores[0] = 100f;
        scores[1] = 90f;
        SetBox(boxes, 0, dx: 0, dy: 0, w: 32, h: 32, keypointRaw: 0);
        SetBox(boxes, 1, dx: 0, dy: 0, w: 32, h: 32, keypointRaw: 0);

        var faces = new BlazeFacePostprocessor(D).Decode(boxes, scores, FullRoi());
        Assert.Single(faces);
    }

    [Fact]
    public void Decode_ThrowsWhenBoxSpanTooSmall()
    {
        var scores = AllNegative();
        Assert.Throws<ArgumentException>(
            () => new BlazeFacePostprocessor(D).Decode(new float[10], scores, FullRoi()));
    }

    /// <summary>A 128-square ROI matching the model input (1:1 normalized → pixel).</summary>
    private static FaceRoi FullRoi() => new(0, 0, 128, 128);

    private static float[] AllNegative()
    {
        var s = new float[D.ScoreElementCount];
        Array.Fill(s, -100f);
        return s;
    }

    private static void SetBox(float[] boxes, int anchor, float dx, float dy, float w, float h, float keypointRaw)
    {
        int b = anchor * D.NumCoords;
        boxes[b + 0] = dx;
        boxes[b + 1] = dy;
        boxes[b + 2] = w;
        boxes[b + 3] = h;
        for (int k = 0; k < D.NumKeypoints; k++)
        {
            boxes[b + 4 + k * 2 + 0] = keypointRaw;
            boxes[b + 4 + k * 2 + 1] = keypointRaw;
        }
    }
}

