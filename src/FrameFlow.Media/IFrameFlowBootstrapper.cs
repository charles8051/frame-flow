// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Represents the bootstrapper responsible for initializing the FFmpeg native environment.
/// </summary>
/// <remarks>
/// Consumers should call <see cref="Initialize"/> once at startup, typically via the hosted
/// service registered by <c>AddFfmpegBootstrapper()</c>. Higher-layer components may depend
/// on this interface without referencing <c>FrameFlow.Native</c>.
/// </remarks>
public interface IFrameFlowBootstrapper
{
    /// <summary>
    /// Gets a value indicating whether the bootstrapper has completed initialization.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initializes the FFmpeg native environment and returns a typed result describing the outcome.
    /// </summary>
    /// <returns>
    /// A <see cref="FrameFlowBootstrapResult"/> that callers can inspect to diagnose success,
    /// failure, or misconfiguration. This method must not throw; all failure information is
    /// communicated through the returned result.
    /// </returns>
    FrameFlowBootstrapResult Initialize();
}
