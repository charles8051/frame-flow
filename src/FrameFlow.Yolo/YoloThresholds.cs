// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Yolo;

/// <summary>
/// Which of a YOLO model's candidate boxes a detector keeps: a confidence floor, the overlap at
/// which non-maximum suppression drops the weaker of two boxes, and how many boxes survive.
/// </summary>
public sealed record YoloThresholds
{
    private readonly float _confidence = 0.25f;
    private readonly float _iou = 0.45f;
    private readonly int _maxDetections = 100;

    /// <summary>The defaults: confidence 0.25, IoU 0.45, 100 boxes.</summary>
    public static YoloThresholds Default { get; } = new();

    /// <summary>
    /// The lowest class confidence kept, from 0 to 1. Defaults to 0.25. A tracker that matches
    /// low-scoring boxes in a second pass, such as ByteTrack, needs a lower floor.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float Confidence
    {
        get => _confidence;
        init
        {
            if (!(value >= 0f && value <= 1f))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The confidence floor must be from 0 to 1.");
            _confidence = value;
        }
    }

    /// <summary>
    /// The intersection over union at or above which a box of the same class as a stronger box is
    /// dropped, above 0 and up to 1. Defaults to 0.45.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not above 0 and up to 1.</exception>
    public float IoU
    {
        get => _iou;
        init
        {
            if (!(value > 0f && value <= 1f))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The IoU threshold must be above 0 and up to 1.");
            _iou = value;
        }
    }

    /// <summary>The most boxes kept per frame, strongest first. Defaults to 100.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
    public int MaxDetections
    {
        get => _maxDetections;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxDetections = value;
        }
    }
}
