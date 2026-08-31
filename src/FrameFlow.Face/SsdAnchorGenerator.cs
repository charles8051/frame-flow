// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Face;

/// <summary>
/// Generates the SSD prior-box table for a BlazeFace-family model. A
/// faithful port of MediaPipe's <c>SsdAnchorsCalculator::GenerateAnchors</c>
/// (Apache-2.0): same scale interpolation, same same-stride merge, same
/// centre placement — so the table matches what the model was trained
/// against exactly.
/// </summary>
/// <remarks>
/// The table is deterministic and depends only on the
/// <see cref="SsdAnchorOptions"/>, so a descriptor generates it once at
/// construction and reuses it for every frame.
/// </remarks>
public static class SsdAnchorGenerator
{
    /// <summary>
    /// Builds the anchor table for <paramref name="options"/>. The count
    /// equals the model's box count (896 for the front-128 face model).
    /// </summary>
    public static IReadOnlyList<SsdAnchor> Generate(SsdAnchorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Strides.Count != options.NumLayers)
        {
            throw new ArgumentException(
                $"Strides has {options.Strides.Count} entries but NumLayers is {options.NumLayers}; "
                    + "MediaPipe requires one stride per layer.",
                nameof(options));
        }

        var anchors = new List<SsdAnchor>(capacity: 1024);
        var strides = options.Strides;
        int numLayers = options.NumLayers;

        int layerId = 0;
        while (layerId < numLayers)
        {
            var anchorHeight = new List<float>();
            var anchorWidth = new List<float>();
            var aspectRatios = new List<float>();
            var scales = new List<float>();

            // Merge consecutive layers that share a stride into one feature
            // map — this is what turns BlazeFace's 4 declared layers into
            // 2-then-6 anchors per cell.
            int lastSameStrideLayer = layerId;
            while (lastSameStrideLayer < strides.Count
                   && strides[lastSameStrideLayer] == strides[layerId])
            {
                float scale = CalculateScale(
                    options.MinScale, options.MaxScale, lastSameStrideLayer, strides.Count);

                if (lastSameStrideLayer == 0 && options.ReduceBoxesInLowestLayer)
                {
                    aspectRatios.Add(1.0f);
                    aspectRatios.Add(2.0f);
                    aspectRatios.Add(0.5f);
                    scales.Add(0.1f);
                    scales.Add(scale);
                    scales.Add(scale);
                }
                else
                {
                    foreach (var ar in options.AspectRatios)
                    {
                        aspectRatios.Add(ar);
                        scales.Add(scale);
                    }
                    if (options.InterpolatedScaleAspectRatio > 0.0f)
                    {
                        float scaleNext = lastSameStrideLayer == strides.Count - 1
                            ? 1.0f
                            : CalculateScale(
                                options.MinScale, options.MaxScale,
                                lastSameStrideLayer + 1, strides.Count);
                        scales.Add(MathF.Sqrt(scale * scaleNext));
                        aspectRatios.Add(options.InterpolatedScaleAspectRatio);
                    }
                }
                lastSameStrideLayer++;
            }

            for (int i = 0; i < aspectRatios.Count; i++)
            {
                float ratioSqrt = MathF.Sqrt(aspectRatios[i]);
                anchorHeight.Add(scales[i] / ratioSqrt);
                anchorWidth.Add(scales[i] * ratioSqrt);
            }

            int stride = strides[layerId];
            int featureMapHeight = CeilDiv(options.InputSizeHeight, stride);
            int featureMapWidth = CeilDiv(options.InputSizeWidth, stride);

            for (int y = 0; y < featureMapHeight; y++)
            {
                for (int x = 0; x < featureMapWidth; x++)
                {
                    for (int anchorId = 0; anchorId < anchorHeight.Count; anchorId++)
                    {
                        float xCenter = (x + options.AnchorOffsetX) / featureMapWidth;
                        float yCenter = (y + options.AnchorOffsetY) / featureMapHeight;
                        float w = options.FixedAnchorSize ? 1.0f : anchorWidth[anchorId];
                        float h = options.FixedAnchorSize ? 1.0f : anchorHeight[anchorId];
                        anchors.Add(new SsdAnchor(xCenter, yCenter, w, h));
                    }
                }
            }

            layerId = lastSameStrideLayer;
        }

        return anchors;
    }

    /// <summary>MediaPipe's linear scale interpolation across the stride layers.</summary>
    internal static float CalculateScale(float minScale, float maxScale, int strideIndex, int numStrides)
        => numStrides == 1
            ? (minScale + maxScale) * 0.5f
            : minScale + (maxScale - minScale) * strideIndex / (numStrides - 1.0f);

    private static int CeilDiv(int a, int b) => (int)MathF.Ceiling((float)a / b);
}
