// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow;

public sealed class FrameFlowOptions
{
    public FrameFlowPlaybackOptions Playback { get; set; } = new();

    public FrameFlowVideoOptions Video { get; set; } = new();

    public FrameFlowAudioOptions Audio { get; set; } = new();

    public FrameFlowBufferingOptions Buffering { get; set; } = new();
}

public sealed class FrameFlowPlaybackOptions
{
    /// <summary>
    /// Initial repeat mode applied when a controller is created. Can be changed
    /// at runtime via <see cref="Playback.IPlaybackController.SetRepeatModeAsync"/>.
    /// </summary>
    public RepeatMode InitialRepeatMode { get; set; } = RepeatMode.Off;
}

/// <summary>
/// Video-related FrameFlow options. Currently carries hardware-decode policy;
/// future video knobs (color management, pixel-format preferences, etc.) will
/// be added here.
/// </summary>
public sealed class FrameFlowVideoOptions
{
    /// <summary>
    /// Hardware-decode selection policy (ADR-0033). Defaults to
    /// <see cref="HardwareDecodeMode.Auto"/> — hwaccel is attempted, with
    /// transparent fallback to software when no backend binds.
    /// </summary>
    public HardwareDecodeOptions HardwareDecode { get; set; } = new();
}

/// <summary>
/// Configures FrameFlow's hardware-decode selection policy (ADR-0033).
/// </summary>
public sealed class HardwareDecodeOptions
{
    /// <summary>
    /// Selection mode. See <see cref="HardwareDecodeMode"/> for the meaning of
    /// each value. Defaults to <see cref="HardwareDecodeMode.Auto"/>.
    /// </summary>
    public HardwareDecodeMode Mode { get; set; } = HardwareDecodeMode.Auto;

    /// <summary>
    /// Backends to try first, in priority order. Backends not in this list are
    /// tried in the platform default order after the preferred ones fail.
    /// An empty list (the default) uses only the platform default order.
    /// </summary>
    public IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends { get; set; } =
        Array.Empty<HardwareDecodeBackendKind>();
}

/// <summary>
/// Selection policy for hardware-decode (ADR-0033).
/// </summary>
public enum HardwareDecodeMode
{
    /// <summary>
    /// Never attach a hardware accelerator; always open the software decoder.
    /// Use for testing or when GPU resources are reserved for other work.
    /// </summary>
    Disabled,

    /// <summary>
    /// Try the configured hwaccel backends in order; fall back to the software
    /// decoder transparently if none binds. This is the default.
    /// </summary>
    Auto,

    /// <summary>
    /// Try the configured hwaccel backends in order; if none binds, the
    /// playback controller's <c>LoadAsync</c> returns
    /// <c>Result.Fail(ErrorCategory.InvalidOperation, ...)</c>. Use when the
    /// pipeline cannot tolerate the software path (e.g., perf-critical decode
    /// at high resolutions).
    /// </summary>
    Required,
}

public sealed class FrameFlowAudioOptions
{
    public bool EnableAudio { get; set; } = true;

    public int PreferredChannels { get; set; } = 2;
}

public sealed class FrameFlowBufferingOptions
{
    public int MaxQueuedAudioPackets { get; set; } = 64;

    public int MaxQueuedVideoPackets { get; set; } = 16;

    public int MaxPendingFrames { get; set; } = 4;
}
