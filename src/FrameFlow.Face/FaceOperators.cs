// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Face;

/// <summary>
/// Operator factories that turn a BlazeFace detection function into a
/// Crossbar pipeline node. The detection function is the entire contract
/// — this type knows nothing about which backend produced the faces.
/// Mirrors <c>FrameFlow.Yolo.YoloOperators</c>.
/// </summary>
public static class FaceOperators
{
    /// <summary>
    /// Builds a 1→1 detection operator from a detection delegate. Each
    /// upstream <see cref="VideoFrameRef"/> becomes a
    /// <see cref="DetectedFaceFrameRef"/> carrying both the frame and the
    /// faces found in it.
    /// </summary>
    /// <param name="id">Node id for graph diagnostics.</param>
    /// <param name="detect">
    /// Function that produces faces for a frame. Called once per upstream
    /// item; not thread-safe by contract (matches
    /// <see cref="BlazeFaceDetector.Detect(IVideoFrame)"/>, which can be
    /// passed directly as a method group).
    /// </param>
    public static OperatorNode<VideoFrameRef, DetectedFaceFrameRef> DetectWith(
        string id,
        Func<IVideoFrame, IReadOnlyList<FaceDetection>> detect)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(detect);

        return new OperatorNode<VideoFrameRef, DetectedFaceFrameRef>(
            id,
            (input, ct) =>
            {
                var faces = detect(input.Frame);
                // AddRef the input's frame so the output wrapper owns its
                // own ref; the substrate disposes `input` after this returns.
                var videoCopy = (VideoFrameRef)input.AddRef();
                return ValueTask.FromResult<DetectedFaceFrameRef?>(
                    new DetectedFaceFrameRef(videoCopy, faces));
            });
    }
}
