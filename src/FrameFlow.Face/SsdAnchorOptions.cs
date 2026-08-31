// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Face;

/// <summary>
/// The subset of MediaPipe's <c>SsdAnchorsCalculatorOptions</c> that the
/// BlazeFace anchor table depends on. Faithful field-for-field port so
/// the generated table is bit-for-bit what the model was trained against
/// — a mismatch here silently corrupts every decoded box, so the values
/// are pinned to the published model config, not guessed.
/// </summary>
/// <remarks>
/// Ported from <c>ssd_anchors_calculator.proto</c> /
/// <c>ssd_anchors_calculator.cc</c> (Apache-2.0). Only the fields the
/// face models actually set are represented; unused knobs
/// (feature_map_height/width overrides, per-layer strides beyond the
/// list) are omitted rather than carried as dead options.
/// </remarks>
public sealed record SsdAnchorOptions
{
    /// <summary>Model input width in pixels.</summary>
    public required int InputSizeWidth { get; init; }

    /// <summary>Model input height in pixels.</summary>
    public required int InputSizeHeight { get; init; }

    /// <summary>Smallest anchor scale, at the first (finest-stride) layer.</summary>
    public required float MinScale { get; init; }

    /// <summary>Largest anchor scale, at the last layer.</summary>
    public required float MaxScale { get; init; }

    /// <summary>Sub-cell x offset of each anchor centre (0.5 = cell centre).</summary>
    public float AnchorOffsetX { get; init; } = 0.5f;

    /// <summary>Sub-cell y offset of each anchor centre (0.5 = cell centre).</summary>
    public float AnchorOffsetY { get; init; } = 0.5f;

    /// <summary>Number of detection-head layers.</summary>
    public required int NumLayers { get; init; }

    /// <summary>
    /// Output stride per layer. Consecutive equal strides are merged into
    /// one feature map whose per-cell anchor count is the sum of the
    /// merged layers' — this merge is what yields BlazeFace's 896 boxes
    /// from a 4-layer config.
    /// </summary>
    public required IReadOnlyList<int> Strides { get; init; }

    /// <summary>Aspect ratios generated per cell (BlazeFace uses just <c>[1.0]</c>).</summary>
    public IReadOnlyList<float> AspectRatios { get; init; } = [1.0f];

    /// <summary>
    /// When &gt; 0, appends one extra anchor per cell at the interpolated
    /// scale <c>sqrt(scale·next_scale)</c> with this aspect ratio.
    /// BlazeFace sets 1.0, which is why each cell gets 2 (finest) / 6
    /// (merged) anchors rather than 1 / 3.
    /// </summary>
    public float InterpolatedScaleAspectRatio { get; init; } = 1.0f;

    /// <summary>When true, all anchors are forced to unit width/height (BlazeFace).</summary>
    public bool FixedAnchorSize { get; init; } = true;

    /// <summary>When true, the lowest layer uses the SSD-predefined 3-anchor set (BlazeFace: false).</summary>
    public bool ReduceBoxesInLowestLayer { get; init; }
}
