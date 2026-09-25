// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Media;

/// <summary>
/// Receives decoded video frames for presentation or processing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this interface is not symmetric with <see cref="IAudioSink"/>.</b>
/// A sink is one dataflow method plus whatever resources its medium
/// requires. Video output is a surface with a format that can change
/// mid-stream, so it carries <see cref="OnFormatChangedAsync"/>.
/// Audio output is a device: it has a transport (activate / pause /
/// resume / deactivate) and may publish a sample-counter clock, so
/// <see cref="IAudioSink"/> carries those instead. The differing
/// members are the media, not an accident. There is no shared base
/// interface — <see cref="PresentAsync"/> and
/// <see cref="IAsyncDisposable.DisposeAsync"/> are the only members
/// the two have in common, and they are declared independently.
/// ADR-0066 records why, and what would change that answer.
/// </para>
/// <para>
/// <b>Dataflow contract (ADR-0066).</b> The dataflow facet is
/// <see cref="PresentAsync"/>; the adapter
/// (<c>FrameFlow.Media.SinkAdapters.AsSinkNode</c>) wraps each
/// <see cref="IVideoSink"/> as a substrate <c>SinkNode&lt;IVideoFrame&gt;</c>
/// whose body invokes <see cref="PresentAsync"/>. The lifecycle/resource
/// facet is everything else on this interface
/// (<see cref="OnFormatChangedAsync"/>,
/// <see cref="IAsyncDisposable.DisposeAsync"/>, diagnostics).
/// </para>
/// <para>
/// <b>Memory-domain handling.</b> The substrate does not negotiate
/// memory-domain compatibility. If a sink only accepts CPU-resident
/// frames and the upstream pipeline can produce GPU frames, the
/// consumer is responsible for inserting an explicit conversion operator
/// before the sink. Sinks that receive a frame in a domain they can't
/// handle should fail loudly from <see cref="PresentAsync"/>.
/// </para>
/// <para>
/// The playback pipeline calls <see cref="PresentAsync"/> to deliver frames
/// and <see cref="OnFormatChangedAsync"/> when the stream format changes
/// (e.g. resolution or pixel format switch).
/// </para>
/// <para>
/// Sinks are <see cref="IAsyncDisposable"/> because teardown may involve
/// GPU resource cleanup or async flush operations.
/// </para>
/// <para>
/// <b>Disposal contract (ADR-0044).</b> Implementations <b>must</b>
/// support idempotent <see cref="IAsyncDisposable.DisposeAsync"/>:
/// calling it more than once is a no-op (no throw, no side effects,
/// no resource access on the second and subsequent calls).
/// </para>
/// <para>
/// <b>Ownership (ADR-0044, as amended).</b> Whoever constructs a sink owns
/// it: the DI container for what it registered, the caller for what they
/// built. A player, a pass, a session and the pipeline controller are
/// <i>users</i> — they call <c>ActivateAsync</c> and <c>DeactivateAsync</c>,
/// and never <see cref="IAsyncDisposable.DisposeAsync"/>. One sink serves one
/// player at a time, and may serve several in sequence, which is what keeps a
/// presenter warm across a playlist's items. Nothing enforces the "at a time":
/// driving one sink from two players at once is undefined, and keeping them
/// apart is the caller's job.
/// </para>
/// </remarks>
public interface IVideoSink : IAsyncDisposable
{
    /// <summary>
    /// Presents one decoded video frame. The sink takes ownership of
    /// <paramref name="frame"/> and is responsible for disposing it
    /// after presentation (or immediately, if dropping). The substrate
    /// invokes this method exactly once per frame from a
    /// <c>SinkNode&lt;IVideoFrame&gt;</c> body.
    /// </summary>
    /// <param name="frame">The frame to present. Ownership transfers to the sink.</param>
    /// <param name="ct">Cancellation token observed during async work (rare).</param>
    ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct);

    /// <summary>
    /// Called when the video stream format changes (resolution, pixel format).
    /// The sink should reconfigure its rendering surfaces accordingly.
    /// </summary>
    /// <param name="format">The new video format.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct);

    /// <summary>
    /// Returns a coherent snapshot of the sink's observable state (ADR-0034).
    /// Implementations handle any synchronization needed for a coherent
    /// multi-field read. Cheap enough to call from a UI timer at modest
    /// frequency.
    /// </summary>
    /// <remarks>
    /// Default implementation returns <see cref="VideoSinkDiagnosticsSnapshot.Empty"/>.
    /// Sinks that surface real state (e.g. <c>AvaloniaVideoSink</c>,
    /// <c>SdlVideoSink</c>) override. Test doubles and toy sinks can rely on
    /// the default.
    /// </remarks>
    VideoSinkDiagnosticsSnapshot GetDiagnostics() => VideoSinkDiagnosticsSnapshot.Empty;

    /// <summary>
    /// The most frames the sink keeps after <see cref="PresentAsync"/> returns, counting frames
    /// on their way to the display, or <see langword="null"/> when nothing bounds it (ADR-0081).
    /// </summary>
    /// <remarks>
    /// A frame from a fixed pool, a hardware decoder's surface or a camera's buffer, stays out
    /// of the pool while the sink keeps it, and the pool is sized from this count. The default
    /// is <see langword="null"/>, so a sink that does not say is treated as holding without
    /// bound.
    /// </remarks>
    int? MaxHeldFrames => null;
}
