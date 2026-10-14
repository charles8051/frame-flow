// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Core;

/// <summary>
/// The tasks of a playback session's run, its demux pump, its graph and its lateness-recovery
/// walk, which the session holds until they have ended (#707). Pure: what is held, and which of it
/// has ended, in; what is still held, and whether a run may launch, out.
/// </summary>
/// <remarks>
/// <para>
/// A run's tasks read the demux session and act on the decoders and the pipeline: the pump and the
/// graph decode, and the recovery walk sets the video decoder's readback and discard level. A stop
/// that repositions or frees any of those waits for the tasks first. A wait its token cuts short
/// forgets only the tasks that have ended, so the next stop, a seek that supersedes it or the
/// session's dispose, finds the rest and waits for them in turn. A task forgotten while it still
/// ran would let that stop seek the demuxer, reset the decoders or free them beside it.
/// </para>
/// <para>
/// A run launches only once nothing is held, so one of each runs at a time. The recovery walk ends
/// only when the run is cancelled, so a run that has it is not over until it is stopped.
/// </para>
/// <para>
/// Generic over the task so the decisions are tested without one. The shell holds
/// <see cref="Task"/> and says which have ended.
/// </para>
/// </remarks>
/// <typeparam name="T">The task type.</typeparam>
/// <param name="Pump">The demux pump's task, or null when none is held.</param>
/// <param name="Graph">The graph's task, or null when none is held.</param>
/// <param name="Recovery">The lateness-recovery walk's task, or null when none is held.</param>
internal readonly record struct RunTasks<T>(T? Pump, T? Graph, T? Recovery)
    where T : class
{
    /// <summary>Nothing held: no run has launched, or the last one's tasks have ended.</summary>
    public static RunTasks<T> None => default;

    /// <summary>
    /// Whether nothing is held, so nothing still reads the demuxer or acts on the decoders. A run
    /// may launch only then.
    /// </summary>
    public bool IsEmpty => Pump is null && Graph is null && Recovery is null;

    /// <summary>The tasks held, for the shell to wait on.</summary>
    public IReadOnlyList<T> Held
    {
        get
        {
            var held = new List<T>(3);
            if (Pump is { } pump)
                held.Add(pump);
            if (Graph is { } graph)
                held.Add(graph);
            if (Recovery is { } recovery)
                held.Add(recovery);
            return held;
        }
    }

    /// <summary>A run's tasks, held from its launch. A run without lateness recovery has no walk.</summary>
    public static RunTasks<T> Launched(T pump, T graph, T? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentNullException.ThrowIfNull(graph);
        return new(pump, graph, recovery);
    }

    /// <summary>
    /// Forgets each task <paramref name="hasEnded"/> says has ended, and holds the rest.
    /// </summary>
    public RunTasks<T> ForgetEnded(Func<T, bool> hasEnded)
    {
        ArgumentNullException.ThrowIfNull(hasEnded);
        return new(Keep(Pump), Keep(Graph), Keep(Recovery));

        T? Keep(T? task) => task is not null && !hasEnded(task) ? task : null;
    }
}
