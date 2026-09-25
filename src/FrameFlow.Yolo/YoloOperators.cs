// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Yolo;

/// <summary>
/// Operator factories that turn a YOLOv8 detection function into a
/// graph pipeline node. The detection function is the entire
/// contract — this type knows nothing about which backend (CUDA / DML /
/// CPU) produced the detections.
/// </summary>
public static class YoloOperators
{
    /// <summary>
    /// Builds a 1→1 detection operator from a detection delegate. Each
    /// upstream <see cref="IVideoFrame"/> becomes a
    /// <see cref="DetectedVideoFrameRef"/> carrying both the frame and
    /// the detection results.
    /// </summary>
    /// <param name="id">Node id for graph diagnostics.</param>
    /// <param name="detect">
    /// Function that produces detection results for a frame. Called
    /// once per upstream item; not thread-safe by contract (matches
    /// the behaviour of backend-specific detectors like
    /// <c>CudaYolov8Detector.Detect</c> / <c>DmlYolov8Detector.Detect</c>,
    /// either of which can be passed directly as a method group).
    /// </param>
    public static OperatorNode<IVideoFrame, DetectedVideoFrameRef> DetectWith(
        string id,
        Func<IVideoFrame, IReadOnlyList<Detection>> detect
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(detect);

        return new OperatorNode<IVideoFrame, DetectedVideoFrameRef>(
            id,
            (input, ct) =>
            {
                var detections = detect(input);
                // AddRef the input's frame so the output item holds
                // its own ref. The substrate releases `input` after
                // this returns.
                var videoCopy = input.AddRef();
                return ValueTask.FromResult<DetectedVideoFrameRef?>(
                    new DetectedVideoFrameRef(videoCopy, detections)
                );
            },
            // The output carries the input frame itself.
            holding: Holding.InFlight
        );
    }
}
