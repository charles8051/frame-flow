// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Yolo;

/// <summary>
/// Decodes a YOLOv8/v11 transposed detect-head output tensor into a
/// deduplicated list of <see cref="Detection"/>s.
/// </summary>
/// <remarks>
/// <para>
/// The output tensor is shape <c>[1, 4+C, A]</c>: <c>A</c> anchor
/// predictions, each predicting 4 box coordinates (cx, cy, w, h, in
/// model-space pixels) followed by <c>C</c> per-class confidence scores.
/// The model emits no objectness score — the confidence comes from the
/// max-over-classes value. Both <c>C</c> and <c>A</c> come from the
/// <see cref="YoloModelDescriptor"/> (ADR-0050 §1), so smaller-input and
/// reduced-class (person-only) models share this decoder.
/// </para>
/// <para>
/// <b>Backend-agnostic.</b> The postprocessor reads from a
/// caller-supplied <see cref="ReadOnlySpan{Single}"/>. CUDA-backed
/// callers download the model output into a host buffer and pass that;
/// CPU/DML-backed callers pass the output tensor's
/// <c>ReadOnlySpan&lt;float&gt;</c> directly, skipping the intermediate
/// buffer.
/// </para>
/// <para>
/// Postprocessing steps:
/// </para>
/// <list type="number">
/// <item><description>For each of the A anchors, find max class score and class id (scanning C classes).</description></item>
/// <item><description>Drop anchors below <see cref="ConfidenceThreshold"/>.</description></item>
/// <item><description>Run greedy NMS per class with <see cref="IoUThreshold"/>.</description></item>
/// <item><description>Map surviving box coords from model-space back to source-image-space via the scale factors from the preprocessor.</description></item>
/// </list>
/// </remarks>
public sealed class Yolov8Postprocessor
{
    private readonly YoloModelDescriptor _descriptor;

    // Class ids the per-anchor argmax scans. All classes by default; a class
    // allow-list (ADR-0050 §4) narrows it — e.g. [0] for person-only — which
    // collapses the per-anchor scan from C to |filter| (the CPU-side decode
    // win on postprocess-bound iGPUs, where a people-counter workload lives).
    private readonly int[] _classesToScan;

    /// <summary>Anchor count (predictions per frame), from the descriptor.</summary>
    public int AnchorCount => _descriptor.AnchorCount;

    /// <summary>Output channels: 4 box coords + C class scores, from the descriptor.</summary>
    public int ChannelCount => _descriptor.OutputChannelCount;

    /// <summary>Total elements in the output tensor.</summary>
    public int OutputElementCount => _descriptor.OutputElementCount;

    /// <summary>Minimum class confidence to keep a detection. Default 0.25.</summary>
    public float ConfidenceThreshold { get; init; } = 0.25f;

    /// <summary>IoU threshold for NMS (boxes above this are suppressed). Default 0.45.</summary>
    public float IoUThreshold { get; init; } = 0.45f;

    /// <summary>Maximum detections to retain after NMS. Default 100.</summary>
    public int MaxDetections { get; init; } = 100;

    /// <summary>Class ids the decoder scans per anchor — the allow-list, or all classes when unfiltered.</summary>
    public IReadOnlyList<int> DecodedClasses => _classesToScan;

    /// <summary>
    /// Builds a postprocessor for the supplied model shape. Defaults to the
    /// stock COCO/640 export. <paramref name="classFilter"/> is an optional
    /// class allow-list (ADR-0050 §4): when supplied, only those class
    /// columns are decoded (e.g. <c>[0]</c> for person-only), which is the
    /// CPU-side win on postprocess-bound hardware. <see langword="null"/> /
    /// empty scans every class.
    /// </summary>
    public Yolov8Postprocessor(
        YoloModelDescriptor? descriptor = null,
        IReadOnlyCollection<int>? classFilter = null)
    {
        _descriptor = descriptor ?? YoloModelDescriptor.CocoDefault;
        _classesToScan = BuildScanList(_descriptor.ClassCount, classFilter);
    }

    private static int[] BuildScanList(int classCount, IReadOnlyCollection<int>? filter)
    {
        if (filter is null || filter.Count == 0)
        {
            var all = new int[classCount];
            for (int i = 0; i < classCount; i++)
                all[i] = i;
            return all;
        }

        var seen = new HashSet<int>();
        foreach (var c in filter)
        {
            if (c < 0 || c >= classCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(filter), c,
                    $"Class filter id {c} is outside the model's class range [0,{classCount - 1}].");
            }
            seen.Add(c);
        }
        var list = new int[seen.Count];
        seen.CopyTo(list);
        Array.Sort(list);
        return list;
    }

    /// <summary>
    /// Runs NMS on the supplied model output and returns detections in
    /// source-image pixel coordinates.
    /// </summary>
    public List<Detection> Decode(
        ReadOnlySpan<float> modelOutput,
        float scaleX,
        float scaleY
    )
    {
        int anchorCount = _descriptor.AnchorCount;
        int classCount = _descriptor.ClassCount;
        var classNames = _descriptor.ClassNames;

        if (modelOutput.Length < OutputElementCount)
        {
            throw new ArgumentException(
                $"Model output span has {modelOutput.Length} elements; this model expects at least "
                    + $"{OutputElementCount} ([1, {ChannelCount}, {anchorCount}] for a {classCount}-class head).",
                nameof(modelOutput)
            );
        }

        // Tensor layout is [1, 4+C, A] — channel-major, all A cx values
        // followed by all A cy values, etc. The argmax scans only the
        // allow-listed classes (_classesToScan); unfiltered, that's every
        // class and the result is identical to a full scan.
        var classes = _classesToScan;
        var candidates = new List<Detection>(capacity: 256);
        for (int anchor = 0; anchor < anchorCount; anchor++)
        {
            int bestClass = classes[0];
            float bestScore = modelOutput[(4 + bestClass) * anchorCount + anchor];
            for (int i = 1; i < classes.Length; i++)
            {
                int c = classes[i];
                float score = modelOutput[(4 + c) * anchorCount + anchor];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestScore < ConfidenceThreshold)
                continue;

            float cx = modelOutput[0 * anchorCount + anchor];
            float cy = modelOutput[1 * anchorCount + anchor];
            float w = modelOutput[2 * anchorCount + anchor];
            float h = modelOutput[3 * anchorCount + anchor];

            float x = (cx - w / 2f) * scaleX;
            float y = (cy - h / 2f) * scaleY;
            float scaledW = w * scaleX;
            float scaledH = h * scaleY;

            candidates.Add(
                new Detection(
                    ClassId: bestClass,
                    ClassName: classNames[bestClass],
                    Confidence: bestScore,
                    X: x,
                    Y: y,
                    Width: scaledW,
                    Height: scaledH
                )
            );
        }

        return NonMaxSuppression(candidates);
    }

    /// <summary>
    /// Standard per-class greedy NMS. Sorted by confidence; for each
    /// remaining detection, suppress any later detection in the same
    /// class whose IoU exceeds the threshold.
    /// </summary>
    private List<Detection> NonMaxSuppression(List<Detection> candidates)
    {
        candidates.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        var kept = new List<Detection>(capacity: Math.Min(candidates.Count, MaxDetections));
        var suppressed = new bool[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            if (suppressed[i])
                continue;

            kept.Add(candidates[i]);
            if (kept.Count >= MaxDetections)
                break;

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed[j])
                    continue;
                if (candidates[i].ClassId != candidates[j].ClassId)
                    continue;
                if (IoU(candidates[i], candidates[j]) >= IoUThreshold)
                    suppressed[j] = true;
            }
        }

        return kept;
    }

    private static float IoU(Detection a, Detection b)
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
}
