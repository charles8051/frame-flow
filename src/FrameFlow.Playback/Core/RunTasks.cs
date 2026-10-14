// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Core;

/// <summary>
/// The tasks of a playback session's run, its demux pump and its graph, which the session holds
/// until they have ended (#707). Pure: what is held, and which of it has ended, in; what is still
/// held, and whether a run may launch, out.
/// </summary>
/// <remarks>
/// <para>
/// A run's tasks read the demux session and decode on the decoders and the pipeline. A stop that
/// repositions or frees any of those waits for the tasks first. A wait its token cuts short
/// forgets only the tasks that have ended, so the next stop, a seek that supersedes it or the
/// session's dispose, finds the rest and waits for them in turn. A task forgotten while it still
/// ran would let that stop seek the demuxer, reset the decoders or free them beside it.
/// </para>
/// <para>
/// A run launches only once nothing is held, so one pump and one graph run at a time.
/// </para>
/// <para>
/// Generic over the task so the decisions are tested without one. The shell holds
/// <see cref="Task"/> and says which have ended.
/// </para>
/// </remarks>
/// <typeparam name="T">The task type.</typeparam>
/// <param name="Pump">The demux pump's task, or null when none is held.</param>
/// <param name="Graph">The graph's task, or null when none is held.</param>
internal readonly record struct RunTasks<T>(T? Pump, T? Graph)
    where T : class
{
    /// <summary>Nothing held: no run has launched, or the last one's tasks have ended.</summary>
    public static RunTasks<T> None => default;

    /// <summary>
    /// Whether nothing is held, so nothing still reads the demuxer or decodes. A run may launch
    /// only then.
    /// </summary>
    public bool IsEmpty => Pump is null && Graph is null;

    /// <summary>The tasks held, for the shell to wait on.</summary>
    public IReadOnlyList<T> Held =>
        (Pump, Graph) switch
        {
            ({ } pump, { } graph) => [pump, graph],
            ({ } pump, null) => [pump],
            (null, { } graph) => [graph],
            _ => [],
        };

    /// <summary>A run's tasks, held from its launch.</summary>
    public static RunTasks<T> Launched(T pump, T graph)
    {
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentNullException.ThrowIfNull(graph);
        return new(pump, graph);
    }

    /// <summary>
    /// Forgets each task <paramref name="hasEnded"/> says has ended, and holds the rest.
    /// </summary>
    public RunTasks<T> ForgetEnded(Func<T, bool> hasEnded)
    {
        ArgumentNullException.ThrowIfNull(hasEnded);
        return new(
            Pump is { } pump && !hasEnded(pump) ? pump : null,
            Graph is { } graph && !hasEnded(graph) ? graph : null
        );
    }
}
