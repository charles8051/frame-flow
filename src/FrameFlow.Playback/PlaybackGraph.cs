// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Minimal playback graph on the substrate. Wires decoder
/// sources to video / audio sinks via the adapters,
/// then runs the graph to natural EOS (or external cancellation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> This is the lean Phase-3 sibling to the old
/// <c>FrameFlow.Playback.PipelineController</c>. It covers the
/// happy path — open file, play to end — using the substrate
/// as the underlying graph. It does NOT replicate the full
/// <c>IPlaybackController</c> surface (state machine, seek, repeat,
/// observables, pull-mode channels, etc.); each of those needs
/// its own design pass against the substrate's scheduling and
/// cancellation primitives.
/// </para>
/// <para>
/// <b>What this proves.</b> An end-to-end playback path —
/// decoder → operator chain → sink — works on the substrate
/// without any of the old <c>FramePipeline&lt;T&gt;</c> machinery.
/// Demos and the full-controller port can build on this.
/// </para>
/// <para>
/// <b>Frame-pool / clock-source handling.</b> The graph doesn't
/// own the video sink's frame pool or the audio sink's clock —
/// callers construct and activate sinks externally, same as the
/// old <c>WithVideoSink</c>/<c>WithAudioSink</c> pattern. The
/// graph only handles the data plane.
/// </para>
/// </remarks>
public sealed class PlaybackGraph : IAsyncDisposable
{
    private readonly IVideoDecoder? _videoDecoder;
    private readonly IAudioDecoder? _audioDecoder;
    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private bool _disposed;

    /// <summary>
    /// Constructs a playback graph from the given decoders and
    /// sinks. Either pair can be null when the source has no
    /// corresponding stream (audio-only files, video-only files).
    /// </summary>
    public PlaybackGraph(
        IVideoDecoder? videoDecoder = null,
        IAudioDecoder? audioDecoder = null,
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null
    )
    {
        if (videoDecoder is null && audioDecoder is null)
            throw new ArgumentException(
                "At least one of videoDecoder or audioDecoder must be provided."
            );

        _videoDecoder = videoDecoder;
        _audioDecoder = audioDecoder;
        _videoSink = videoSink;
        _audioSink = audioSink;
    }

    /// <summary>
    /// Builds the graph and runs it to completion. Returns when:
    /// </summary>
    /// <list type="bullet">
    /// <item>both decoders reach EOS, OR</item>
    /// <item><paramref name="ct"/> is cancelled, OR</item>
    /// <item>any node faults.</item>
    /// </list>
    /// <remarks>
    /// Re-callable: each call builds a fresh graph. The underlying
    /// decoders are stateful, however — calling
    /// <see cref="PlayToCompletionAsync"/> a second time on a
    /// decoder that's already reached EOS produces an empty graph.
    /// Demos that want loop / restart semantics need to recreate
    /// the decoders too (deferred per Crossbar ADR-0014 Phase 3 long pole).
    /// </remarks>
    public async Task PlayToCompletionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var graph = new Graph.Graph();

        if (_videoDecoder is not null && _videoSink is not null)
        {
            var source = _videoDecoder.AsSourceNode("video-source");
            var sink = _videoSink.AsSinkNode("video-sink");
            graph.Pipeline(source).To(sink);
        }

        if (_audioDecoder is not null && _audioSink is not null)
        {
            var source = _audioDecoder.AsSourceNode("audio-source");
            var sink = _audioSink.AsSinkNode("audio-sink");
            graph.Pipeline(source).To(sink);
        }

        await graph.RunAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
