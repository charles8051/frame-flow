// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Decoding;
using FrameFlow.Native;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FrameFlow.Graph;

namespace FrameFlow.Player;

/// <summary>
/// Concrete <see cref="IPlayerBuilder"/>: mutable fluent state, and one terminal that hands it
/// all to <see cref="PlayerFactory.CreateAsync"/>.
/// </summary>
/// <remarks>
/// The unpaced sibling is <see cref="PassBuilder"/>, which builds a <see cref="MediaPass"/> and
/// does the demux and decoder construction itself. The two were one class until
/// ADR-0079 split them.
/// </remarks>
internal sealed class PlayerBuilder : IPlayerBuilder
{
    private IReadOnlyList<IMediaSource> _sources = [];
    private IVideoSink? _videoSink;
    private IAudioSink? _audioSink;
    private Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? _videoConfigurator;
    private Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? _audioConfigurator;
    private HardwareDecodeMode _hwMode = HardwareDecodeMode.Auto;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    private RepeatMode _repeatMode = RepeatMode.Off;
    private IPlaybackClock? _clock;
    private LatenessRecoveryOptions? _latenessRecovery;
    private TimeProvider? _timeProvider;
    private bool _yieldHardwareFrames;
    private bool _activateAudioSink = true;

    public IPlayerBuilder WithMedia(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _sources = [MediaSource.FromFile(path)];
        return this;
    }

    public IPlayerBuilder WithMedia(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources = [source];
        return this;
    }

    public IPlayerBuilder WithMedia(IEnumerable<IMediaSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
        return this;
    }

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

    public IPlayerBuilder WithLogger(ILoggerFactory? loggerFactory)
    {
        // Null is a no-op rather than a throw so a conditional logging
        // step stays inside the chain instead of forcing the caller to
        // reassign the builder to a local.
        if (loggerFactory is not null)
        {
            _loggerFactory = loggerFactory;
        }
        return this;
    }

    public IPlayerBuilder WithRepeatMode(RepeatMode mode)
    {
        _repeatMode = mode;
        return this;
    }

    public IPlayerBuilder WithClock(IPlaybackClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        return this;
    }

    public IPlayerBuilder WithTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        return this;
    }

    public IPlayerBuilder WithLatenessRecovery(LatenessRecoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Validate here rather than leaving it to the session factory, so an inverted hysteresis
        // names the call that set it instead of surfacing from BuildPlayerAsync.
        options.Validate();
        _latenessRecovery = options;
        return this;
    }

    public IPlayerBuilder WithHardwareFrames(bool yieldHardwareFrames = true)
    {
        _yieldHardwareFrames = yieldHardwareFrames;
        return this;
    }

    public IPlayerBuilder WithAudioActivation(bool activateAudioSink = true)
    {
        _activateAudioSink = activateAudioSink;
        return this;
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

    public async Task<IMediaPlaylistPlayer> BuildPlayerAsync(
        CancellationToken cancellationToken = default
    )
    {
        RequireSinkForEachConfigurator();
        return await PlayerFactory.CreateAsync(
            initial: _sources,
            videoSink: _videoSink,
            audioSink: _audioSink,
            hardwareDecodeMode: _hwMode,
            yieldHardwareFrames: _yieldHardwareFrames,
            initialRepeatMode: _repeatMode,
            loggerFactory: _loggerFactory,
            activateAudioSink: _activateAudioSink,
            configureVideo: _videoConfigurator,
            configureAudio: _audioConfigurator,
            clock: _clock,
            latenessRecovery: _latenessRecovery,
            timeProvider: _timeProvider,
            cancellationToken: cancellationToken
        );
    }
}
