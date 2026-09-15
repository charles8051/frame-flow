// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Delivers what a <see cref="PlaylistSession"/> is told from outside a command: an item runtime's
/// end-of-stream or fault, and the coordinator's skip and jump requests. The session reads what it
/// needs at the moment it is told, such as the run number and the generation, and hands the
/// delivery of that input to this.
/// </summary>
/// <remarks>
/// The default delivers at once, on the thread that told the session. A test can deliver later, to
/// reproduce a notification that reaches the session after a later call.
/// </remarks>
internal interface IPlaylistSessionScheduler
{
    /// <summary>Runs <paramref name="work"/>, now or later, without waiting for it.</summary>
    void Schedule(Func<Task> work);
}

/// <summary>Delivers a <see cref="PlaylistSession"/>'s inputs at once, on the calling thread.</summary>
internal sealed class InlinePlaylistSessionScheduler : IPlaylistSessionScheduler
{
    public static InlinePlaylistSessionScheduler Instance { get; } = new();

    private InlinePlaylistSessionScheduler() { }

    public void Schedule(Func<Task> work) => _ = work();
}
