// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player;

/// <summary>
/// Factory for a player, over one source or over many. Both overloads of <c>CreateAsync</c> build
/// the same object and return <see cref="IMediaPlaylistPlayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="PlaybackController"/> returns
/// <see cref="FrameFlow.Playback.IPlaybackController"/>, the full state machine. UI controls like
/// <c>FrameFlowPlayerView</c> consume the simpler <see cref="IMediaPlayer"/> projection instead.
/// This factory builds sinks into a controller and wraps it in that projection, which is the shape
/// most consumers want.
/// </para>
/// <para>
/// <b>One source is a queue of one</b> (ADR-0077). A caller who plays one file can name
/// <see cref="IMediaPlayer"/> and ignore the queue; one who later wants a second source enqueues it
/// on the player they already hold. The sinks are attached once and stay warm across every item, so
/// nothing is rebuilt at a boundary. A caller with no source yet passes none, and the player is
/// built with an empty queue.
/// </para>
/// <para>
/// <b>Prefer the fluent builder.</b> <c>FrameFlowPlayer.Create().WithMedia(path)…BuildPlayerAsync()</c> runs the
/// same wiring and returns the same player. This factory remains for existing callers. New code
/// should use the builder.
/// </para>
/// </remarks>
public static class MediaPlayer
{
    /// <summary>
    /// Builds a player over one source, as a queue of one. Bootstraps the FFmpeg native runtime if
    /// it is not already up, so callers do not have to.
    /// </summary>
    /// <param name="source">Media source to load.</param>
    /// <param name="videoSink">Optional video sink.</param>
    /// <param name="audioSink">Optional audio sink. Doubles as
    /// master clock when it implements <see cref="IClockSource"/>.</param>
    /// <param name="hardwareDecodeMode">Hardware-decode policy (ADR-0033).</param>
    /// <param name="yieldHardwareFrames">
    /// When <see langword="true"/>, hardware-decoded frames reach the
    /// video sink as GPU frames instead of being downloaded to system
    /// memory first. Only useful with a sink that can consume them; a
    /// CPU-only sink should leave this <see langword="false"/>.
    /// </param>
    /// <param name="initialRepeatMode">
    /// Starting loop policy. <see cref="RepeatMode.Off"/> (the default) ends after the last item;
    /// <see cref="RepeatMode.All"/> loops the whole queue; <see cref="RepeatMode.One"/> loops the
    /// current item. A queue of one repeats its one item, so <c>All</c> and <c>One</c> behave alike
    /// there.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="activateAudioSink">
    /// When <see langword="true"/> (the default), calls
    /// <see cref="IAudioSink.ActivateAsync"/> before handing the
    /// session to the caller, so audio is audible without a second step.
    /// Set to <see langword="false"/> to activate the sink yourself later.
    /// </param>
    /// <param name="configureVideo">
    /// Optional per-item video-chain configurator that runs between the
    /// decoder source and the pace+gate+sink terminal. Consumers
    /// insert resize / convert / inference-tap operators here. The
    /// fluent builder's equivalent is
    /// <see cref="IPlayerBuilder.ConfigureVideo"/>.
    /// </param>
    /// <param name="configureAudio">
    /// Optional per-item audio-chain configurator. Same shape as
    /// <paramref name="configureVideo"/>; the fluent builder's
    /// equivalent is <see cref="IPlayerBuilder.ConfigureAudio"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the load. The returned player is not created if this
    /// fires before <c>LoadAsync</c> completes.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The FFmpeg bootstrap failed, or the source could not be loaded.
    /// Construction throws rather than returning a <see cref="Result"/>: a
    /// factory that fails has no player to hand back (ADR-0069). The
    /// player's own transport commands do return <see cref="Result"/>.
    /// </exception>
    public static async Task<IMediaPlaylistPlayer> CreateAsync(
        IMediaSource source,
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Auto,
        bool yieldHardwareFrames = false,
        RepeatMode initialRepeatMode = RepeatMode.Off,
        ILoggerFactory? loggerFactory = null,
        bool activateAudioSink = true,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? configureVideo = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? configureAudio = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        return await CreateCoreAsync(
                [source],
                videoSink,
                audioSink,
                hardwareDecodeMode,
                yieldHardwareFrames,
                initialRepeatMode,
                loggerFactory,
                activateAudioSink,
                configureVideo,
                configureAudio,
                clock: null,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a player over an ordered set of sources, and begins by loading the first. The
    /// supplied sinks are attached once and reused for every item.
    /// </summary>
    /// <param name="sources">
    /// The initial queue, in order. More can be added later with
    /// <see cref="IMediaPlaylistPlayer.AddAsync"/>, or played once with
    /// <see cref="IMediaPlaylistPlayer.EnqueueAsync"/>. An empty set builds the player with its
    /// sinks warm and nothing loaded; the first <see cref="IMediaPlayer.PlayAsync"/> starts
    /// whatever the queue holds by then, and is refused while it is still empty.
    /// </param>
    /// <param name="videoSink">Optional video sink, kept warm across all items.</param>
    /// <param name="audioSink">
    /// Optional audio sink, kept warm across all items. Doubles as the master clock for items that
    /// carry audio; silent items fall back to the wallclock pacer.
    /// </param>
    /// <param name="hardwareDecodeMode">Hardware-decode policy (ADR-0033).</param>
    /// <param name="yieldHardwareFrames">
    /// When <see langword="true"/>, decoded frames are delivered GPU-resident for zero-copy
    /// presentation.
    /// </param>
    /// <param name="initialRepeatMode">
    /// Starting loop policy. <see cref="RepeatMode.Off"/> (the default) ends after the last item;
    /// <see cref="RepeatMode.All"/> loops the whole queue; <see cref="RepeatMode.One"/> loops the
    /// current item.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="activateAudioSink">
    /// When <see langword="true"/> (the default), activates the audio sink before the first item
    /// loads.
    /// </param>
    /// <param name="configureVideo">Optional per-item video-chain configurator.</param>
    /// <param name="configureAudio">Optional per-item audio-chain configurator.</param>
    /// <param name="cancellationToken">Cancels the initial load.</param>
    /// <exception cref="InvalidOperationException">
    /// The FFmpeg bootstrap failed, or the first source could not be loaded.
    /// </exception>
    public static async Task<IMediaPlaylistPlayer> CreateAsync(
        IEnumerable<IMediaSource> sources,
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Auto,
        bool yieldHardwareFrames = false,
        RepeatMode initialRepeatMode = RepeatMode.Off,
        ILoggerFactory? loggerFactory = null,
        bool activateAudioSink = true,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? configureVideo = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? configureAudio = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(sources);

        return await CreateCoreAsync(
                sources.ToList(),
                videoSink,
                audioSink,
                hardwareDecodeMode,
                yieldHardwareFrames,
                initialRepeatMode,
                loggerFactory,
                activateAudioSink,
                configureVideo,
                configureAudio,
                clock: null,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The construction path both overloads share, plus the <paramref name="clock"/> that
    /// <c>PlaybackController.CreatePlaylist</c> accepts and the public factory does not expose.
    /// </summary>
    /// <remarks>
    /// Internal so the clock stays off the positional surface. <c>CreateAsync</c> is a published
    /// signature; a twelfth optional parameter on it would break existing positional calls at
    /// source and existing compiled callers at load. The fluent builder's
    /// <see cref="IPlayerBuilder.WithClock"/> reaches this instead.
    /// </remarks>
    internal static async Task<PlaylistMediaPlayerCore> CreateCoreAsync(
        IReadOnlyList<IMediaSource> initial,
        IVideoSink? videoSink,
        IAudioSink? audioSink,
        HardwareDecodeMode hardwareDecodeMode,
        bool yieldHardwareFrames,
        RepeatMode initialRepeatMode,
        ILoggerFactory? loggerFactory,
        bool activateAudioSink,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? configureVideo,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? configureAudio,
        IPlaybackClock? clock,
        CancellationToken cancellationToken
    )
    {
        loggerFactory ??= NullLoggerFactory.Instance;

        // Bootstrap the FFmpeg native runtime (idempotent). Skip the hardware probe when the
        // caller disabled hardware decoding, matching the fluent builder.
        var nativeOptions = new FrameFlowNativeOptions
        {
            SkipHardwareProbe = hardwareDecodeMode == HardwareDecodeMode.Disabled,
        };
        var bootstrap = new FrameFlowBootstrapper(nativeOptions, loggerFactory).Initialize();
        if (!bootstrap.IsSuccess)
            throw new InvalidOperationException($"FFmpeg bootstrap failed: {bootstrap.Message}");

        var coordinator = new PlaylistCoordinator(initial, initialRepeatMode);

#pragma warning disable CA2000 // controller ownership transfers to the player returned below; disposed via Dispose
        var controller = PlaybackController.CreatePlaylist(
            coordinator,
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: hardwareDecodeMode,
            hardwareDecodeCapabilities: bootstrap.Capabilities,
            yieldHardwareFrames: yieldHardwareFrames,
            initialRepeatMode: initialRepeatMode,
            clock: clock,
            loggerFactory: loggerFactory,
            configureVideo: configureVideo,
            configureAudio: configureAudio
        );
#pragma warning restore CA2000

        try
        {
            if (activateAudioSink && audioSink is not null)
                await audioSink.ActivateAsync(cancellationToken).ConfigureAwait(false);

            // Loading the first item drives the controller through to Paused; the
            // session pops it from the coordinator, so the two stay in lockstep. An empty
            // queue has nothing to load, and the player stays Idle until the first PlayAsync
            // starts whatever has been added by then.
            if (initial.Count > 0)
            {
                var load = await controller
                    .LoadAsync(initial[0], cancellationToken)
                    .ConfigureAwait(false);
                if (!load.IsSuccess)
                    throw new InvalidOperationException(
                        $"LoadAsync failed: {load.Error.Category} — {load.Error.Message}",
                        load.Error.Inner
                    );
            }

            var logger = loggerFactory.CreateLogger<PlaylistMediaPlayerCore>();
            return new PlaylistMediaPlayerCore(controller, coordinator, audioSink, logger);
        }
        catch
        {
            try
            {
                await controller.DisposeAsync().ConfigureAwait(false);
            }
            catch { /* swallow during failure cleanup */ }
            coordinator.Dispose();
            throw;
        }
    }
}
