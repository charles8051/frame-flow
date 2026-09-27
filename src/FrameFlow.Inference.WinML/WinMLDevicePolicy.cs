// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference.WinML;

/// <summary>
/// How Windows ML chooses the execution provider for a session, among those registered. It tracks
/// the providers Windows installs, so new hardware is picked up without a code change.
/// </summary>
public enum WinMLDevicePolicy
{
    /// <summary>A GPU provider when one is registered, such as TensorRT-RTX or DirectML; the CPU otherwise.</summary>
    PreferGpu,

    /// <summary>An NPU provider when one is registered.</summary>
    PreferNpu,

    /// <summary>The CPU provider.</summary>
    PreferCpu,

    /// <summary>Whichever registered provider Windows expects to run fastest.</summary>
    MaxPerformance,

    /// <summary>Whichever registered provider Windows expects to use least energy per inference.</summary>
    MaxEfficiency,

    /// <summary>Whichever registered provider Windows expects to draw least power overall.</summary>
    MinOverallPower,
}
