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
/// requires. Video output is a surface: it owns a frame pool and has a
/// format that can change mid-stream, so it carries
/// <see cref="FramePool"/> and <see cref="OnFormatChangedAsync"/>.
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
/// <see cref="IVideoSink"/> as a substrate <c>SinkNode&lt;VideoFrameRef&gt;</c>
/// whose body invokes <see cref="PresentAsync"/>. The lifecycle/resource
/// facet is everything else on this interface (<see cref="FramePool"/>,
/// <see cref="OnFormatChangedAsync"/>,
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
/// A sink owns an <see cref="IFramePool"/> whose frames the decoder fills.
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
/// no resource access on the second and subsequent calls). Sinks are
/// owned by their DI container or by their immediate caller; the
/// playback session and pipeline controller are <i>users</i> of sinks,
/// not owners, and never invoke <see cref="IAsyncDisposable.DisposeAsync"/>
/// on a sink.
/// </para>
/// </remarks>
public interface IVideoSink : IAsyncDisposable
{
    /// <summary>
    /// Presents one decoded video frame. The sink takes ownership of
    /// <paramref name="frame"/> and is responsible for disposing it
    /// after presentation (or immediately, if dropping). The substrate
    /// invokes this method exactly once per frame from a
    /// <c>SinkNode&lt;VideoFrameRef&gt;</c> body.
    /// </summary>
    /// <param name="frame">The frame to present. Ownership transfers to the sink.</param>
    /// <param name="ct">Cancellation token observed during async work (rare).</param>
    ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct);

    /// <summary>
    /// The frame pool that produces frames for this sink.
    /// The decoder rents from this pool and the sink returns frames after presentation.
    /// </summary>
    IFramePool FramePool { get; }

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
}
