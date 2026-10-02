// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// Whether <see cref="CompositionInteropVideoView"/> asks the driver to upscale the video, and if
/// not, the first condition that keeps it from asking (#560).
/// </summary>
public enum SuperResolutionStatus
{
    /// <summary><see cref="CompositionInteropVideoView.DriverSuperResolution"/> is off.</summary>
    Off,

    /// <summary>
    /// The view scales through the video processor with the driver's super resolution on. Whether
    /// the driver applies it depends on the GPU and on its own video settings, which the API does
    /// not report.
    /// </summary>
    Requested,

    /// <summary>The view is not larger than the frame on both axes.</summary>
    NotUpscaling,

    /// <summary>The decoder's adapter is not an NVIDIA one.</summary>
    UnsupportedAdapter,

    /// <summary>The frames are not D3D11VA NV12: D3D12VA, 10-bit, or decoded in software.</summary>
    UnsupportedFrames,

    /// <summary>The process runs in a remote session, where the driver does not upscale.</summary>
    RemoteSession,

    /// <summary>Another view in this process holds the video processor.</summary>
    InUseByAnotherView,

    /// <summary>The video processor failed to set up on this adapter.</summary>
    Unavailable,
}
