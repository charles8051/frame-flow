// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FrameFlow.Playback;

/// <summary>
/// Entry point for the playback controller. Returns an
/// <see cref="IPlaybackController"/> (the same contract as the
/// existing <see cref="FrameFlow.Playback.IPlaybackController"/>) but
/// wired to <see cref="SubstrateSession"/> internally so the dataflow
/// runs on the substrate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a facade and not a parallel state machine.</b> The existing
/// <c>PlaybackController</c> is substrate-agnostic in its core: the
/// three Stateless state machines, the channel-serialized command
/// dispatch loop, the observable subjects, the position-ticker worker —
/// none of it touches Crossbar directly. The substrate-tied piece is
/// the <c>IPlaybackSession</c> the controller delegates dataflow to.
/// So the port is just "swap the session factory."
/// </para>
/// <para>
/// The <c>IPlaybackController</c> and supporting types are
/// <c>internal</c> in <c>FrameFlow.Playback</c>; this facade reaches
/// them via the <c>InternalsVisibleTo</c> added in Crossbar ADR-0014
/// Phase 3. Phase 4 has since completed: the old assembly is gone and
/// the supporting types live here.
/// </para>
/// <para>
/// <b>What's deferred.</b>
/// <list type="bullet">
/// <item>The video / audio pipeline configurators are wired into
///   <c>MediaPlayer.CreateAsync</c> directly (the
///   substrate-side seam, not the controller-side one). Old
///   <c>IPlaybackController</c> doesn't see them; that's fine
///   because nothing reads them from the controller surface.</item>
/// </list>
/// Everything else — Load/Play/Pause/Resume/Seek/Stop/Repeat,
/// observables, diagnostics, error propagation — is unchanged from the
/// controller's original shape. Only the session underneath it moved to
/// the substrate.
/// </para>
/// </remarks>
public static class PlaybackController
{
    /// <summary>
    /// Creates a new <see cref="IPlaybackController"/> backed by the
    /// substrate. The returned controller has the same public
    /// surface as the existing
    /// <see cref="FrameFlow.Playback.IPlaybackController"/>; consumers
    /// that depend on that interface can use this controller as a
    /// drop-in replacement (subject to the deferred items in the
    /// type-level remarks).
    /// </summary>
    /// <param name="videoSink">
    /// Video sink the controller will drive when video is present in
    /// the loaded source. <see langword="null"/> for audio-only
    /// playback or when consumers only need the controller's state
    /// machine + observables surface.
    /// </param>
    /// <param name="audioSink">
    /// Audio sink the controller will drive when audio is present.
    /// Doubles as the master clock when it implements
    /// <see cref="IClockSource"/> (per the existing master-clock
    /// selection in <see cref="FrameFlow.Playback.PlaybackSession"/>).
    /// </param>
    /// <param name="hardwareDecodeMode">
    /// Hardware-decode policy for the video decoder (ADR-0033). The
    /// session falls back to software when no hwaccel backend binds.
    /// </param>
    /// <param name="initialRepeatMode">
    /// Starting repeat mode. Can be changed at runtime via
    /// <see cref="FrameFlow.Playback.IPlaybackController.SetRepeatModeAsync"/>.
    /// </param>
    /// <param name="clock">
    /// Optional clock to inject (e.g. a test clock with a fake
    /// <see cref="ITimeSource"/>). Defaults to a fresh
    /// <see cref="PlaybackClock"/> with system time.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory. Defaults to
    /// <see cref="NullLoggerFactory.Instance"/>.
    /// </param>
    /// <param name="configureVideo">
    /// Optional video-chain configurator that runs between the
    /// decoder source and the pace+gate+sink terminal. Consumers
    /// insert resize / convert / inference-tap operators here.
    /// </param>
    /// <param name="configureAudio">
    /// Optional audio-chain configurator. Same shape as the video
    /// hook; runs between the decoder source and the gate+sink
    /// terminal.
    /// </param>
    public static IPlaybackController Create(
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Auto,
        FrameFlow.Media.HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        bool yieldHardwareFrames = false,
        RepeatMode initialRepeatMode = RepeatMode.Off,
        IPlaybackClock? clock = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? configureVideo = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? configureAudio = null
    )
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        clock ??= new PlaybackClock();

        var sessionFactory = new SubstrateSessionFactory(
            videoSink,
            audioSink,
            hardwareDecodeMode,
            hardwareDecodeCapabilities,
            loggerFactory,
            configureVideo,
            configureAudio,
            yieldHardwareFrames
        );

        var options = Options.Create(
            new FrameFlowPlaybackOptions { InitialRepeatMode = initialRepeatMode }
        );

        return new PlaybackControllerCore(
            loggerFactory.CreateLogger<PlaybackControllerCore>(),
            sessionFactory,
            clock,
            options
        );
    }

    /// <summary>
    /// Creates an <see cref="IPlaybackController"/> backed by a
    /// <see cref="PlaylistSession"/>: the controller drives one session that
    /// presents the ordered, optionally looping sequence carried by
    /// <paramref name="coordinator"/> through the supplied warm sinks, swapping
    /// only the per-item decode source at each boundary so the presenter (sink +
    /// GPU resources) is never rebuilt.
    /// </summary>
    /// <remarks>
    /// Internal because it takes the internal <see cref="PlaylistCoordinator"/>;
    /// the public entry point is <c>FrameFlow.Player.MediaPlaylistPlayer.CreateAsync</c>,
    /// which constructs the coordinator and projects the controller to the
    /// playlist player surface.
    /// </remarks>
    /// <param name="coordinator">
    /// The shared playlist queue + loop policy + transition stream. Its
    /// <see cref="PlaylistCoordinator.RepeatMode"/> should match
    /// <paramref name="initialRepeatMode"/>.
    /// </param>
    internal static IPlaybackController CreatePlaylist(
        PlaylistCoordinator coordinator,
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Auto,
        FrameFlow.Media.HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        bool yieldHardwareFrames = false,
        RepeatMode initialRepeatMode = RepeatMode.All,
        IPlaybackClock? clock = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? configureVideo = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? configureAudio = null
    )
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        loggerFactory ??= NullLoggerFactory.Instance;
        clock ??= new PlaybackClock();

        var sessionFactory = new PlaylistSessionFactory(
            coordinator,
            videoSink,
            audioSink,
            hardwareDecodeMode,
            hardwareDecodeCapabilities,
            loggerFactory,
            configureVideo,
            configureAudio,
            yieldHardwareFrames
        );

        var options = Options.Create(
            new FrameFlowPlaybackOptions { InitialRepeatMode = initialRepeatMode }
        );

        return new PlaybackControllerCore(
            loggerFactory.CreateLogger<PlaybackControllerCore>(),
            sessionFactory,
            clock,
            options
        );
    }
}
