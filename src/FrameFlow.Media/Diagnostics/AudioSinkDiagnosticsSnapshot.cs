// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Diagnostics;

/// <summary>
/// Immutable snapshot of an <see cref="IAudioSink"/>'s observable state at a
/// single point in time (ADR-0034).
/// </summary>
/// <param name="PresentationTime">
/// The sink's current master-clock playback time, equivalent to a synchronized
/// read of <see cref="IAudioSink.GetPlaybackTime"/>. Sampled inside the sink's
/// internal lock alongside <paramref name="ProcessedSamplesPerChannel"/> so the
/// two fields are coherent — this is the API shape that prevents the
/// PTS/samples-played torn-read race that motivated this ADR.
/// </param>
/// <param name="ProcessedSamplesPerChannel">
/// Cumulative count of audio samples per channel that have been fully played
/// out of the device. Combined with <paramref name="SampleRate"/> this gives
/// wall-clock-paced presentation time.
/// </param>
/// <param name="SampleRate">Output sample rate in Hz. Zero before activation.</param>
/// <param name="Channels">Output channel count. Zero before activation.</param>
/// <param name="BlocksWritten">
/// Total decoded audio blocks accepted by <see cref="IAudioSink.WriteAsync"/>
/// since the most recent <see cref="IAudioSink.ActivateAsync"/>.
/// </param>
/// <param name="UnderrunCount">
/// Number of times the audio source ran out of queued data and stalled. A
/// non-zero value during playback indicates the decoder/pipeline is not
/// keeping up; transient underruns at start-up are normal.
/// </param>
/// <param name="BackpressureEvents">
/// Number of times <see cref="IAudioSink.WriteAsync"/> had to wait for a free
/// device-side buffer before it could accept the next block. Indicates the
/// sink is being fed faster than it can drain.
/// </param>
/// <param name="IsActive">
/// <see langword="true"/> when the sink is between
/// <see cref="IAudioSink.ActivateAsync"/> and
/// <see cref="IAudioSink.DeactivateAsync"/>.
/// </param>
public sealed record AudioSinkDiagnosticsSnapshot(
    TimeSpan PresentationTime,
    long ProcessedSamplesPerChannel,
    int SampleRate,
    int Channels,
    long BlocksWritten,
    long UnderrunCount,
    long BackpressureEvents,
    bool IsActive
)
{
    /// <summary>
    /// Zero-valued snapshot used by null-object sinks and as the seed value
    /// for rollups when no audio sink is registered.
    /// </summary>
    public static AudioSinkDiagnosticsSnapshot Empty { get; } =
        new(
            PresentationTime: TimeSpan.Zero,
            ProcessedSamplesPerChannel: 0,
            SampleRate: 0,
            Channels: 0,
            BlocksWritten: 0,
            UnderrunCount: 0,
            BackpressureEvents: 0,
            IsActive: false
        );
}
