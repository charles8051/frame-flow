// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Output gain control. Implemented alongside <see cref="IAudioSink"/> by
/// backends that own a real mixer or device gain (OpenAL, WASAPI, CoreAudio,
/// …). Backends with no gain of their own — capture sinks, test doubles,
/// anything that discards or records rather than renders — simply do not
/// implement it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate interface.</b> Volume is not universal to sinks,
/// and the previous shape said so with a runtime flag
/// (<c>AudioSinkCapabilities.SupportsVolumeControl</c>) whose documented
/// fallback was to accept the write and silently drop it. A sink that cannot
/// change gain should not have to pretend it can. Splitting the members out
/// makes the absence a compile-time fact, the same way ADR-0035 split the
/// master clock into <see cref="FrameFlow.Graph.IClockSource"/>.
/// </para>
/// <para>
/// <b>Discovery.</b> Consumers who hold a sink test for this interface
/// directly. Consumers who hold an <c>IMediaPlayer</c> cannot — they never see
/// the sink — so the player surface exposes
/// <c>IMediaPlayer.SupportsVolumeControl</c> instead. That property is the
/// same question asked at the layer that can't answer it with a type test.
/// </para>
/// <para>
/// <b>Persistence.</b> Values set here survive
/// <see cref="IAudioSink.ActivateAsync"/> /
/// <see cref="IAudioSink.DeactivateAsync"/> cycles, so a consumer who sets
/// volume before playback starts still gets that volume on the first frame,
/// and a loop restart does not reset it.
/// </para>
/// </remarks>
public interface IVolumeControl
{
    /// <summary>
    /// Output gain. <c>0.0</c> is silent, <c>1.0</c> is unity (the default).
    /// Values above <c>1.0</c> are accepted but may distort depending on the
    /// backend. The setter applies instantly — samples already queued in the
    /// backend's buffer play out at the new gain on the next device-internal
    /// mix.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative or <see cref="float.NaN"/>. Implementations
    /// reject invalid gain rather than clamping it, so a caller learns about
    /// the mistake.
    /// </exception>
    float Volume { get; set; }

    /// <summary>
    /// Master mute. <c>true</c> silences output without changing
    /// <see cref="Volume"/>; setting it back to <c>false</c> restores the
    /// previous volume. The effective gain at any time is
    /// <c>Muted ? 0 : Volume</c>.
    /// </summary>
    bool Muted { get; set; }
}
