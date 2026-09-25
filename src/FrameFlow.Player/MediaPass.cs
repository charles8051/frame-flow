// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Decoding;
using FrameFlow.Playback;
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
/// once and handed to any number of passes.
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

    private int _started;
    private bool _disposed;

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
        IPlaybackClock? clock = null
    )
    {
        Clock = clock;
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
    /// If either side faults the other is cancelled via the linked
    /// CTS; the exception propagates after both have unwound.
    /// </para>
    /// </remarks>
    public async Task RunToCompletionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "MediaPass.RunToCompletionAsync is single-use. "
                    + "Open a new session for another playback."
            );
        }

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

        var graph = new Graph.Graph();

        if (hasVideo)
        {
            var source = _videoDecoder!.AsSourceNode("video-source");
            var chain = graph.Pipeline(source);
            if (_videoConfigurator is not null)
                chain = _videoConfigurator(chain);
            chain.To(_videoSink!.AsSinkNode("video-sink"));
        }

        if (hasAudio)
        {
            var source = _audioDecoder!.AsSourceNode("audio-source");
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Pump task: read packets → decoder queues. Finalize decoders
        // on exit (EOF, cancel, fault) so the DecodeAsync enumerators
        // terminate; otherwise the source nodes block forever.
        var pumpTask = Task.Run(
            async () =>
            {
                try
                {
                    await _pipeline.RunDemuxPumpAsync(cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    // The flush marker waits for space in the decoder queue. Once
                    // the run is cancelled or faulted the graph stops draining, so
                    // a full queue never frees up: pass the linked token so the
                    // flush gives up. The queue is completed either way.
                    try
                    {
                        await _pipeline.FinalizeDecodersAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch
                    { /* swallow — graph will unwind on its own */
                    }
                }
            },
            cts.Token
        );

        var graphTask = graph.RunAsync(cts.Token);

        try
        {
            await Task.WhenAll(pumpTask, graphTask).ConfigureAwait(false);
        }
        catch
        {
            // Either side faulted — cancel the other and let it unwind
            // before re-raising the original exception.
            try
            {
                cts.Cancel();
            }
            catch
            { /* CTS already disposed by linked-source dispose */
            }
            try
            {
                await Task.WhenAll(pumpTask, graphTask).ConfigureAwait(false);
            }
            catch
            { /* secondary failures during unwind are not interesting */
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Dispose in reverse construction order:
        //   pipeline (clears any pending packet) → decoders → demux.
        // Sinks are owned by the caller and not disposed here.
        try
        {
            await _pipeline.DisposeAsync().ConfigureAwait(false);
        }
        catch
        { /* best-effort */
        }

        if (_videoDecoder is not null)
        {
            try
            {
                await _videoDecoder.DisposeAsync().ConfigureAwait(false);
            }
            catch
            { /* best-effort */
            }
        }

        if (_audioDecoder is not null)
        {
            try
            {
                await _audioDecoder.DisposeAsync().ConfigureAwait(false);
            }
            catch
            { /* best-effort */
            }
        }

        try
        {
            await _demux.DisposeAsync().ConfigureAwait(false);
        }
        catch
        { /* best-effort */
        }
    }
}
