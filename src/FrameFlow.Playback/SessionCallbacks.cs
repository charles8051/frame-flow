// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Callback channel from an <see cref="IPlaybackSession"/> back to its owning
/// controller. Injected at session construction time rather than wired through
/// mutable delegate properties, so every callback is guaranteed to be set
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
/// <param name="OnRecoverableError">
/// Invoked when the session hits an error and carries on, such as a playlist item that
/// fails and is skipped. The controller reports it on <c>ErrorOccurred</c> and does not
/// change state.
/// </param>
/// <param name="OnCurrentItemChanged">
/// Invoked when a session that presents several items, a playlist, makes a new item
/// current. Carries that item's <see cref="MediaInfo"/>, which the controller applies to its
/// <c>Duration</c> and <c>MediaInfo</c>. A session with one item never invokes it: the
/// controller reads that item's metadata when it loads.
/// </param>
/// <param name="OnLoopRestarted">
/// Invoked when a session that loops internally, a playlist, has put its current item back at its
/// start after it played to its end. Carries the count of consecutive loops of that item, which the
/// controller publishes on <c>LoopRestarted</c>. A session that does not loop internally never
/// invokes it: the controller runs that session's loop.
/// </param>
internal readonly record struct SessionCallbacks(
    Action OnEndOfStream,
    Action<Exception> OnWorkerFaulted,
    Action OnBufferReady,
    Action OnBufferUnderrun,
    Action<PlaybackError> OnRecoverableError,
    Action<MediaInfo?> OnCurrentItemChanged,
    Action<int> OnLoopRestarted
);
