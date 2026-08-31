// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Playback;

/// <summary>
/// Factory that creates <see cref="PlaylistSession"/> instances bound to a shared
/// <see cref="PlaylistCoordinator"/> and the configured warm sinks. The shape
/// mirrors <see cref="SubstrateSessionFactory"/> (it captures the same long-lived
/// sinks + options + configurators and produces a session per controller load);
/// the only addition is the coordinator, which carries the playlist queue and
/// loop policy and is shared with the player surface.
/// </summary>
internal sealed class PlaylistSessionFactory : IPlaybackSessionFactory
{
    private readonly PlaylistCoordinator _coordinator;
    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly HardwareDecodeMode _hwMode;
    private readonly HardwareDecodeCapabilities? _hwCapabilities;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? _videoConfigurator;
    private readonly Func<
        GraphChain<PcmAudioBufferRef>,
        GraphChain<PcmAudioBufferRef>
    >? _audioConfigurator;
    private readonly bool _yieldHardwareFrames;

    public PlaylistSessionFactory(
        PlaylistCoordinator coordinator,
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hwMode = HardwareDecodeMode.Auto,
        HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? videoConfigurator = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? audioConfigurator = null,
        bool yieldHardwareFrames = false
    )
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _videoSink = videoSink;
        _audioSink = audioSink;
        _hwMode = hwMode;
        _hwCapabilities = hardwareDecodeCapabilities;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _videoConfigurator = videoConfigurator;
        _audioConfigurator = audioConfigurator;
        _yieldHardwareFrames = yieldHardwareFrames;
    }

    public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new PlaylistSession(
            _coordinator,
            _videoSink,
            _audioSink,
            clock,
            callbacks,
            _hwMode,
            _hwCapabilities,
            _loggerFactory,
            _videoConfigurator,
            _audioConfigurator,
            _yieldHardwareFrames
        );
    }
}
