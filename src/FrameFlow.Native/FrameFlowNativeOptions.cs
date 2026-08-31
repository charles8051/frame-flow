// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Native;

public sealed class FrameFlowNativeOptions
{
    public bool UseBundledBinaries { get; set; } = true;

    public bool ProbeSystemLibraries { get; set; } = true;

    public string? CustomFfmpegPath { get; set; }

    /// <summary>
    /// When <see langword="true"/>, skips the hardware-decode capability probe
    /// during bootstrap. The resulting <c>FrameFlowBootstrapResult.Capabilities</c>
    /// is <c>HardwareDecodeCapabilities.Empty</c>, and
    /// <c>HardwareDecodeMode.Auto</c> will fall through to software decode.
    /// </summary>
    /// <remarks>
    /// Useful for constrained environments (containers without GPU access, smoke
    /// tests) where the probe's "no device available" diagnostics would be noisy.
    /// Default is <see langword="false"/> — probing is on per ADR-0033.
    /// </remarks>
    public bool SkipHardwareProbe { get; set; }
}
