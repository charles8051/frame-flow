// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FrameFlow.Media;
using FrameFlow.Decoding;
using FrameFlow.Playback;
using FrameFlow.Player.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FrameFlow.Graph;

namespace FrameFlow.Player;

/// <summary>
/// One traversal of a source. Owns the open demux session, the decoders and the demux pump
/// pipeline, and builds and runs a graph when <see cref="RunToCompletionAsync"/> is called.
/// Built by <see cref="FrameFlowPass"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No clock.</b> A pass waits on no presentation time. The graph it builds is the decoder
/// source, the configured operators and the sink, with none of the pacing the controller path
/// puts in between, so frames reach the sink as fast as the sink accepts them. An audio sink
/// paces itself by what its device consumes; a video sink does not, and a pass over video runs at
/// decode speed. That is the point of the type, and
/// <c>docs/adr/ADR-0079-the-pass-and-the-player.md</c> is the record.
/// </para>
/// <para>
/// <b>Single-shot.</b> <see cref="RunToCompletionAsync"/> can be called exactly once: the demux
/// session and the decoders are stateful and reach EOS after one full run. Build another pass to
/// run the content again. Pause, resume, seek and repeat are the player's, not a pass's — see
/// <see cref="FrameFlowPlayer"/>.
/// </para>
/// <para>
/// <b>Sink ownership.</b> Sinks are passed in from the caller; the pass does not dispose them.
/// This matches the convention of <see cref="IPassBuilder.WithVideoSink"/> and ADR-0044: a sink is
/// owned by whoever constructed it, and the player layer is a user rather than an owner. A sink
/// that is expensive to build, such as one holding a loaded inference model, is therefore built
/// once and handed to any number of passes. As a user, the pass activates the audio sink for its
/// run and deactivates it when the run ends, however it ends.
/// </para>
/// <para>
/// <b>Sink claims (ADR-0085, decision 6).</b> The pass builder claims each sink that implements
/// <see cref="IClaimableSink"/>, and the pass holds the claim until it is disposed, not until its
/// run ends: a pass built against a sink this one holds fails until this one has been disposed.
/// </para>
/// <para>
/// <b>Dispose.</b> <see cref="DisposeAsync"/> cancels a run still in flight and waits for it to
/// end. Only then does it free the pump pipeline, the decoders and the demux session the run reads
/// from, and it releases the claims last, so nothing the run sends reaches a sink after the release
/// (#696). A run cannot start once the dispose has begun. The first dispose tears down, and every
/// other waits for it and ends as it did (ADR-0086, decision 3).
/// </para>
/// </remarks>
public sealed class MediaPass : IAsyncDisposable
{
    private readonly IDemuxSession _demux;
    private readonly DecodingPipeline _pipeline;
    private readonly VideoDecoder? _videoDecoder;
    private readonly AudioDecoder? _audioDecoder;
    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? _videoConfigurator;
    private readonly Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? _audioConfigurator;
    private readonly ILogger _logger;
    private readonly PassRange _range;

    // The claims on the sinks, released at the end of the dispose, after the run. Which run starts
    // and whether the dispose finds one in flight are the lifecycle's, read and advanced under the
    // lock.
    private readonly IDisposable? _sinkClaims;
    private readonly object _lifecycleLock = new();
    private PassLifecycle _lifecycle = PassLifecycle.Built;

    // Holds the pass's one run until it ends (ADR-0086, decision 1). The dispose stops it, which
    // cancels the run and waits for it to end before anything the run uses is freed.
    private readonly WorkScope _work = new();

    // The scope that holds the run's demux pump and graph while they run, so a dispose that has
    // waited too long can say which of them has not ended. Null outside that stretch of the run.
    private WorkScope? _pumpAndGraph;

    // Completed once the run has ended, with what it threw or null. The run's caller waits on it,
    // and the dispose reads it once the scope has let the run go.
    private readonly TaskCompletionSource<Exception?> _runEnded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Runs the teardown once and has every other dispose wait for it (ADR-0086, decision 3).
    private readonly DisposeOnce _dispose = new();

    /// <summary>
    /// The clock a pass is given and never reads.
    /// </summary>
    /// <remarks>
    /// A pass waits on no presentation time, which is a claim that needs a way to fail. Nothing
    /// public supplies this, because a consumer has nothing to supply. A test hands a pass a clock
    /// that throws on every read and asserts the run completes anyway; an implementation that grew
    /// a pacer would take the clock from here and fail that test with the throw. Carrying it is
    /// what makes the decision falsifiable rather than asserted.
    /// </remarks>
    internal IPlaybackClock? Clock { get; }

    internal MediaPass(
        IDemuxSession demux,
        DecodingPipeline pipeline,
        VideoDecoder? videoDecoder,
        AudioDecoder? audioDecoder,
        IVideoSink? videoSink,
        IAudioSink? audioSink,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? videoConfigurator,
        Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? audioConfigurator,
        ILogger? logger = null,
        IPlaybackClock? clock = null,
        PassRange range = default,
        IDisposable? sinkClaims = null
    )
    {
        Clock = clock;
        _range = range;
        _sinkClaims = sinkClaims;
        _demux = demux ?? throw new ArgumentNullException(nameof(demux));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _videoDecoder = videoDecoder;
        _audioDecoder = audioDecoder;
        _videoSink = videoSink;
        _audioSink = audioSink;
        _videoConfigurator = videoConfigurator;
        _audioConfigurator = audioConfigurator;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Metadata describing the opened container and its streams.</summary>
    public MediaInfo Info => _demux.MediaInfo;

    /// <summary>The video decoder, or null for a pass with no video sink. Internal: tests read how it was opened.</summary>
    internal VideoDecoder? VideoDecoder => _videoDecoder;

    /// <summary>
    /// Demux counters. Internal: tests read how many packets the pump read.
    /// </summary>
    internal FrameFlow.Decoding.Diagnostics.DemuxSessionDiagnosticsSnapshot GetDemuxDiagnostics() =>
        _demux.GetDiagnostics();

    /// <summary>
    /// Completes when the demux pump is suspended on a full decoder queue.
    /// Internal: tests race it against <see cref="RunToCompletionAsync"/>.
    /// </summary>
    internal Task WaitUntilPumpParkedAsync() => _pipeline.WaitUntilParkedAsync();

    /// <summary>
    /// What a pass does at the edges of its run and through its dispose. Internal: tests read the
    /// order the steps are taken in, and act at one to drive a race (#696).
    /// </summary>
    internal enum Step
    {
        /// <summary>A run has been let start, on the scope that holds it, and has not yet built its graph.</summary>
        RunStarted,

        /// <summary>The run has ended: its pump and graph have finished, and it has made its last call on a sink.</summary>
        RunEnded,

        /// <summary>The dispose is about to free the pump pipeline.</summary>
        DisposingPipeline,

        /// <summary>The dispose is about to free the video decoder.</summary>
        DisposingVideoDecoder,

        /// <summary>The dispose is about to free the audio decoder.</summary>
        DisposingAudioDecoder,

        /// <summary>The dispose is about to free the demux session.</summary>
        DisposingDemux,

        /// <summary>The dispose is about to release the claims on the sinks.</summary>
        ReleasingClaims,
    }

    /// <summary>
    /// Called with each <see cref="Step"/> as the pass takes it, on the thread that takes it.
    /// Internal: a seam for tests, set before the run.
    /// </summary>
    internal Action<Step>? OnStep { get; set; }

    private void Take(Step step) => OnStep?.Invoke(step);

    /// <summary>
    /// Measures how long a dispose has waited for the run before it says what the run is still in.
    /// Internal: a seam for tests, set before the dispose.
    /// </summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>How long a dispose waits for the run before it says what the run is still in.</summary>
    internal static TimeSpan DisposeWarningInterval => SlowWorkWarning.DefaultInterval;

    /// <summary>
    /// Runs the graph to natural end-of-stream (both decoders EOF) or
    /// until <paramref name="ct"/> cancels. Single-shot per session —
    /// throws on second call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coordinates the demux pump task with the graph run: the pump
    /// reads packets and routes them to the decoders' packet queues;
    /// the graph drains the decoders' <c>DecodeAsync</c> enumerators
    /// into the sinks. When the pump reaches EOF it finalizes the
    /// decoders (flush + complete their packet queues), which lets
    /// the <c>DecodeAsync</c> enumerators exit cleanly and the graph
    /// sources reach EOS.
    /// </para>
    /// <para>
    /// The first side to fault cancels the other, and its fault is thrown once both have ended. A
    /// pump waiting for room in a decoder queue that a faulted graph no longer drains gives up
    /// there, rather than holding the run open (#713). A node's fault arrives as a
    /// <see cref="GraphFaultException"/> naming the node, with what it threw as the inner
    /// exception.
    /// </para>
    /// <para>
    /// <b>The audio sink.</b> The pass activates it before the run and deactivates it before this
    /// returns or throws, so a caller has no lifecycle call of its own to make. On a natural end it
    /// first awaits <see cref="IAudioSink.DrainAsync"/>, so the run returns once the device has
    /// played the last sample rather than once the last buffer was handed over (#609).
    /// </para>
    /// <para>
    /// <b>Disposing the pass.</b> <see cref="DisposeAsync"/> cancels a run in flight, which then
    /// throws <see cref="OperationCanceledException"/> here as it would for <paramref name="ct"/>,
    /// and waits for it to end before it frees anything the run uses (#696). A dispose awaited
    /// from inside the run, such as in a sink, would wait for the run, which waits for that sink,
    /// so neither would end: cancel <paramref name="ct"/> there instead.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The pass's dispose has begun.</exception>
    /// <exception cref="InvalidOperationException">The pass has run before.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled, or the pass was disposed, before the run ended.
    /// </exception>
    public async Task RunToCompletionAsync(CancellationToken ct = default)
    {
        PassRunStart start;
        lock (_lifecycleLock)
        {
            (_lifecycle, start) = _lifecycle.StartRun();

            // Under the lock, so a dispose that begins once this has let the run start finds the
            // run held, and waits for it. Only a dispose stops the scope, and it takes this lock
            // first, so the scope cannot refuse the run here.
            if (start == PassRunStart.Started)
            {
                var held = _work.TryStart("run", RunAsync, ct);
                Debug.Assert(held, "The scope was stopped before the dispose began.");
            }
        }

        ObjectDisposedException.ThrowIf(start == PassRunStart.Disposed, this);
        if (start == PassRunStart.AlreadyRan)
        {
            throw new InvalidOperationException(
                "MediaPass.RunToCompletionAsync is single-use. "
                    + "Open a new session for another playback."
            );
        }

        if (await _runEnded.Task.ConfigureAwait(false) is { } failure)
            ExceptionDispatchInfo.Throw(failure);
    }

    /// <summary>
    /// The pass's run, as the scope holds it: <paramref name="ct"/> is cancelled by the caller's
    /// token or by the dispose. It reports how it ended through <see cref="_runEnded"/>, whatever
    /// throws, a step's callback included, so it ends without a fault of its own.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            Take(Step.RunStarted);
            await RunGraphToEndAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            Take(Step.RunEnded);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
        finally
        {
            lock (_lifecycleLock)
                _lifecycle = _lifecycle.EndRun();
            _runEnded.TrySetResult(failure);
        }
    }

    /// <summary>The body of <see cref="RunToCompletionAsync"/>, once it has started the run.</summary>
    private async Task RunGraphToEndAsync(CancellationToken ct)
    {
        var hasVideo = _videoDecoder is not null && _videoSink is not null;
        var hasAudio = _audioDecoder is not null && _audioSink is not null;

        if (!hasVideo && !hasAudio)
        {
            // No sinks wired — nothing to drive. Caller intent unclear;
            // we could no-op, but throwing is friendlier than silent
            // success for a misconfigured builder.
            throw new InvalidOperationException(
                "No sinks attached. Call WithVideoSink and/or WithAudioSink before BuildAsync."
            );
        }

        // A pass is one run, so it is one timeline (ADR-0084, decision 2). Installed before the run
        // starts, which publishes it to the threads that decode.
        var timeline = TimelineId.Next();
        if (_videoDecoder is not null)
            _videoDecoder.Timeline = timeline;
        if (_audioDecoder is not null)
            _audioDecoder.Timeline = timeline;

        var graph = new Graph.Graph();

        if (hasVideo)
        {
            var source = Within(_videoDecoder!.AsSourceNode("video-source"), static frame => frame.Pts);
            WireVideo(graph, source, _videoConfigurator, _videoSink!);
        }

        if (hasAudio)
        {
            var source = Within(
                _audioDecoder!.AsSourceNode("audio-source"),
                static buffer => buffer.PresentationTime
            );
            var chain = graph.Pipeline(source);
            if (_audioConfigurator is not null)
                chain = _audioConfigurator(chain);
            chain.To(_audioSink!.AsSinkNode("audio-sink"));
        }

        // Bring the audio device up before the graph starts pushing PCM at
        // it. An IAudioSink is inert until activated: buffers presented to a
        // dormant sink are accepted and dropped, so without this the session
        // plays through in silence.
        //
        // The caller may have activated it already (the AudioOnlyPlayer
        // example did, because this line did not exist). Activation is
        // re-entrant, so the duplicate is harmless.
        //
        // This mirrors what SubstrateSession does on the controller path
        // (SubstrateSession.PlayAsync) and what the player builder does
        // on the player path. MediaPass was the one surface that left it
        // to the caller, which meant a sink the caller had not activated out of
        // band played silence (issue #60).
        if (hasAudio)
            await _audioSink!.ActivateAsync(ct).ConfigureAwait(false);

        // What the pass activated it deactivates, on every way out (#609). The sink may be the
        // container's singleton and outlive the pass, so leaving it active hands the next user a
        // device still running.
        try
        {
            await RunPumpAndGraphAsync(graph, ct).ConfigureAwait(false);

            // The graph completes once the last buffer has been handed to the sink, which is not
            // when it has been heard: a sink in front of a device still has a queue to play out.
            // Only a natural end drains. A cancelled or faulted run stops where it is.
            if (hasAudio)
                await _audioSink!.DrainAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (hasAudio)
            {
                try
                {
                    await _audioSink!.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The run's own failure is the one to report.
                    _logger.LogWarning(ex, "Deactivating the audio sink after a failed pass threw.");
                }
            }
            throw;
        }

        if (hasAudio)
            await _audioSink!.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the demux pump and <paramref name="graph"/> together until both have ended. The first
    /// to fault cancels the other, as <see cref="Graph.Graph.RunAsync"/> does for its nodes, and
    /// its fault is thrown once both have ended.
    /// </summary>
    /// <remarks>
    /// Waiting for both to end before cancelling either hung a run whose graph faulted while the
    /// pump waited for room in a decoder queue: the faulted graph no longer drains it, so the pump
    /// never ended, and nor did the run (#713).
    /// </remarks>
    private async Task RunPumpAndGraphAsync(Graph.Graph graph, CancellationToken ct)
    {
        var work = new WorkScope();
        var watch = new FirstEnd(2);
        Volatile.Write(ref _pumpAndGraph, work);
        try
        {
            work.TryStart("demux pump", watch.Watch(PumpAsync), ct);
            work.TryStart("graph", watch.Watch(graph.RunAsync), ct);

            // Until one of them throws, or both have completed. The stop then cancels whatever
            // still runs and waits for it, and returns each fault in the order it came. A
            // cancellation the stop or ct asked for is not one.
            await watch.Ended.ConfigureAwait(false);
            var faults = await work.StopAsync(CancellationToken.None).ConfigureAwait(false);

            if (faults.Count > 0)
            {
                foreach (var later in faults.Skip(1))
                    _logger.LogDebug(later.Error, "The pass's {Work} also faulted as the run ended.", later.Work);
                ExceptionDispatchInfo.Throw(faults[0].Error);
            }

            // Both ended without a fault, and one of them did not complete: ct cancelled it.
            if (!watch.AllCompleted)
                throw new OperationCanceledException(ct);
        }
        finally
        {
            Volatile.Write(ref _pumpAndGraph, null);
        }
    }

    /// <summary>
    /// The demux pump: reads packets into the decoders' queues, then finalizes the decoders on
    /// every way out (end of stream, cancellation, fault) so their enumerators end and the graph's
    /// sources reach end of stream.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await _pipeline.RunDemuxPumpAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // The flush marker waits for room in a decoder queue. Once the run is cancelled, or
            // the graph has faulted, nothing drains the queue, so a full one never frees up. The
            // token is cancelled in both cases, by the run or by the graph's fault, and the flush
            // gives up. The queue is completed either way.
            try
            {
                await _pipeline.FinalizeDecodersAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The pump's own end, or the graph's fault, is the one to report.
                _logger.LogDebug(ex, "Finalizing the decoders after the pass's demux pump threw.");
            }
        }
    }

    /// <summary>
    /// Watches the work of one run: <see cref="Ended"/> completes once a piece of it has thrown,
    /// or once all of it has completed, so the run can stop the rest at the first fault rather
    /// than after everything has ended (#713).
    /// </summary>
    private sealed class FirstEnd(int count)
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _count = count;
        private int _running = count;
        private int _completed;

        /// <summary>Completes once a piece has thrown, or all have ended. Never faults.</summary>
        public Task Ended => _ended.Task;

        /// <summary>Whether every piece completed without throwing.</summary>
        public bool AllCompleted => Volatile.Read(ref _completed) == _count;

        /// <summary><paramref name="work"/>, reporting its end here and ending as it ends.</summary>
        public Func<CancellationToken, Task> Watch(Func<CancellationToken, Task> work) =>
            async ct =>
            {
                try
                {
                    await work(ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _completed);
                }
                catch
                {
                    _ended.TrySetResult();
                    throw;
                }
                finally
                {
                    if (Interlocked.Decrement(ref _running) == 0)
                        _ended.TrySetResult();
                }
            };
    }

    /// <summary>
    /// <paramref name="source"/>, delivering only what the pass's range does (#483). An item
    /// outside it is released as soon as it is decoded, and the source reads on. The whole
    /// source is returned as it is.
    /// </summary>
    private SourceNode<T> Within<T>(SourceNode<T> source, Func<T, TimeSpan> timestamp)
        where T : class, IRefCounted
    {
        if (_range.IsWhole)
            return source;

        var range = _range;
        return new SourceNode<T>(
            source.Id,
            async ct =>
            {
                while (await source.Body(ct).ConfigureAwait(false) is { } item)
                {
                    if (range.Delivers(timestamp(item)))
                        return item;
                    item.Dispose();
                }
                return null;
            },
            source.OnError,
            source.Cleanup,
            source.OnBudget,
            source.Emits
        );
    }

    /// <summary>
    /// The video path's one shape: source, then the consumer's configurator, then the sink.
    /// <see cref="RunToCompletionAsync"/> and <see cref="VideoFrameBudget"/> both wire it here, so
    /// the budget is computed on the path that runs.
    /// </summary>
    private static void WireVideo(
        Graph.Graph graph,
        SourceNode<IVideoFrame> source,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configurator,
        IVideoSink sink
    )
    {
        var chain = graph.Pipeline(source);
        if (configurator is not null)
            chain = configurator(chain);
        chain.To(sink.AsSinkNode("video-sink"));
    }

    /// <summary>
    /// The frame budget of a pass's video path over <paramref name="sink"/> (ADR-0081, decision 3):
    /// the most decoded frames the path can hold at once, or the node that leaves it unbounded.
    /// </summary>
    /// <remarks>
    /// Computed on a copy of the path with a stand-in source, so it is known before the decoder
    /// opens and can size its pool (#292). The configurator runs once more to build the copy, as
    /// the player's does, and its contract is to wire the same path each time.
    /// </remarks>
    internal static FrameBudget VideoFrameBudget(
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configurator,
        IVideoSink sink
    ) => VideoProbe(configurator, sink).Budget;

    /// <summary>
    /// A copy of a pass's video path over a stand-in source, built before the decoder opens: its
    /// frame budget sizes the decoder's pool, and once the decoder has opened, it says whether a
    /// node on the path cannot take the memory domains the decoder hands out (#435).
    /// </summary>
    internal static VideoPathProbe VideoProbe(
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configurator,
        IVideoSink sink
    )
    {
        ArgumentNullException.ThrowIfNull(sink);
        var graph = new Graph.Graph();
        var source = new SourceNode<IVideoFrame>("video-source", static _ => default);
        WireVideo(graph, source, configurator, sink);
        return new VideoPathProbe(graph, source.Output);
    }

    /// <summary>A copy of a video path, wired from a stand-in source that never runs.</summary>
    internal sealed record VideoPathProbe(Graph.Graph Graph, OutputPort<IVideoFrame> Source)
    {
        /// <summary>The most frames the path can hold, or the node that leaves it unbounded.</summary>
        public FrameBudget Budget => Graph.FrameBudgetFor(Source);

        /// <summary>A node on the path that one of <paramref name="emitted"/> reaches and that does not take it.</summary>
        public FrameDomainMismatch? MismatchFor(FrameMemoryDomains emitted) => Graph.FrameDomainMismatchFor(Source, emitted);

        /// <summary>
        /// When no GPU domain gets through the path, the first node that GPU frames would reach and
        /// that has not said it takes them; null when one domain does (#294, #566).
        /// </summary>
        public FrameDomainMismatch? UndeclaredGpuConsumer() =>
            Graph.FrameDomainsAcceptedFrom(Source, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly) == FrameMemoryDomains.None
                ? Graph.FrameDomainMismatchFor(Source, FrameMemoryDomains.Gpu, FrameDomainRule.CpuOnly)
                : null;

        /// <summary>
        /// The first node that refuses <paramref name="backend"/>'s frames, with a node that says
        /// nothing taking CPU frames, or null when every node takes them (#566).
        /// </summary>
        public FrameDomainMismatch? RefusalOf(HardwareDecodeBackendKind backend) =>
            Graph.FrameDomainMismatchFor(Source, HardwareFrameDomains.Of(backend), FrameDomainRule.CpuOnly);
    }

    /// <summary>
    /// Cancels a run in flight and waits for it to end, then frees the pump pipeline, the decoders
    /// and the demux session, and releases the claims on the sinks last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dispose waits as long as the run takes to unwind once cancelled, with no bound
    /// (ADR-0086, decision 3). A sink's present, drain or activation, a node in a configurator, or
    /// a video sink's <see cref="IVideoSink.OnFormatChangedAsync"/> that ignores its token holds it
    /// there, and so does a source whose read blocks, such as a stalled network stream: nothing
    /// interrupts the demuxer's read (#709). Once it has waited
    /// <see cref="DisposeWarningInterval"/>, it logs once, at Warning, which of the run's work has
    /// not ended. A caller that cannot wait that long bounds its own wait; the pass keeps what the
    /// run uses until the run ends.
    /// </para>
    /// <para>
    /// Throws for neither the run's cancellation nor a fault the run ends with: the run's caller
    /// sees both. A fault is logged here only when this dispose cancelled the run that ended with
    /// it. What freeing the pipeline, the decoders or the demux session throws is thrown once the
    /// claims are released: one exception, or an <see cref="AggregateException"/> of several. The
    /// first call tears down; a call made while it runs, or after, waits for it and ends as it did.
    /// The sinks are the caller's, and are not disposed.
    /// </para>
    /// </remarks>
    public ValueTask DisposeAsync() =>
        _dispose.TryBegin() ? _dispose.EndAsync(TearDownAsync()) : _dispose.WaitAsync();

    /// <summary>The teardown the first <see cref="DisposeAsync"/> runs.</summary>
    private async ValueTask TearDownAsync()
    {
        PassTeardown teardown;
        lock (_lifecycleLock)
            (_lifecycle, teardown) = _lifecycle.Dispose();

        List<Exception>? failures = null;
        try
        {
            await EndRunAsync(inFlight: teardown == PassTeardown.AfterTheRun).ConfigureAwait(false);
            failures = await FreeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Last, once the run has ended, so nothing it sent reaches a sink after a pass built
            // next has claimed it, and that pass cannot claim the sinks while this one still holds
            // a decoder. Released whatever freeing threw.
            try
            {
                Take(Step.ReleasingClaims);
            }
            finally
            {
                _sinkClaims?.Dispose();
            }
        }

        switch (failures)
        {
            case null:
                break;
            case [var one]:
                ExceptionDispatchInfo.Throw(one);
                break;
            default:
                throw new AggregateException(failures);
        }
    }

    /// <summary>
    /// Cancels the run, if one has started, and waits for it to end, however long it takes. Logs
    /// a fault the run ends with when it was in flight as the dispose began.
    /// </summary>
    private async Task EndRunAsync(bool inFlight)
    {
        if (inFlight)
            _logger.LogDebug("A pass disposed during its run cancels the run and waits for it to end.");

        // The stop cancels the run and waits for it. The run reports how it ended to its caller,
        // so it leaves no fault for the stop to return. The cancellation's callbacks, the run's
        // own unwinding among them, run on the thread pool rather than on the caller's thread.
        await SlowWorkWarning
            .AwaitAsync(_work.StopAsync(), DisposeWarningInterval, TimeProvider, WarnRunNotEnded)
            .ConfigureAwait(false);

        // The scope has let the run go, so it has reported how it ended.
        if (inFlight && await _runEnded.Task.ConfigureAwait(false) is { } failure and not OperationCanceledException)
            _logger.LogWarning(failure, "The run of a pass being disposed ended with a fault.");
    }

    /// <summary>Says which of the run's work a dispose is still waiting for (ADR-0086, decision 3.3).</summary>
    private void WarnRunNotEnded()
    {
        var pending = Volatile.Read(ref _pumpAndGraph)?.Pending is { Count: > 0 } inner ? inner : _work.Pending;
        _logger.LogWarning(
            "A pass's dispose has waited {Seconds:F0}s for its run to end, which is still running: {Work}. "
                + "A sink, a configurator's node or the source's read is not observing cancellation; the dispose waits for it.",
            DisposeWarningInterval.TotalSeconds,
            string.Join(", ", pending)
        );
    }

    /// <summary>
    /// Frees what a run reads from, in reverse construction order: the pipeline, which frees any
    /// packet it holds, then the decoders, then the demux session. Each runs whatever the one
    /// before it threw, and what they threw is returned, or null when nothing did.
    /// </summary>
    private async ValueTask<List<Exception>?> FreeAsync()
    {
        List<Exception> failures = [];
        await FreeAsync(Step.DisposingPipeline, _pipeline, failures).ConfigureAwait(false);
        if (_videoDecoder is not null)
            await FreeAsync(Step.DisposingVideoDecoder, _videoDecoder, failures).ConfigureAwait(false);
        if (_audioDecoder is not null)
            await FreeAsync(Step.DisposingAudioDecoder, _audioDecoder, failures).ConfigureAwait(false);
        await FreeAsync(Step.DisposingDemux, _demux, failures).ConfigureAwait(false);
        return failures.Count > 0 ? failures : null;
    }

    /// <summary>
    /// Takes <paramref name="step"/>, then frees <paramref name="resource"/>, adding what either
    /// throws to <paramref name="failures"/>. A step's callback that throws does not keep the
    /// resource from being freed.
    /// </summary>
    private async ValueTask FreeAsync(Step step, IAsyncDisposable resource, List<Exception> failures)
    {
        try
        {
            Take(step);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }
}
