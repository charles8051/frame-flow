// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Decoding;
using FrameFlow.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FrameFlow.Graph;

namespace FrameFlow.Player;

/// <summary>
/// Concrete <see cref="IPlayerBuilder"/>. Mutable fluent state +
/// a <see cref="BuildAsync"/> that bootstraps FFmpeg, opens the
/// demux session, constructs decoders, and hands ownership to a
/// <see cref="PlayerSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no DI container.</b> The old
/// <see cref="FrameFlow.Player.PlayerBuilder"/> assembles a
/// <c>ServiceProvider</c> on the fly to compose
/// <see cref="IPlaybackControllerFactory"/> + decoder factories +
/// sink registrations + options + logger. The Next builder doesn't
/// need any of that — it constructs the demux + decoder pieces
/// directly because the substrate doesn't depend on
/// <see cref="FrameFlow.Playback.IPlaybackController"/>. Skipping
/// the DI layer makes the implementation about half the LOC and
/// removes a per-build allocation tree.
/// </para>
/// </remarks>
internal sealed class PlayerBuilder : IPlayerBuilder
{
    private readonly IMediaSource _source;
    private IVideoSink? _videoSink;
    private IAudioSink? _audioSink;
    private Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? _videoConfigurator;
    private Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? _audioConfigurator;
    private HardwareDecodeMode _hwMode = HardwareDecodeMode.Auto;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    internal PlayerBuilder(IMediaSource source) => _source = source;

    public IPlayerBuilder WithVideoSink(IVideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _videoSink = sink;
        return this;
    }

    public IPlayerBuilder WithAudioSink(IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _audioSink = sink;
        return this;
    }

    public IPlayerBuilder ConfigureVideo(
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        _videoConfigurator = configure;
        return this;
    }

    public IPlayerBuilder ConfigureAudio(
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        _audioConfigurator = configure;
        return this;
    }

    public IPlayerBuilder WithHardwareDecode(HardwareDecodeMode mode)
    {
        _hwMode = mode;
        return this;
    }

    public IPlayerBuilder WithLogger(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    public async Task<PlayerSession> BuildAsync(CancellationToken cancellationToken = default)
    {
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

            // Build the decoder factory delegates. CreateVideo wires
            // HardwareDecodeOptions + capabilities; CreateAudio threads
            // the logger factory so AudioDecoder diagnostics aren't
            // silently swallowed by NullLogger.Instance (the asymmetry
            // that hid the post-seek freeze bug fixed in d03e4b0).
            var videoFactory = DecoderFactories.CreateVideo(
                new HardwareDecodeOptions { Mode = _hwMode },
                bootstrapResult.Capabilities,
                _loggerFactory
            );
            var audioFactory = DecoderFactories.CreateAudio(_loggerFactory);

            // DecoderFactories return interfaces, but the concrete types
            // are always VideoDecoder / AudioDecoder — DecodingPipeline
            // constructor requires the concrete types because it reaches
            // into their packet-queue surface that isn't on the public
            // interfaces.
            videoDecoder = videoFactory(demux) as VideoDecoder;
            audioDecoder = audioFactory(demux) as AudioDecoder;

            if (videoDecoder is null && audioDecoder is null)
            {
                throw new InvalidOperationException(
                    $"Source '{_source.DisplayName}' has neither a video nor audio stream "
                        + "the registered factories can decode."
                );
            }

            // DecodingPipeline owns the demux pump; it requires the
            // concrete DemuxSession (it reaches FormatContextPtr through
            // it). The demux factory always returns DemuxSession today.
            var concreteDemux =
                demux as DemuxSession
                ?? throw new InvalidOperationException(
                    $"DemuxSessionFactory returned unexpected type {demux.GetType().Name}; "
                        + $"DecodingPipeline requires {nameof(DemuxSession)}."
                );

            var pipeline = new DecodingPipeline(
                concreteDemux,
                videoDecoder,
                audioDecoder,
                _loggerFactory.CreateLogger<DecodingPipeline>()
            );

            // Hand ownership to PlayerSession; suppress outer dispose.
            var session = new PlayerSession(
                demux,
                pipeline,
                videoDecoder,
                audioDecoder,
                _videoSink,
                _audioSink,
                _videoConfigurator,
                _audioConfigurator,
                _loggerFactory.CreateLogger<PlayerSession>()
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
