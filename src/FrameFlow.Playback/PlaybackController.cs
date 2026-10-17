// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
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
/// none of it touches the graph substrate directly. The substrate-tied piece is
/// the <c>IPlaybackSession</c> the controller delegates dataflow to.
/// So the port is just "swap the session factory."
/// </para>
/// <para>
/// The <c>IPlaybackController</c> and supporting types are
/// <c>internal</c> in <c>FrameFlow.Playback</c>; this facade reaches
/// them via the <c>InternalsVisibleTo</c> added during the substrate
/// migration, which has since completed: the old assembly is gone and
/// the supporting types live here.
/// </para>
/// <para>
/// <b>What's deferred.</b>
/// <list type="bullet">
/// <item>The video / audio pipeline configurators are wired into
///   <c>FrameFlowPlayer.Create()</c> directly (the
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
    /// <remarks>
    /// <b>The sinks are claimed (ADR-0085, decision 6).</b> Each sink that implements
    /// <see cref="IClaimableSink"/> is claimed before anything else is built, and the claim is
    /// released once the controller's <c>DisposeAsync</c> has completed. A sink another player,
    /// controller or pass holds makes this throw.
    /// </remarks>
    /// <param name="videoSink">
    /// Video sink the controller will drive when video is present in
    /// the loaded source. <see langword="null"/> for audio-only
    /// playback or when consumers only need the controller's state
    /// machine + observables surface.
    /// </param>
    /// <param name="audioSink">
    /// Audio sink the controller will drive when audio is present.
    /// Doubles as the master clock when it implements
    /// <see cref="IClockSource"/>, which is how the session picks its
    /// master clock.
    /// </param>
    /// <param name="hardwareDecodeMode">
    /// Hardware-decode policy for the video decoder (ADR-0033). The
    /// session falls back to software when no hwaccel backend binds.
    /// </param>
    /// <param name="hardwareDecodeCapabilities">
    /// Backends the host was probed to support. Pass the value from
    /// <see cref="FrameFlow.Media.FrameFlowBootstrapResult.Capabilities"/>.
    /// <see langword="null"/> uses this process's hardware decode probe, running
    /// it first if nothing has yet. The first probe can take a second or so on a
    /// multi-GPU host; later ones are free. Pass
    /// <see cref="FrameFlow.Media.HardwareDecodeCapabilities.Empty"/> to force
    /// software decode.
    /// </param>
    /// <param name="yieldHardwareFrames">
    /// When <see langword="true"/>, hardware-decoded frames reach the video
    /// sink as GPU frames instead of being downloaded to system memory first;
    /// <see langword="false"/> always downloads them. <see langword="null"/>, the
    /// default, keeps them on the GPU when every node on the video path, the sink
    /// included, says it takes GPU frames (#294).
    /// </param>
    /// <param name="initialRepeatMode">
    /// Starting repeat mode. Can be changed at runtime via
    /// <see cref="FrameFlow.Playback.IPlaybackController.SetRepeatModeAsync"/>.
    /// </param>
    /// <param name="clock">
    /// Optional clock to inject (e.g. a <see cref="PlaybackClock"/> over a fake
    /// <see cref="TimeProvider"/>). Defaults to a fresh
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
    /// <param name="timeProvider">
    /// Wall time for the loop-stall watchdog and the position ticker, and for the
    /// <see cref="PlaybackClock"/> this builds when <paramref name="clock"/> is
    /// <see langword="null"/>. Defaults to <see cref="TimeProvider.System"/>. This is elapsed real
    /// time, not the presentation timeline <paramref name="clock"/> carries: the watchdog's job is
    /// to notice that the timeline advanced while frames stopped, so it cannot read the clock it
    /// supervises.
    /// </param>
    /// <param name="latenessRecovery">
    /// Tuning for the lateness-recovery walk. <see langword="null"/> leaves
    /// the walk off, which is the default.
    /// </param>
    /// <param name="hardwareDevice">
    /// A device every item's video decoder borrows instead of creating its own, so it outlives the
    /// items. Owned by the caller, who disposes it after the controller. Null lets each decoder
    /// create its own.
    /// </param>
    /// <param name="preferredBackends">
    /// Hardware decode backends every item's decoder tries first, then the rest in the platform
    /// default order. Null or empty defers to the video sink's
    /// <see cref="IVideoSink.PreferredBackends"/> when frames stay on the GPU, then the platform
    /// default (#532). A <paramref name="hardwareDevice"/> fixes the backend instead.
    /// </param>
    /// <param name="excludedCodecs">
    /// Codecs every item's decoder decodes in software, named as
    /// <see cref="VideoStreamInfo.CodecName"/> reports them, such as <c>"av1"</c>. Null or empty
    /// excludes none. Under <see cref="HardwareDecodeMode.Required"/> an excluded codec fails to load.
    /// </param>
    /// <param name="decoderThreads">
    /// How many threads every item's video decoder uses when it decodes in software: 0 chooses from
    /// the processor count, up to 8, and 1 decodes on one thread. A hardware decoder ignores it.
    /// </param>
    /// <param name="colorMatrix">
    /// The matrix every item's video frames convert to RGB with (#841). <see cref="ColorMatrix.Auto"/>
    /// takes the one the stream signals, and for a stream that signals none BT.709 at 720 rows or
    /// more and BT.601 below.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Another player, controller or pass holds <paramref name="videoSink"/> or
    /// <paramref name="audioSink"/>. The message names the sink and its holder.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// <paramref name="videoSink"/> or <paramref name="audioSink"/> is a claimable sink that has been
    /// disposed.
    /// </exception>
    public static IPlaybackController Create(
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Auto,
        FrameFlow.Media.HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        bool? yieldHardwareFrames = null,
        RepeatMode initialRepeatMode = RepeatMode.Off,
        IPlaybackClock? clock = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configureVideo = null,
        Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? configureAudio = null,
        LatenessRecoveryOptions? latenessRecovery = null,
        TimeProvider? timeProvider = null,
        HardwareDevice? hardwareDevice = null,
        IReadOnlyList<HardwareDecodeBackendKind>? preferredBackends = null,
        IReadOnlyList<string>? excludedCodecs = null,
        int decoderThreads = 0,
        ColorMatrix colorMatrix = ColorMatrix.Auto
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decoderThreads);
        ThrowIfUndefined(colorMatrix);

        // One source at a time, as a queue of one on the playlist session. Each load makes the
        // loaded source the queue's only item.
        loggerFactory ??= NullLoggerFactory.Instance;
        var factoryLoggers = loggerFactory;
        return Assemble(
            "a controller from PlaybackController.Create",
            videoSink,
            audioSink,
            () => new PlaylistSessionFactory(
                new PlaylistCoordinator(initialRepeatMode),
                videoSink,
                audioSink,
                hardwareDecodeMode,
                hardwareDecodeCapabilities,
                factoryLoggers,
                configureVideo,
                configureAudio,
                yieldHardwareFrames,
                latenessRecovery,
                loadsSource: true,
                hardwareDevice: hardwareDevice,
                preferredBackends: preferredBackends,
                excludedCodecs: excludedCodecs,
                decoderThreads: decoderThreads,
                colorMatrix: colorMatrix
            ),
            initialRepeatMode,
            clock,
            loggerFactory,
            timeProvider
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
    /// the public entry point is <c>FrameFlow.Player.FrameFlowPlayer.Create</c>,
    /// which constructs the coordinator and projects the controller to the
    /// playlist player surface.
    /// </remarks>
    /// <param name="coordinator">
    /// The shared playlist queue + loop policy + transition stream. Its
    /// <see cref="PlaylistCoordinator.RepeatMode"/> should match
    /// <paramref name="initialRepeatMode"/>.
    /// </param>
    /// <param name="videoSink">
    /// Warm video sink, reused across every item. <see langword="null"/> for
    /// an audio-only playlist.
    /// </param>
    /// <param name="audioSink">
    /// Warm audio sink, reused across every item. Doubles as the master clock
    /// when it implements <see cref="IClockSource"/>.
    /// </param>
    /// <param name="hardwareDecodeMode">
    /// Hardware-decode policy applied to each item's video decoder.
    /// </param>
    /// <param name="hardwareDecodeCapabilities">
    /// Backends the host was probed to support, probed once and reused for
    /// every item. <see langword="null"/> uses this process's hardware decode
    /// probe, as for <see cref="Create"/>.
    /// </param>
    /// <param name="yieldHardwareFrames">
    /// When <see langword="true"/>, hardware-decoded frames reach the video
    /// sink as GPU frames rather than being downloaded first; <see langword="null"/>
    /// derives it from the video path, as for <see cref="Create"/>.
    /// </param>
    /// <param name="initialRepeatMode">
    /// Starting repeat mode. Defaults to <see cref="RepeatMode.All"/>, which
    /// is the usual playlist case.
    /// </param>
    /// <param name="clock">
    /// Optional clock to inject. Defaults to a fresh
    /// <see cref="PlaybackClock"/> with system time.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory. Defaults to
    /// <see cref="NullLoggerFactory.Instance"/>.
    /// </param>
    /// <param name="configureVideo">
    /// Optional video-chain configurator, applied to every item's chain.
    /// </param>
    /// <param name="configureAudio">
    /// Optional audio-chain configurator, applied to every item's chain.
    /// </param>
    /// <param name="timeProvider">
    /// Wall time for the loop-stall watchdog and the position ticker, and for the
    /// <see cref="PlaybackClock"/> this builds when <paramref name="clock"/> is
    /// <see langword="null"/>. Defaults to <see cref="TimeProvider.System"/>. This is elapsed real
    /// time, not the presentation timeline <paramref name="clock"/> carries: the watchdog's job is
    /// to notice that the timeline advanced while frames stopped, so it cannot read the clock it
    /// supervises.
    /// </param>
    /// <param name="latenessRecovery">
    /// Tuning for the lateness-recovery walk, applied to every item's chain.
    /// <see langword="null"/> leaves the walk off, which is the default.
    /// </param>
    /// <param name="hardwareDevice">
    /// A device every item's video decoder borrows instead of creating its own, so it outlives the
    /// items. Owned by the caller, who disposes it after the controller. Null lets each decoder
    /// create its own.
    /// </param>
    /// <param name="preferredBackends">
    /// Hardware decode backends every item's decoder tries first, then the rest in the platform
    /// default order. Null or empty defers to the video sink's
    /// <see cref="IVideoSink.PreferredBackends"/> when frames stay on the GPU, then the platform
    /// default (#532). A <paramref name="hardwareDevice"/> fixes the backend instead.
    /// </param>
    /// <param name="excludedCodecs">
    /// Codecs every item's decoder decodes in software, named as
    /// <see cref="VideoStreamInfo.CodecName"/> reports them, such as <c>"av1"</c>. Null or empty
    /// excludes none. Under <see cref="HardwareDecodeMode.Required"/> an excluded codec fails to load.
    /// </param>
    /// <param name="decoderThreads">
    /// How many threads every item's video decoder uses when it decodes in software: 0 chooses from
    /// the processor count, up to 8, and 1 decodes on one thread. A hardware decoder ignores it.
    /// </param>
    /// <param name="colorMatrix">
    /// The matrix every item's video frames convert to RGB with (#841). <see cref="ColorMatrix.Auto"/>
    /// takes the one the stream signals, and for a stream that signals none BT.709 at 720 rows or
    /// more and BT.601 below.
    /// </param>
    /// <param name="prepareNextItem">
    /// Whether the playlist prepares the item its queue would take next while the current one
    /// plays (media-clips decision 8).
    /// </param>
    /// <param name="claimant">
    /// Who claims the sinks, as the error a later build against them names it (ADR-0085,
    /// decision 6). The sinks are claimed as <see cref="Create"/> claims them, and every item the
    /// playlist plays or prepares shares the claim.
    /// </param>
    internal static IPlaybackController CreatePlaylist(
        PlaylistCoordinator coordinator,
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Auto,
        FrameFlow.Media.HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        bool? yieldHardwareFrames = null,
        RepeatMode initialRepeatMode = RepeatMode.All,
        IPlaybackClock? clock = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configureVideo = null,
        Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? configureAudio = null,
        LatenessRecoveryOptions? latenessRecovery = null,
        TimeProvider? timeProvider = null,
        HardwareDevice? hardwareDevice = null,
        IReadOnlyList<HardwareDecodeBackendKind>? preferredBackends = null,
        IReadOnlyList<string>? excludedCodecs = null,
        int decoderThreads = 0,
        ColorMatrix colorMatrix = ColorMatrix.Auto,
        bool prepareNextItem = false,
        string claimant = "a player"
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decoderThreads);
        ThrowIfUndefined(colorMatrix);
        ArgumentNullException.ThrowIfNull(coordinator);
        loggerFactory ??= NullLoggerFactory.Instance;
        var factoryLoggers = loggerFactory;
        return Assemble(
            claimant,
            videoSink,
            audioSink,
            () => new PlaylistSessionFactory(
                coordinator,
                videoSink,
                audioSink,
                hardwareDecodeMode,
                hardwareDecodeCapabilities,
                factoryLoggers,
                configureVideo,
                configureAudio,
                yieldHardwareFrames,
                latenessRecovery,
                hardwareDevice: hardwareDevice,
                preferredBackends: preferredBackends,
                excludedCodecs: excludedCodecs,
                decoderThreads: decoderThreads,
                colorMatrix: colorMatrix,
                prepareNextItem: prepareNextItem
            ),
            initialRepeatMode,
            clock,
            loggerFactory,
            timeProvider
        );
    }

    /// <summary>
    /// Claims the sinks, then builds the controller over the factory
    /// <paramref name="sessionFactory"/> makes. Both entry points call this (#44).
    /// </summary>
    /// <remarks>
    /// The claim comes before anything is built, so a build against a held sink fails before it
    /// has made anything to tear down (ADR-0085, decision 6). The controller releases it once its
    /// dispose has completed; a build that throws after the claim releases it first.
    /// </remarks>
    private static PlaybackControllerCore Assemble(
        string claimant,
        IVideoSink? videoSink,
        IAudioSink? audioSink,
        Func<IPlaybackSessionFactory> sessionFactory,
        RepeatMode initialRepeatMode,
        IPlaybackClock? clock,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider
    )
    {
        var claims = SinkClaims.Take(claimant, videoSink, audioSink);
        try
        {
            return new(
                loggerFactory.CreateLogger<PlaybackControllerCore>(),
                sessionFactory(),
                // A caller who names a provider and no clock gets both on it, rather than a
                // deterministic watchdog beside a wall-clock timeline.
                clock ?? new PlaybackClock(timeProvider ?? TimeProvider.System),
                Options.Create(new FrameFlowPlaybackOptions { InitialRepeatMode = initialRepeatMode }),
                timeProvider,
                claims
            );
        }
        catch
        {
            claims.Dispose();
            throw;
        }
    }

    private static void ThrowIfUndefined(ColorMatrix colorMatrix)
    {
        if (!Enum.IsDefined(colorMatrix))
            throw new ArgumentOutOfRangeException(nameof(colorMatrix), colorMatrix, "Undefined color matrix.");
    }
}
