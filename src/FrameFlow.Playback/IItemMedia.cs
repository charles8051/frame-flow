// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback.Core;

namespace FrameFlow.Playback;

/// <summary>
/// What an item runtime opens and runs its work over: the demux session, the decoders, the decoding
/// pipeline, the select-by-clock pacer, and the graph a run runs on them (ADR-0086, decision 6).
/// <see cref="SubstrateSession"/> decides when each member is called; <see cref="SubstrateMedia"/>
/// makes the native calls, and the tests drive the session over a fake that records them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Passive.</b> It starts no work of its own. The session runs <see cref="RunPumpAsync"/>,
/// <see cref="RunGraphAsync"/> and <see cref="RunRecoveryWalkAsync"/> as a run's work, and calls
/// <see cref="SeekAsync"/>, <see cref="ResetForSeek"/>, <see cref="BeginRun"/> and
/// <see cref="FreeAsync"/> only once no work of any run is pending. Nothing here checks that: the
/// session's core decides it (ADR-0086, decision 2), and the fake checks it in the tests.
/// </para>
/// <para>
/// <b>Threading.</b> A run's work calls its members on its own threads; the session calls the rest
/// one at a time. The diagnostics may be read from any thread.
/// </para>
/// </remarks>
internal interface IItemMedia
{
    /// <summary>
    /// What it holds: the demux session and the pipeline, the decoder of each stream a sink plays,
    /// and the pacer once <see cref="OpenPacer"/> has built it. A free takes its resource off.
    /// </summary>
    ItemResources Resources { get; }

    /// <summary>The source's metadata, or null once the demux session is freed.</summary>
    MediaInfo? MediaInfo { get; }

    /// <summary>The source's duration, or zero when it is not known.</summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Completes when the video decoder decodes its first frame, once in its life; null without a
    /// video decoder.
    /// </summary>
    Task? FirstFrameDecoded { get; }

    /// <summary>The backend the video decoder bound, or null when it decodes in software or has none.</summary>
    HardwareDecodeBackendKind? BoundBackend { get; }

    /// <summary>
    /// Builds the pacer in front of the video sink, if there is one, paced against
    /// <paramref name="master"/>. It is born with the gates shut. Throws, building nothing, when it
    /// cannot be built.
    /// </summary>
    void OpenPacer(IClockSource master);

    /// <summary>Opens the pause gates and arms the pacer's wait cap.</summary>
    void OpenGates();

    /// <summary>Closes the pause gates and suspends the pacer's wait cap.</summary>
    void CloseGates();

    /// <summary>Drops the frames the pacer holds from the run before.</summary>
    void FlushPacer();

    /// <summary>
    /// Readies the next run: builds a fresh graph, or keeps the last one, sets where the run starts
    /// and stops, arms the pacer for it, and has the decoders stamp a new timeline on what they
    /// decode from here (ADR-0084). Throws, readying nothing it can be blamed for, when the graph
    /// cannot be built.
    /// </summary>
    /// <param name="graph">Whether to build a fresh graph or run the last one again.</param>
    /// <param name="floor">Where the run starts: nothing presented before it is delivered.</param>
    /// <param name="end">Where the run stops, or null when it plays to the end of the source.</param>
    /// <param name="holdForSettle">Whether the pacer holds the destination frame for a settle.</param>
    /// <returns>The timeline the run's frames and buffers carry.</returns>
    TimelineId BeginRun(ItemGraphPolicy graph, TimeSpan floor, TimeSpan? end, bool holdForSettle);

    /// <summary>
    /// The demux pump: reads the source into the decoders' queues, and at the end of the source
    /// flushes the decoders, on <paramref name="cancellationToken"/>. Throws
    /// <see cref="OperationCanceledException"/> once the run is cancelled.
    /// </summary>
    Task RunPumpAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The graph: decodes and delivers to the sinks, then waits for the pacer and the audio sink to
    /// play out what they were given (#609). Returns only if all of that ran uncancelled.
    /// </summary>
    Task RunGraphAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The lateness-recovery walk, which sets the video decoder's readback and discard level. It
    /// ends only on <paramref name="cancellationToken"/>, or by faulting.
    /// </summary>
    Task RunRecoveryWalkAsync(CancellationToken cancellationToken);

    /// <summary>Seeks the demux session to <paramref name="target"/>.</summary>
    ValueTask SeekAsync(TimeSpan target, CancellationToken cancellationToken);

    /// <summary>Resets the decoders and the pipeline for the seek (ADR-0056).</summary>
    void ResetForSeek();

    /// <summary>The pacer's current run, for pairing a settle with the hold it releases.</summary>
    long PacerRun { get; }

    /// <summary>
    /// Waits for the pacer's run <paramref name="run"/> to admit its destination frame, and returns
    /// that frame's time, or null when it has none or <paramref name="cap"/> passes first.
    /// </summary>
    Task<TimeSpan?> WaitForSeekTargetAsync(long run, TimeSpan cap, CancellationToken cancellationToken);

    /// <summary>Releases the hold the pacer's run <paramref name="run"/> took on its destination frame.</summary>
    void ReleaseSettle(long run);

    /// <summary>Frees <paramref name="resource"/>, one flag, and takes it off <see cref="Resources"/>.</summary>
    ValueTask FreeAsync(ItemResources resource);

    /// <summary>The demux session's and the decoders' diagnostics.</summary>
    DecodedMediaStreamDiagnosticsSnapshot StreamDiagnostics { get; }

    /// <summary>The frames the pacer dropped as late, or zero without one.</summary>
    int VideoFramesDroppedLate { get; }

    /// <summary>How late the pacer presents, or null without one or before anything is presented.</summary>
    TimeSpan? VideoPresentationLag { get; }
}

/// <summary>Opens the <see cref="IItemMedia"/> of an item.</summary>
internal interface IItemMediaOpener
{
    /// <summary>
    /// Opens <paramref name="source"/>: the demux session, a decoder for each stream a sink plays,
    /// and the pipeline. One that fails frees what it allocated, in reverse order, and throws.
    /// </summary>
    ValueTask<IItemMedia> OpenAsync(IMediaSource source, CancellationToken cancellationToken);
}
