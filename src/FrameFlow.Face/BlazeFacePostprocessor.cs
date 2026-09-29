// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Inference;

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
/// <item><description>Score = <c>sigmoid(clip(raw, ±ScoreClipThreshold))</c>; drop below <see cref="MinScore"/>, and a score, box or keypoint that is not a finite number.</description></item>
/// <item><description>Decode box + 6 keypoints as offsets against the descriptor's anchor at that index (<c>reverse_output_order</c>: x before y).</description></item>
/// <item><description>Map the normalized <c>[0,1]</c> result into the source frame through the preprocessor's letterbox.</description></item>
/// <item><description>Suppress overlapping detections by score with <see cref="IoUThreshold"/>, as <see cref="Suppression"/> says.</description></item>
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
    private readonly ImageToTensorOptions _input;

    /// <summary>Minimum sigmoid score to keep a face. Default 0.5 (MediaPipe front default).</summary>
    public float MinScore { get; init; } = 0.5f;

    /// <summary>Raw-logit clamp before sigmoid, matching MediaPipe's <c>score_clipping_thresh</c>. Default 100.</summary>
    public float ScoreClipThreshold { get; init; } = 100f;

    /// <summary>IoU threshold for NMS (boxes above this are suppressed). Default 0.3.</summary>
    public float IoUThreshold { get; init; } = 0.3f;

    /// <summary>How overlapping detections merge. Default <see cref="FaceSuppression.Weighted"/>, as MediaPipe's face detector.</summary>
    public FaceSuppression Suppression { get; init; } = FaceSuppression.Weighted;

    /// <summary>Maximum faces to retain after NMS. Default 32.</summary>
    public int MaxFaces { get; init; } = 32;

    /// <summary>Builds a postprocessor for the supplied model shape (defaults to the front-128 model).</summary>
    public BlazeFacePostprocessor(BlazeFaceModelDescriptor? descriptor = null)
    {
        _descriptor = descriptor ?? BlazeFaceModelDescriptor.Front128;
        _input = BlazeFacePreprocessor.OptionsFor(_descriptor.InputSize, _descriptor.InputLayout);
    }

    /// <summary>
    /// Decodes the raw box + score tensors into faces in source-image
    /// pixel coordinates. <paramref name="roi"/> is the same region handed
    /// to <see cref="BlazeFacePreprocessor.Preprocess"/>, whose letterbox this
    /// undoes.
    /// </summary>
    /// <exception cref="ArgumentException">The ROI has no area.</exception>
    public List<FaceDetection> Decode(
        ReadOnlySpan<float> boxes,
        ReadOnlySpan<float> scores,
        FaceRoi roi) =>
        Decode(boxes, scores, ImageToTensor.Transform(roi.ToCrop(), _input));

    /// <summary>
    /// Decodes the raw box + score tensors into faces in source-image
    /// pixel coordinates, through <paramref name="transform"/>: the mapping
    /// <see cref="BlazeFacePreprocessor.Preprocess"/> or
    /// <see cref="ImageToTensor"/> returned for the input. The crop it maps
    /// is expected unrotated, so a box stays axis-aligned.
    /// </summary>
    public List<FaceDetection> Decode(
        ReadOnlySpan<float> boxes,
        ReadOnlySpan<float> scores,
        TensorTransform transform)
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
            // A raw score that is not a finite number is dropped before the clip, which would
            // turn an infinity into a confident face (#498).
            if (!float.IsFinite(scores[i]))
                continue;
            float score = Sigmoid(Clip(scores[i], ScoreClipThreshold));
            if (!(score >= MinScore))
                continue;

            var anchor = anchors[i];
            int b = i * numCoords;

            // reverse_output_order: columns are x, y, w, h.
            float xCenter = boxes[b + 0] / scale * anchor.Width + anchor.XCenter;
            float yCenter = boxes[b + 1] / scale * anchor.Height + anchor.YCenter;
            float w = boxes[b + 2] / scale * anchor.Width;
            float h = boxes[b + 3] / scale * anchor.Height;

            // Normalized [0,1] model-space box corners → source pixels.
            var (x0, y0) = transform.ToFrame(xCenter - w / 2f, yCenter - h / 2f);
            var (x1, y1) = transform.ToFrame(xCenter + w / 2f, yCenter + h / 2f);

            var keypoints = new FaceKeypoint2D[numKeypoints];
            bool finite = float.IsFinite(x0) && float.IsFinite(y0) && float.IsFinite(x1) && float.IsFinite(y1);
            for (int k = 0; k < numKeypoints; k++)
            {
                int kp = b + 4 + k * 2;
                float kx = boxes[kp + 0] / scale * anchor.Width + anchor.XCenter;
                float ky = boxes[kp + 1] / scale * anchor.Height + anchor.YCenter;
                var (skx, sky) = transform.ToFrame(kx, ky);
                keypoints[k] = new FaceKeypoint2D(skx, sky);
                finite &= float.IsFinite(skx) && float.IsFinite(sky);
            }

            // A face that is not a number cannot be placed or suppressed. Two finite corners can
            // still be too far apart for a float, so the size is checked too.
            float width = MathF.Abs(x1 - x0);
            float height = MathF.Abs(y1 - y0);
            if (!(finite && float.IsFinite(width) && float.IsFinite(height)))
                continue;

            candidates.Add(new FaceDetection(score, MathF.Min(x0, x1), MathF.Min(y0, y1), width, height, keypoints));
        }

        return Suppression == FaceSuppression.Weighted
            ? WeightedSuppression(candidates)
            : NonMaxSuppression(candidates);
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

    /// <summary>
    /// Weighted NMS, as MediaPipe's <c>NonMaxSuppressionCalculator</c> with <c>WEIGHTED</c>: take the
    /// highest-scoring detection left and every one left that overlaps it at the IoU threshold, and
    /// replace them with their score-weighted average box and keypoints under the highest score.
    /// </summary>
    private List<FaceDetection> WeightedSuppression(List<FaceDetection> candidates)
    {
        candidates.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        var kept = new List<FaceDetection>(capacity: Math.Min(candidates.Count, MaxFaces));
        var merged = new bool[candidates.Count];
        int keypointCount = _descriptor.NumKeypoints;

        for (int i = 0; i < candidates.Count && kept.Count < MaxFaces; i++)
        {
            if (merged[i])
                continue;

            var top = candidates[i];
            float total = 0, left = 0, upper = 0, right = 0, lower = 0;
            var sums = new float[2 * keypointCount];
            for (int j = i; j < candidates.Count; j++)
            {
                if (merged[j] || (j != i && IoU(top, candidates[j]) < IoUThreshold))
                    continue;

                merged[j] = true;
                var c = candidates[j];
                float weight = c.Confidence;
                total += weight;
                left += c.X * weight;
                upper += c.Y * weight;
                right += (c.X + c.Width) * weight;
                lower += (c.Y + c.Height) * weight;
                for (int k = 0; k < keypointCount; k++)
                {
                    sums[2 * k] += c.Keypoints[k].X * weight;
                    sums[2 * k + 1] += c.Keypoints[k].Y * weight;
                }
            }

            var keypoints = new FaceKeypoint2D[keypointCount];
            for (int k = 0; k < keypointCount; k++)
                keypoints[k] = new FaceKeypoint2D(sums[2 * k] / total, sums[2 * k + 1] / total);

            kept.Add(new FaceDetection(
                top.Confidence,
                left / total,
                upper / total,
                (right - left) / total,
                (lower - upper) / total,
                keypoints));
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
