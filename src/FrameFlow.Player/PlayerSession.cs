// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Decoding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FrameFlow.Graph;

namespace FrameFlow.Player;

/// <summary>
/// A built playback session on the substrate. Owns the open
/// demux session, the decoders, and the demux pump pipeline;
/// constructs and runs a fresh graph each call to
/// <see cref="PlayToCompletionAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-shot.</b> <see cref="PlayToCompletionAsync"/> can be
/// called exactly once per session — the underlying demux + decoders
/// are stateful and reach EOS after one full run. Demos that want to
/// loop or restart re-open via the builder. The full
/// pause/resume/seek/repeat surface stays on
/// <see cref="FrameFlow.Playback.IPlaybackController"/> until its
/// port to the substrate lands.
/// </para>
/// <para>
/// <b>Sink ownership.</b> Sinks are passed in from the caller; the
/// session does not dispose them. This matches the convention of
/// <see cref="FrameFlow.Player.PlayerBuilder.WithVideoSink"/> and with
/// ADR-0044: a sink is owned by whoever constructed it, and the player
/// layer is a user rather than an owner. Examples dispose their own sinks.
/// </para>
/// </remarks>
public sealed class PlayerSession : IAsyncDisposable
{
    private readonly IDemuxSession _demux;
    private readonly DecodingPipeline _pipeline;
    private readonly VideoDecoder? _videoDecoder;
    private readonly AudioDecoder? _audioDecoder;
    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? _videoConfigurator;
    private readonly Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? _audioConfigurator;
    private readonly ILogger _logger;

    private int _started;
    private bool _disposed;

    internal PlayerSession(
        IDemuxSession demux,
        DecodingPipeline pipeline,
        VideoDecoder? videoDecoder,
        AudioDecoder? audioDecoder,
        IVideoSink? videoSink,
        IAudioSink? audioSink,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? videoConfigurator,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? audioConfigurator,
        ILogger? logger = null
    )
    {
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
    public async Task PlayToCompletionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "PlayerSession.PlayToCompletionAsync is single-use. "
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
        // (SubstrateSession.PlayAsync) and what MediaPlayer.CreateAsync does
        // on the player path. PlayerSession was the one surface that left it
        // to the caller, which is why the WithOpenAlAudio builder shortcut —
        // which constructs the sink internally and never hands it back —
        // could not be used to play anything (issue #60).
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
                    try
                    {
                        await _pipeline.FinalizeDecodersAsync().ConfigureAwait(false);
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
