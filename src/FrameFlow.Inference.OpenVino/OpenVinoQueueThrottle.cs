// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.OpenVino;

/// <summary>
/// OpenVINO's GPU queue throttle hint (<c>GPU_QUEUE_THROTTLE</c>), which the GPU plugin passes to the
/// OpenCL queue. At a lower level the driver may wait on the GPU without holding a CPU thread.
/// </summary>
public enum OpenVinoQueueThrottle
{
    /// <summary><c>LOW</c>.</summary>
    Low,

    /// <summary><c>MEDIUM</c>.</summary>
    Medium,

    /// <summary><c>HIGH</c>.</summary>
    High,
}
