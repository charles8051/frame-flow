// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Media;

/// <summary>
/// Accepts decoded PCM audio data and plays it through an audio backend.
/// Implementations handle audio output lifecycle (activate / pause /
/// resume / deactivate), may implement <see cref="IVolumeControl"/> when
/// the backend owns a real gain stage, and may additionally implement
/// <see cref="IClockSource"/> when the backend can publish a sample-counter-
/// derived master clock (ADR-0035, refined by the "clock as graph signal"
/// proposal — see <c>docs/decoding-pipeline-proposed.html</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this interface is not symmetric with <see cref="IVideoSink"/>.</b>
/// A sink is one dataflow method plus whatever resources its medium
/// requires. Audio output is a device: it has a transport (activate /
/// pause / resume / deactivate) and may publish a sample-counter clock
/// via <see cref="IClockSource"/>. Video output is a surface: it owns a
/// frame pool and has a format that can change mid-stream, so
/// <see cref="IVideoSink"/> carries <c>FramePool</c> and
/// <c>OnFormatChangedAsync</c> instead. The differing members are the
/// media, not an accident. There is no shared base interface —
/// <see cref="PresentAsync"/> and
/// <see cref="IAsyncDisposable.DisposeAsync"/> are the only members the
/// two have in common, and they are declared independently.
/// ADR-0066 records why, and what would change that answer. Gain is a
/// further split along the same lines: see <see cref="IVolumeControl"/>
/// and ADR-0065.
/// </para>
/// <para>
/// <b>Dataflow contract (ADR-0066).</b> The dataflow facet is
/// <see cref="PresentAsync"/>; the adapter
/// (<c>FrameFlow.Media.SinkAdapters.AsSinkNode</c>) wraps each
/// <see cref="IAudioSink"/> as a substrate <c>SinkNode&lt;PcmAudioBufferRef&gt;</c>
/// whose body invokes <see cref="PresentAsync"/>.
/// </para>
/// <para>
/// <b>Master clock role.</b> Backends that own a continuous sample
/// counter (OpenAL, DirectSound, WASAPI, …) implement
/// <see cref="IClockSource"/> alongside <see cref="IAudioSink"/> and
/// publish ticks from their sample counter — see
/// <c>OpenAlAudioSink</c> for the reference implementation. Backends
/// without a sample-accurate counter (test doubles, capture sinks) need
/// not implement <see cref="IClockSource"/>; the playback session falls
/// back to a wallclock-backed clock source.
/// </para>
/// <para>
/// <b>Gain control.</b> Backends with a mixer or device gain implement
/// <see cref="IVolumeControl"/> alongside this interface — see
/// <c>OpenAlAudioSink</c>. Sinks that record or discard rather than render
/// do not, and consumers discover the difference by type test rather than
/// by a capability flag whose fallback was to swallow the write.
/// </para>
/// <para>
/// <b>Single ownership contract.</b> Sinks own the buffer reference
/// handed to <see cref="PresentAsync"/>; refcounting
/// (<see cref="IAudioBuffer.AddRef"/> / <c>Dispose</c>) handles fan-out
/// without cloning.
/// </para>
/// <para>
/// <b>Disposal contract (ADR-0044).</b> Implementations <b>must</b>
/// support idempotent <see cref="IAsyncDisposable.DisposeAsync"/>:
/// calling it more than once is a no-op (no throw, no side effects,
/// no resource access on the second and subsequent calls). Sinks are
/// owned by their DI container or by their immediate caller; the
/// playback session and pipeline controller are <i>users</i> of sinks,
/// not owners, and never invoke <see cref="IAsyncDisposable.DisposeAsync"/>
/// on a sink.
/// </para>
/// </remarks>
public interface IAudioSink : IAsyncDisposable
{
    /// <summary>
    /// Presents one decoded PCM audio buffer. The sink takes ownership
    /// of <paramref name="buffer"/> and is responsible for disposing it
    /// after presentation. The substrate invokes this exactly once per
    /// buffer from a <c>SinkNode&lt;PcmAudioBufferRef&gt;</c> body.
    /// </summary>
    /// <param name="buffer">The buffer to present. Ownership transfers to the sink.</param>
    /// <param name="ct">Cancellation token observed during async work.</param>
    ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct);

    /// <summary>
    /// Brings the audio device up so presented buffers are actually heard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The playback surface owns this call â€” callers must not pre-activate.</b>
    /// <c>SubstrateSession</c>, <c>PlayerSession</c>, and
    /// <c>MediaPlayer.CreateAsync</c> each activate the sink they were given
    /// before any buffer reaches <see cref="PresentAsync"/>. A sink handed to
    /// one of them should arrive dormant.
    /// </para>
    /// <para>
    /// <b>Re-activation is a reset, not a no-op.</b> Implementations are
    /// expected to return the sink to a fresh playing state â€” clearing queued
    /// buffers and rebasing the sample counter. That is what makes loop
    /// restart work (see <c>OpenAlAudioSink.ActivateAsync</c>, which reasserts
    /// <c>SourceStop</c> and rewinds). For a sink that also implements
    /// <see cref="IClockSource"/>, this rebases the master clock â€” which is
    /// exactly why a redundant activation is not harmless and why the caller
    /// must leave activation to the session.
    /// </para>
    /// <para>
    /// A second call is nonetheless required to be safe rather than throwing,
    /// so a sink reused across sessions does not fault.
    /// </para>
    /// </remarks>
    ValueTask ActivateAsync(CancellationToken cancellationToken = default);

    ValueTask PauseAsync(CancellationToken cancellationToken = default);

    ValueTask ResumeAsync(CancellationToken cancellationToken = default);

    ValueTask DeactivateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a coherent snapshot of the sink's observable state (ADR-0034).
    /// This is the sanctioned cross-thread read path — implementations
    /// internally acquire whatever synchronization is needed so callers can
    /// be naïve about thread safety. Cheap enough to call from a UI timer
    /// at modest frequency; do not call from per-frame hot paths.
    /// </summary>
    /// <remarks>
    /// Default implementation returns <see cref="AudioSinkDiagnosticsSnapshot.Empty"/>.
    /// Sinks that surface real state (e.g. <c>OpenAlAudioSink</c>) override.
    /// Test doubles and toy sinks can rely on the default.
    /// </remarks>
    AudioSinkDiagnosticsSnapshot GetDiagnostics() => AudioSinkDiagnosticsSnapshot.Empty;
}
