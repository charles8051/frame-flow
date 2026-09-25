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
internal sealed class PlaylistSessionFactory : IPlaybackSessionFactory, IDisposable
{
    private readonly PlaylistCoordinator _coordinator;
    private readonly IPlaylistItemRuntimeFactory _itemFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly bool _loadsSource;

    /// <param name="coordinator">The queue every session plays.</param>
    /// <param name="videoSink">Warm video sink, reused across every item.</param>
    /// <param name="audioSink">Warm audio sink, reused across every item.</param>
    /// <param name="hwMode">Hardware-decode policy applied to each item's video decoder.</param>
    /// <param name="hardwareDecodeCapabilities">Backends the host was probed to support.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="videoConfigurator">Optional video-chain configurator, applied to every item.</param>
    /// <param name="audioConfigurator">Optional audio-chain configurator, applied to every item.</param>
    /// <param name="yieldHardwareFrames">Whether hardware-decoded frames reach the sink as GPU frames.</param>
    /// <param name="latenessRecovery">Tuning for the lateness-recovery walk, applied to every item.</param>
    /// <param name="loadsSource">
    /// Whether each session makes the source the controller loads the queue's only item. A
    /// controller that plays one source at a time sets it; a playlist player seeds the queue itself.
    /// It also says who owns <paramref name="coordinator"/>: a coordinator built for the controller
    /// is disposed with this factory, and one a player handed in is the player's.
    /// </param>
    public PlaylistSessionFactory(
        PlaylistCoordinator coordinator,
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hwMode = HardwareDecodeMode.Auto,
        HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? videoConfigurator = null,
        Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? audioConfigurator = null,
        bool yieldHardwareFrames = false,
        LatenessRecoveryOptions? latenessRecovery = null,
        bool loadsSource = false
    )
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _loadsSource = loadsSource;

        // Every item runs on the same warm sinks, with the same options and configurators.
        _itemFactory = new SubstrateSessionFactory(
            videoSink,
            audioSink,
            hwMode,
            hardwareDecodeCapabilities,
            _loggerFactory,
            videoConfigurator,
            audioConfigurator,
            yieldHardwareFrames,
            latenessRecovery
        );
    }

    /// <summary>The queue every session this factory creates plays.</summary>
    internal PlaylistCoordinator Coordinator => _coordinator;

    /// <summary>
    /// The lateness-recovery tuning the item factory carries, for the tests that check a caller's
    /// options reached it rather than being dropped on the way (#319). Null for an item factory
    /// that is not a <see cref="SubstrateSessionFactory"/>, which no production path builds.
    /// </summary>
    internal LatenessRecoveryOptions? LatenessRecovery =>
        (_itemFactory as SubstrateSessionFactory)?.LatenessRecovery;

    public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new PlaylistSession(
            _coordinator,
            clock,
            callbacks,
            _itemFactory,
            _loggerFactory,
            loadsSource: _loadsSource
        );
    }

    public void RepeatModeChanged(RepeatMode mode) => _coordinator.RepeatMode = mode;

    /// <summary>
    /// Reserves the queue's start item for a controller that has nothing loaded. A factory whose
    /// sessions load the source they are given has no queue to start from until that load, so it
    /// answers <see langword="null"/>.
    /// </summary>
    public IMediaSource? ReserveStart() =>
        _loadsSource ? null : _coordinator.ReserveStart()?.Source;

    /// <inheritdoc />
    public void ReleaseStart()
    {
        if (!_loadsSource)
            _coordinator.ReleaseStart();
    }

    /// <summary>
    /// Disposes the coordinator this factory was built with, when it was built for the controller.
    /// A playlist player's coordinator outlives its controller and is disposed by the player.
    /// </summary>
    public void Dispose()
    {
        if (_loadsSource)
            _coordinator.Dispose();
    }
}
