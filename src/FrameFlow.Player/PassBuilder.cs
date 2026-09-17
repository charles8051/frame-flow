// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player;

/// <summary>
/// Concrete <see cref="IPassBuilder"/>. Mutable fluent state, and a <see cref="BuildAsync"/> that
/// bootstraps FFmpeg, opens the demux session, constructs the decoders and hands ownership to a
/// <see cref="MediaPass"/>.
/// </summary>
/// <remarks>
/// <b>Why no DI container.</b> This builder constructs the demux session and decoders directly
/// rather than standing up a <c>ServiceProvider</c> to resolve them. It can, because a
/// <see cref="MediaPass"/> does not depend on <see cref="FrameFlow.Playback.IPlaybackController"/>
/// or on anything else registered through <see cref="FrameFlow.Media.IFrameFlowBuilder"/>.
/// Skipping the DI layer removes a per-build allocation tree.
/// </remarks>
internal sealed class PassBuilder : IPassBuilder
{
    private readonly IMediaSource _source;
    private IVideoSink? _videoSink;
    private IAudioSink? _audioSink;
    private Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? _videoConfigurator;
    private Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? _audioConfigurator;
    private HardwareDecodeMode _hwMode = HardwareDecodeMode.Auto;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private VideoDecoderOptions? _videoDecoderOptions;
    private AudioDecoderOptions? _audioDecoderOptions;
    private IPlaybackClock? _clock;

    internal PassBuilder(IMediaSource source) => _source = source;

    public IPassBuilder WithVideoSink(IVideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _videoSink = sink;
        return this;
    }

    public IPassBuilder WithAudioSink(IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _audioSink = sink;
        return this;
    }

    public IPassBuilder ConfigureVideo(
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        _videoConfigurator = configure;
        return this;
    }

    public IPassBuilder ConfigureAudio(
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        _audioConfigurator = configure;
        return this;
    }

    public IPassBuilder WithHardwareDecode(HardwareDecodeMode mode)
    {
        _hwMode = mode;
        return this;
    }

    public IPassBuilder WithLogger(ILoggerFactory? loggerFactory)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        return this;
    }

    /// <summary>
    /// Decoder options for <see cref="BuildAsync"/>. Internal: tests shrink the packet queues so a
    /// full queue shows up after a few packets.
    /// </summary>
    internal PassBuilder WithDecoderOptions(
        VideoDecoderOptions? video = null,
        AudioDecoderOptions? audio = null
    )
    {
        _videoDecoderOptions = video;
        _audioDecoderOptions = audio;
        return this;
    }

    /// <summary>
    /// The clock the built pass carries and never reads. Internal, because a consumer has nothing
    /// to supply: a pass waits on no presentation time. A test hands one that throws on every read
    /// and asserts the run completes, which is what makes that claim falsifiable.
    /// See <see cref="MediaPass.Clock"/>.
    /// </summary>
    internal PassBuilder WithClock(IPlaybackClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        return this;
    }

    /// <summary>
    /// Refuses a pass with nowhere to put what it decodes.
    /// </summary>
    /// <remarks>
    /// A run with no sink decodes the whole source and drops every frame, which is never what was
    /// meant. The check is here rather than in the type system because a video-only and an
    /// audio-only source each need a different one of the two sinks, and which streams a file
    /// carries is not known until it is opened. Refusing at the terminal at least gets the answer
    /// out before the demuxer opens anything.
    /// </remarks>
    private void RequireASink()
    {
        if (_videoSink is null && _audioSink is null)
        {
            throw new InvalidOperationException(
                "No sinks attached. Call WithVideoSink and/or WithAudioSink before BuildAsync — a "
                    + "pass with no sink decodes the whole source and drops it. HeadlessVideoSink "
                    + "is the terminal for a run that presents nothing."
            );
        }
    }

    /// <summary>
    /// Refuses a configurator with no sink to terminate at.
    /// </summary>
    /// <remarks>
    /// A configurator transforms a stream on its way to a sink and returns the chain open; the
    /// builder wires the sink. Without one there is nothing to wire, and the configurator would
    /// run against a graph that presents nothing. A consumer that needs extra sinks wires them
    /// on <c>Branch</c> edges inside the configurator and returns its trunk open.
    /// </remarks>
    private void RequireSinkForEachConfigurator()
    {
        if (_videoConfigurator is not null && _videoSink is null)
        {
            throw new InvalidOperationException(
                "ConfigureVideo was called without WithVideoSink. The configurator returns its "
                    + "chain open and the builder terminates it at the video sink, so there has "
                    + "to be one. Wire any extra sinks on Branch edges inside the configurator."
            );
        }

        if (_audioConfigurator is not null && _audioSink is null)
        {
            throw new InvalidOperationException(
                "ConfigureAudio was called without WithAudioSink. The configurator returns its "
                    + "chain open and the builder terminates it at the audio sink, so there has "
                    + "to be one. Wire any extra sinks on Branch edges inside the configurator."
            );
        }
    }

    public async Task<MediaPass> BuildAsync(CancellationToken cancellationToken = default)
    {
        RequireASink();
        RequireSinkForEachConfigurator();

        // Bootstrap the FFmpeg native runtime — same eager call the
        // existing examples make manually. Idempotent across calls; a
        // shared bootstrapper would also work but constructing a fresh
        // one keeps the builder dependency-free.
        //
        // Skip the HW probe when the caller has explicitly disabled HW
        // decoding — the probe's "no device available" diagnostics are
        // wasted work in that case, and on some test hosts the
        // device-init dance is fragile.
        var nativeOptions = new FrameFlowNativeOptions
        {
            SkipHardwareProbe = _hwMode == HardwareDecodeMode.Disabled,
        };
        var bootstrapper = new FrameFlowBootstrapper(nativeOptions, _loggerFactory);
        var bootstrapResult = bootstrapper.Initialize();
        if (!bootstrapResult.IsSuccess)
        {
            throw new InvalidOperationException(
                $"FFmpeg bootstrap failed: {bootstrapResult.Message}"
            );
        }

        var demuxFactory = new DemuxSessionFactory(_loggerFactory);

        IDemuxSession? demux = null;
        VideoDecoder? videoDecoder = null;
        AudioDecoder? audioDecoder = null;

        try
        {
            demux = await demuxFactory.OpenAsync(_source, cancellationToken).ConfigureAwait(false);

            // DecodingPipeline owns the demux pump; it requires the
            // concrete DemuxSession (it reaches FormatContextPtr through
            // it). The demux factory always returns DemuxSession today.
            var concreteDemux =
                demux as DemuxSession
                ?? throw new InvalidOperationException(
                    $"DemuxSessionFactory returned unexpected type {demux.GetType().Name}; "
                        + $"DecodingPipeline requires {nameof(DemuxSession)}."
                );

            if (
                demux.MediaInfo.VideoStreams.Count == 0
                && demux.MediaInfo.AudioStreams.Count == 0
            )
            {
                throw new InvalidOperationException(
                    $"Source '{_source.DisplayName}' has neither a video nor audio stream."
                );
            }

            // ADR-0059: decode a stream only when a sink will drain it. The
            // demux pump feeds every decoder's bounded packet queue and waits
            // while any of them is full, and RunToCompletionAsync builds a
            // graph branch only for a stream that has a sink. A decoder with
            // no sink fills its queue, stops the pump, and freezes the stream
            // that is playing. A configurator alone is not a consumer here:
            // RunToCompletionAsync applies it only on a branch with a sink.
            //
            // A stream with no sink is discarded at the demuxer so its packets
            // are never read, and gets no decoder, so the few packets the probe
            // buffered before the discard have no queue to fill.
            //
            // DecoderFactories return interfaces, but the concrete types
            // are always VideoDecoder / AudioDecoder — DecodingPipeline
            // constructor requires the concrete types because it reaches
            // into their packet-queue surface that isn't on the public
            // interfaces.
            if (_videoSink is not null)
            {
                videoDecoder =
                    DecoderFactories.CreateVideo(
                        new HardwareDecodeOptions { Mode = _hwMode },
                        bootstrapResult.Capabilities,
                        _loggerFactory,
                        _videoDecoderOptions
                    )(demux) as VideoDecoder;
            }
            else
            {
                foreach (var stream in demux.MediaInfo.VideoStreams)
                    concreteDemux.DiscardStream(stream.StreamIndex);
            }

            // CreateAudio threads the logger factory so AudioDecoder
            // diagnostics aren't silently swallowed by NullLogger.Instance
            // (the asymmetry that hid the post-seek freeze bug fixed in d03e4b0).
            if (_audioSink is not null)
            {
                audioDecoder =
                    DecoderFactories.CreateAudio(_loggerFactory, _audioDecoderOptions)(demux)
                    as AudioDecoder;
            }
            else
            {
                foreach (var stream in demux.MediaInfo.AudioStreams)
                    concreteDemux.DiscardStream(stream.StreamIndex);
            }

            var pipeline = new DecodingPipeline(
                concreteDemux,
                videoDecoder,
                audioDecoder,
                _loggerFactory.CreateLogger<DecodingPipeline>()
            );

            // Hand ownership to MediaPass; suppress outer dispose.
            var session = new MediaPass(
                demux,
                pipeline,
                videoDecoder,
                audioDecoder,
                _videoSink,
                _audioSink,
                _videoConfigurator,
                _audioConfigurator,
                _loggerFactory.CreateLogger<MediaPass>(),
                _clock
            );
            demux = null;
            videoDecoder = null;
            audioDecoder = null;
            return session;
        }
        catch
        {
            // Dispose anything we managed to construct before failing.
            if (videoDecoder is not null)
            {
                try
                {
                    await videoDecoder.DisposeAsync().ConfigureAwait(false);
                }
                catch
                { /* swallow during cleanup */
                }
            }
            if (audioDecoder is not null)
            {
                try
                {
                    await audioDecoder.DisposeAsync().ConfigureAwait(false);
                }
                catch
                { /* swallow during cleanup */
                }
            }
            if (demux is not null)
            {
                try
                {
                    await demux.DisposeAsync().ConfigureAwait(false);
                }
                catch
                { /* swallow during cleanup */
                }
            }
            throw;
        }
    }
}
