// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Face;

/// <summary>
/// Decodes BlazeFace's two raw output tensors — a box-regressor tensor
/// <c>[1, N, 16]</c> and a score tensor <c>[1, N, 1]</c> — into a
/// deduplicated list of <see cref="FaceDetection"/>s in source-image
/// pixel coordinates. The face analogue of
/// <c>FrameFlow.Yolo.Yolov8Postprocessor</c>.
/// </summary>
/// <remarks>
/// <para>
/// Steps, per MediaPipe's <c>TensorsToDetectionsCalculator</c>:
/// </para>
/// <list type="number">
/// <item><description>Score = <c>sigmoid(clip(raw, ±ScoreClipThreshold))</c>; drop below <see cref="MinScore"/>.</description></item>
/// <item><description>Decode box + 6 keypoints as offsets against the descriptor's anchor at that index (<c>reverse_output_order</c>: x before y).</description></item>
/// <item><description>Map the normalized <c>[0,1]</c> result into the source frame via the <see cref="FaceRoi"/>.</description></item>
/// <item><description>Greedy NMS by score with <see cref="IoUThreshold"/>.</description></item>
/// </list>
/// <para>
/// <b>Backend-agnostic.</b> Reads caller-supplied
/// <see cref="ReadOnlySpan{Single}"/>s, so CUDA callers can download into
/// host buffers and DML/CPU callers pass the output tensors' spans
/// directly.
/// </para>
/// </remarks>
public sealed class BlazeFacePostprocessor
{
    private readonly BlazeFaceModelDescriptor _descriptor;

    /// <summary>Minimum sigmoid score to keep a face. Default 0.5 (MediaPipe front default).</summary>
    public float MinScore { get; init; } = 0.5f;

    /// <summary>Raw-logit clamp before sigmoid, matching MediaPipe's <c>score_clipping_thresh</c>. Default 100.</summary>
    public float ScoreClipThreshold { get; init; } = 100f;

    /// <summary>IoU threshold for NMS (boxes above this are suppressed). Default 0.3.</summary>
    public float IoUThreshold { get; init; } = 0.3f;

    /// <summary>Maximum faces to retain after NMS. Default 32.</summary>
    public int MaxFaces { get; init; } = 32;

    /// <summary>Builds a postprocessor for the supplied model shape (defaults to the front-128 model).</summary>
    public BlazeFacePostprocessor(BlazeFaceModelDescriptor? descriptor = null)
    {
        _descriptor = descriptor ?? BlazeFaceModelDescriptor.Front128;
    }

    /// <summary>
    /// Decodes the raw box + score tensors into faces in source-image
    /// pixel coordinates. <paramref name="roi"/> is the same region handed
    /// to the preprocessor.
    /// </summary>
    public List<FaceDetection> Decode(
        ReadOnlySpan<float> boxes,
        ReadOnlySpan<float> scores,
        FaceRoi roi)
    {
        int n = _descriptor.NumBoxes;
        int numCoords = _descriptor.NumCoords;
        int numKeypoints = _descriptor.NumKeypoints;
        float scale = _descriptor.CoordinateScale;
        var anchors = _descriptor.Anchors;

        if (boxes.Length < _descriptor.BoxElementCount)
        {
            throw new ArgumentException(
                $"Box span has {boxes.Length} elements; this model expects at least "
                    + $"{_descriptor.BoxElementCount} ([1,{n},{numCoords}]).",
                nameof(boxes));
        }
        if (scores.Length < _descriptor.ScoreElementCount)
        {
            throw new ArgumentException(
                $"Score span has {scores.Length} elements; this model expects at least "
                    + $"{_descriptor.ScoreElementCount} ([1,{n},1]).",
                nameof(scores));
        }

        var candidates = new List<FaceDetection>(capacity: 64);
        for (int i = 0; i < n; i++)
        {
            float score = Sigmoid(Clip(scores[i], ScoreClipThreshold));
            if (score < MinScore)
                continue;

            var anchor = anchors[i];
            int b = i * numCoords;

            // reverse_output_order: columns are x, y, w, h.
            float xCenter = boxes[b + 0] / scale * anchor.Width + anchor.XCenter;
            float yCenter = boxes[b + 1] / scale * anchor.Height + anchor.YCenter;
            float w = boxes[b + 2] / scale * anchor.Width;
            float h = boxes[b + 3] / scale * anchor.Height;

            // Normalized [0,1] model-space box corner + size → source pixels.
            float nx = xCenter - w / 2f;
            float ny = yCenter - h / 2f;
            var (srcX, srcY) = roi.ToSource(nx, ny);
            float srcW = w * roi.Width;
            float srcH = h * roi.Height;

            var keypoints = new FaceKeypoint2D[numKeypoints];
            for (int k = 0; k < numKeypoints; k++)
            {
                int kp = b + 4 + k * 2;
                float kx = boxes[kp + 0] / scale * anchor.Width + anchor.XCenter;
                float ky = boxes[kp + 1] / scale * anchor.Height + anchor.YCenter;
                var (skx, sky) = roi.ToSource(kx, ky);
                keypoints[k] = new FaceKeypoint2D(skx, sky);
            }

            candidates.Add(new FaceDetection(score, srcX, srcY, srcW, srcH, keypoints));
        }

        return NonMaxSuppression(candidates);
    }

    /// <summary>Greedy single-class NMS: sort by score, suppress later boxes over the IoU threshold.</summary>
    private List<FaceDetection> NonMaxSuppression(List<FaceDetection> candidates)
    {
        candidates.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        var kept = new List<FaceDetection>(capacity: Math.Min(candidates.Count, MaxFaces));
        var suppressed = new bool[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            if (suppressed[i])
                continue;

            kept.Add(candidates[i]);
            if (kept.Count >= MaxFaces)
                break;

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed[j])
                    continue;
                if (IoU(candidates[i], candidates[j]) >= IoUThreshold)
                    suppressed[j] = true;
            }
        }

        return kept;
    }

    private static float IoU(FaceDetection a, FaceDetection b)
    {
        float ax2 = a.X + a.Width;
        float ay2 = a.Y + a.Height;
        float bx2 = b.X + b.Width;
        float by2 = b.Y + b.Height;

        float interX1 = MathF.Max(a.X, b.X);
        float interY1 = MathF.Max(a.Y, b.Y);
        float interX2 = MathF.Min(ax2, bx2);
        float interY2 = MathF.Min(ay2, by2);

        if (interX2 <= interX1 || interY2 <= interY1)
            return 0f;

        float interArea = (interX2 - interX1) * (interY2 - interY1);
        float unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
        return interArea / unionArea;
    }

    private static float Clip(float v, float threshold)
        => v < -threshold ? -threshold : (v > threshold ? threshold : v);

    private static float Sigmoid(float v) => 1f / (1f + MathF.Exp(-v));
}
