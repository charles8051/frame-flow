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
    private readonly IPlaylistItemRuntimeFactory _itemFactory;
    private readonly ILoggerFactory _loggerFactory;

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
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

        // Every item runs on the same warm sinks, with the same options and configurators.
        _itemFactory = new SubstrateSessionFactory(
            videoSink,
            audioSink,
            hwMode,
            hardwareDecodeCapabilities,
            _loggerFactory,
            videoConfigurator,
            audioConfigurator,
            yieldHardwareFrames
        );
    }

    public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new PlaylistSession(_coordinator, clock, callbacks, _itemFactory, _loggerFactory);
    }
}
