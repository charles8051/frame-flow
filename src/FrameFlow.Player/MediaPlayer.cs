// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FrameFlow.Graph;

namespace FrameFlow.Player;

/// <summary>
/// Factory for an <see cref="IMediaPlayer"/> backed by
/// <see cref="PlaybackController"/>. The returned instance is the internal
/// <c>MediaPlayerCore</c> wrapper, which projects the controller's full
/// state machine down to the smaller surface <c>FrameFlowPlayerView</c> and
/// other UI callers consume.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="PlaybackController"/> returns
/// <see cref="FrameFlow.Playback.IPlaybackController"/>, the full state
/// machine. UI controls like <c>FrameFlowPlayerView</c> consume the
/// simpler <see cref="IMediaPlayer"/> projection instead. This factory
/// builds sinks into a controller and wraps it in that projection, which
/// is the shape most consumers want.
/// </para>
/// <para>
/// <b>Prefer the fluent builder.</b>
/// <c>FrameFlowPlayer.Open(path)…BuildPlayerAsync()</c> runs the same
/// wiring and returns the same <see cref="IMediaPlayer"/>. This factory
/// remains for existing callers. New code should use the builder.
/// </para>
/// </remarks>
public static class MediaPlayer
{
    /// <summary>
    /// Builds an <see cref="IMediaPlayer"/> backed by
    /// <see cref="PlaybackController"/>, with the given sinks wired into the
    /// playback graph. Bootstraps the FFmpeg native runtime if it is not
    /// already up, so callers do not have to.
    /// </summary>
    /// <param name="source">Media source to load.</param>
    /// <param name="videoSink">Optional video sink.</param>
    /// <param name="audioSink">Optional audio sink. Doubles as
    /// master clock when it implements <see cref="IClockSource"/>.</param>
    /// <param name="hardwareDecodeMode">Hardware-decode policy.</param>
    /// <param name="initialRepeatMode">Starting repeat mode.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="activateAudioSink">
    /// When <see langword="true"/> (the default), calls
    /// <see cref="IAudioSink.ActivateAsync"/> before handing the
    /// session to the caller, so audio is audible without a second step.
    /// Set to <see langword="false"/> to activate the sink yourself later.
    /// </param>
    /// <param name="configureVideo">
    /// Optional video-chain configurator that runs between the
    /// decoder source and the pace+gate+sink terminal. Consumers
    /// insert resize / convert / inference-tap operators here. The
    /// fluent builder's equivalent is
    /// <see cref="IPlayerBuilder.ConfigureVideo"/>.
    /// </param>
    /// <param name="configureAudio">
    /// Optional audio-chain configurator. Same shape as
    /// <paramref name="configureVideo"/>; the fluent builder's
    /// equivalent is <see cref="IPlayerBuilder.ConfigureAudio"/>.
    /// </param>
    /// <param name="yieldHardwareFrames">
    /// When <see langword="true"/>, hardware-decoded frames reach the
    /// video sink as GPU frames instead of being downloaded to system
    /// memory first. Only useful with a sink that can consume them; a
    /// CPU-only sink should leave this <see langword="false"/>.
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
    public static Task<IMediaPlayer> CreateAsync(
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
    ) =>
        CreateCoreAsync(
            source: source,
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: hardwareDecodeMode,
            yieldHardwareFrames: yieldHardwareFrames,
            initialRepeatMode: initialRepeatMode,
            loggerFactory: loggerFactory,
            activateAudioSink: activateAudioSink,
            configureVideo: configureVideo,
            configureAudio: configureAudio,
            clock: null,
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// The body of <see cref="CreateAsync"/>, plus the
    /// <paramref name="clock"/> that <c>PlaybackController.Create</c>
    /// accepts and the public factory does not expose.
    /// </summary>
    /// <remarks>
    /// Internal so the clock stays off the positional surface.
    /// <c>CreateAsync</c> is a published signature; a twelfth optional
    /// parameter on it would break existing positional calls at source
    /// and existing compiled callers at load. The fluent builder's
    /// <see cref="IMediaPlayerBuilder.WithClock"/> reaches this instead.
    /// </remarks>
    internal static async Task<IMediaPlayer> CreateCoreAsync(
        IMediaSource source,
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
        ArgumentNullException.ThrowIfNull(source);

        // A single source is a queue of one, built on the playlist player's path.
        return await MediaPlaylistPlayer
            .CreateCoreAsync(
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
                clock,
                cancellationToken
            )
            .ConfigureAwait(false);
    }
}
