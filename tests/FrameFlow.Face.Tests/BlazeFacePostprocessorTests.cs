using FrameFlow.Inference;
using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>
/// Exercises the SSD decode against hand-built box/score tensors for the
/// front-128 model: anchor-relative box math, the six keypoints, the
/// sigmoid score gate, the letterboxed ROI → source-pixel mapping, and NMS.
/// </summary>
public sealed class BlazeFacePostprocessorTests
{
    private static readonly BlazeFaceModelDescriptor D = BlazeFaceModelDescriptor.Front128;

    [Fact]
    public void Decode_SingleFace_MapsBoxAndKeypointsThroughTheLetterbox()
    {
        var boxes = new float[D.BoxElementCount];
        var scores = AllNegative();

        // Anchor 0 is at normalized centre (0.03125, 0.03125), unit size.
        scores[0] = 100f;                       // sigmoid → ~1
        SetBox(boxes, 0, dx: 0, dy: 0, w: 32, h: 32, keypointRaw: 0);
        // normW = 32 / scale(128) = 0.25.

        // A 100 x 50 ROI letterboxes into the square input as a 100 x 100 square centred on
        // (60, 45): normalized (u, v) lands at (60 + (u - 0.5)·100, 45 + (v - 0.5)·100).
        var roi = new FaceRoi(X: 10, Y: 20, Width: 100, Height: 50);
        var faces = new BlazeFacePostprocessor(D).Decode(boxes, scores, roi);

        var face = Assert.Single(faces);
        Assert.True(face.Confidence > 0.99f, $"confidence was {face.Confidence}");

        // Box: centre stays at the anchor centre (dx=dy=0), and a square stays square.
        Assert.Equal(25f, face.Width, 3);       // 0.25 · 100
        Assert.Equal(25f, face.Height, 3);      // 0.25 · 100, not 0.25 · 50
        Assert.Equal(0.625f, face.X, 3);        // 60 + (0.03125 - 0.125 - 0.5)·100
        Assert.Equal(-14.375f, face.Y, 3);      // 45 + (0.03125 - 0.125 - 0.5)·100

        // All six keypoints were raw-0 → each sits at the anchor centre mapped to source.
        Assert.Equal(FaceDetection.KeypointCount, face.Keypoints.Count);
        var nose = face.Keypoint(FaceKeypoint.Nose);
        Assert.Equal(13.125f, nose.X, 3);       // 60 + (0.03125 - 0.5)·100
        Assert.Equal(-1.875f, nose.Y, 3);       // 45 + (0.03125 - 0.5)·100
    }

    [Fact]
    public void Decode_ThroughATransform_IsDecodeThroughTheRoiThatMadeIt()
    {
        var boxes = new float[D.BoxElementCount];
        var scores = AllNegative();
        scores[0] = 100f;
        SetBox(boxes, 0, dx: 3, dy: -2, w: 20, h: 24, keypointRaw: 5);
        var roi = new FaceRoi(0, 0, 1280, 720);
        var transform = ImageToTensor.Transform(
            RotatedRect.FromBounds(0, 0, 1280, 720), new ImageToTensorOptions(128, 128) { Fit = ImageFit.Letterbox });
        var post = new BlazeFacePostprocessor(D);

        var viaRoi = Assert.Single(post.Decode(boxes, scores, roi));
        var viaTransform = Assert.Single(post.Decode(boxes, scores, transform));

        Assert.Equal(
            (viaRoi.X, viaRoi.Y, viaRoi.Width, viaRoi.Height),
            (viaTransform.X, viaTransform.Y, viaTransform.Width, viaTransform.Height));
        Assert.Equal(viaRoi.Keypoints, viaTransform.Keypoints);
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
    public void WeightedSuppression_AveragesOverlappingFaces_ByScore()
    {
        var boxes = new float[D.BoxElementCount];
        var scores = AllNegative();

        // Anchors 0 and 1 share a cell centre, (4, 4) px on a 128 ROI. Face a: score 1, box x
        // from -12, keypoints at (4, 4). Face b: score 0.5, 4 px to the right, keypoints at
        // (16, 16). IoU 28/36 is over 0.3, so they merge.
        scores[0] = 100f;
        scores[1] = 0f;
        SetBox(boxes, 0, dx: 0, dy: 0, w: 32, h: 32, keypointRaw: 0);
        SetBox(boxes, 1, dx: 4, dy: 0, w: 32, h: 32, keypointRaw: 12);

        var face = Assert.Single(new BlazeFacePostprocessor(D).Decode(boxes, scores, FullRoi()));

        const float keypoint = (4f * 1 + 16f * 0.5f) / 1.5f;
        Assert.Equal(1f, face.Confidence, 3);
        Assert.Equal((-12f * 1 + -8f * 0.5f) / 1.5f, face.X, 3);
        Assert.Equal(-12f, face.Y, 3);
        Assert.Equal(32f, face.Width, 3);
        Assert.Equal(32f, face.Height, 3);
        Assert.All(face.Keypoints, k =>
        {
            Assert.Equal(keypoint, k.X, 3);
            Assert.Equal(keypoint, k.Y, 3);
        });
    }

    [Fact]
    public void HardSuppression_KeepsTheStrongerFace_Unchanged()
    {
        var boxes = new float[D.BoxElementCount];
        var scores = AllNegative();
        scores[0] = 100f;
        scores[1] = 0f;
        SetBox(boxes, 0, dx: 0, dy: 0, w: 32, h: 32, keypointRaw: 0);
        SetBox(boxes, 1, dx: 4, dy: 0, w: 32, h: 32, keypointRaw: 12);

        var post = new BlazeFacePostprocessor(D) { Suppression = FaceSuppression.Hard };
        var face = Assert.Single(post.Decode(boxes, scores, FullRoi()));

        Assert.Equal(-12f, face.X, 3);
        Assert.All(face.Keypoints, k => Assert.Equal(new FaceKeypoint2D(4f, 4f), k));
    }

    [Theory]
    [InlineData(FaceSuppression.Hard)]
    [InlineData(FaceSuppression.Weighted)]
    public void Suppression_KeepsFacesThatDoNotOverlap(FaceSuppression suppression)
    {
        var boxes = new float[D.BoxElementCount];
        var scores = AllNegative();

        // Anchor 0 sits in the top-left 8 px cell; anchor 100 is well away from it.
        scores[0] = 100f;
        scores[100] = 100f;
        SetBox(boxes, 0, dx: 0, dy: 0, w: 8, h: 8, keypointRaw: 0);
        SetBox(boxes, 100, dx: 0, dy: 0, w: 8, h: 8, keypointRaw: 0);

        var post = new BlazeFacePostprocessor(D) { Suppression = suppression };

        Assert.Equal(2, post.Decode(boxes, scores, FullRoi()).Count);
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

