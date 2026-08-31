// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Callback channel from an <see cref="IPlaybackSession"/> back to its owning
/// controller. Injected at session construction time rather than wired through
/// mutable delegate properties, so all four callbacks are guaranteed to be set
/// before any worker can fire.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0028 §4, this record replaces the previous mutable
/// <c>Action?</c> properties on <see cref="IPlaybackSession"/>. Adding a new
/// callback to the session → controller channel is a breaking change that
/// forces every construction site to supply the new value, eliminating the
/// silent-omission risk of per-field setters.
/// </para>
/// <para>
/// Callbacks may be invoked from worker threads and must be non-blocking.
/// Implementations should route the notification through the controller's
/// command channel via <see cref="PlaybackControllerCore"/>'s internal dispatch
/// rather than performing synchronous state-machine work.
/// </para>
/// </remarks>
/// <param name="OnEndOfStream">Invoked when the pipeline reaches end-of-stream.</param>
/// <param name="OnWorkerFaulted">Invoked when a pipeline worker faults with an unrecoverable error.</param>
/// <param name="OnBufferReady">Invoked when the buffer reaches the ready threshold.</param>
/// <param name="OnBufferUnderrun">Invoked when the buffer underruns during playback.</param>
internal readonly record struct SessionCallbacks(
    Action OnEndOfStream,
    Action<Exception> OnWorkerFaulted,
    Action OnBufferReady,
    Action OnBufferUnderrun
);
