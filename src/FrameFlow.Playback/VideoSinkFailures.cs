// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Playback.Core;

namespace FrameFlow.Playback;

/// <summary>
/// The run of failed calls to one video sink, kept for the sink rather than for an item (#703).
/// </summary>
/// <remarks>
/// <para>
/// One instance is shared by every pacer over the same sink, as the
/// <see cref="VideoFormatAnnouncer"/> is, and for the same reason: the question it answers, how
/// many calls to this sink have failed in a row, is about the sink, which is warm across the
/// queue. A run kept per item would start again at every item, and a queue of items shorter than
/// the limit, stills for instance, would never reach it however broken the sink was.
/// </para>
/// <para>
/// Two pacers over one sink do not call it at the same time: the announcer serializes their
/// calls, and an item prepared ahead keeps its gates shut until the one before it is disposed. The
/// lock keeps each update whole whatever the callers.
/// </para>
/// </remarks>
internal sealed class VideoSinkFailures
{
    private readonly Lock _gate = new();
    private SinkFailureRun _run;

    /// <summary>The run as it stands, for tests.</summary>
    internal SinkFailureRun Run
    {
        get
        {
            lock (_gate)
                return _run;
        }
    }

    /// <summary>A call to the sink threw at <paramref name="now"/>.</summary>
    /// <param name="now">Elapsed time on a clock every pacer over the sink shares.</param>
    internal SinkFailureVerdict Failed(TimeSpan now)
    {
        lock (_gate)
        {
            (_run, var verdict) = _run.Failed(now);
            return verdict;
        }
    }

    /// <summary>The sink took a frame without throwing.</summary>
    internal void Presented()
    {
        lock (_gate)
            _run = _run.Presented();
    }
}
