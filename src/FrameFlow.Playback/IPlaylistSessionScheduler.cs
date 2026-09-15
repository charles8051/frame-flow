// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Runs the work a <see cref="PlaylistSession"/> moves off the thread that asked for it: the
/// advance after an item ends, faults or is skipped, and the advance that takes a jump.
/// </summary>
internal interface IPlaylistSessionScheduler
{
    /// <summary>Starts <paramref name="work"/> without waiting for it.</summary>
    void Schedule(Func<Task> work);
}

/// <summary>Runs a <see cref="PlaylistSession"/>'s work on the thread pool.</summary>
internal sealed class ThreadPoolPlaylistSessionScheduler : IPlaylistSessionScheduler
{
    public static ThreadPoolPlaylistSessionScheduler Instance { get; } = new();

    private ThreadPoolPlaylistSessionScheduler() { }

    public void Schedule(Func<Task> work) => _ = Task.Run(work);
}
