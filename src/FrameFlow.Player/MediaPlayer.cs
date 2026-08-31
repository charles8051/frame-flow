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
/// <b>This is not a fluent builder.</b> For a chain with
/// <c>WithVideoSink</c> / <c>WithAudioSink</c> / <c>ConfigureVideo</c>,
/// use <see cref="FrameFlowPlayer.Open(string)"/> — but note it returns a
/// <see cref="PlayerSession"/>, which plays to end of stream and has no
/// seek, pause, or repeat. The two entry points trade fluency against
/// control surface; pick by which you need, not by preference. Consumers
/// wanting both build their sinks directly and call this factory.
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
    /// analog of the old
    /// <see cref="FrameFlow.Player.IPlayerBuilder.ConfigureVideoPipeline"/>
    /// hook, retyped for <see cref="GraphChain{VideoFrameRef}"/>.
    /// </param>
    /// <param name="configureAudio">
    /// Optional audio-chain configurator. Same shape as
    /// <paramref name="configureVideo"/>; the analog of
    /// <see cref="FrameFlow.Player.IPlayerBuilder.ConfigureAudioPipeline"/>.
    /// </param>
    public static async Task<IMediaPlayer> CreateAsync(
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
        loggerFactory ??= NullLoggerFactory.Instance;

        // Bootstrap the FFmpeg native runtime. Idempotent — the
        // FrameFlowBootstrapper caches its result so repeated calls
        // across multiple CreateAsync invocations are cheap. We do it
        // here so consumers don't have to remember to call it
        // separately; the old `FrameFlowPlayer.BuildAsync` path also
        // ran the bootstrap via its DI registration. Skip the HW
        // probe when the caller explicitly disabled HW decoding —
        // matches the fluent builder's behaviour.
        var nativeOptions = new FrameFlowNativeOptions
        {
            SkipHardwareProbe = hardwareDecodeMode == HardwareDecodeMode.Disabled,
        };
        var bootstrap = new FrameFlowBootstrapper(nativeOptions, loggerFactory).Initialize();
        if (!bootstrap.IsSuccess)
        {
            throw new InvalidOperationException(
                $"FFmpeg bootstrap failed: {bootstrap.Message}"
            );
        }

#pragma warning disable CA2000 // controller ownership transfers to the MediaPlayer instance returned below; disposed via Dispose
        var controller = PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: hardwareDecodeMode,
            hardwareDecodeCapabilities: bootstrap.Capabilities,
            yieldHardwareFrames: yieldHardwareFrames,
            initialRepeatMode: initialRepeatMode,
            clock: null,
            loggerFactory: loggerFactory,
            configureVideo: configureVideo,
            configureAudio: configureAudio
        );
#pragma warning restore CA2000

        try
        {
            // Activate audio sink before LoadAsync so it's ready when
            // the first PlayAsync starts feeding samples. The old
            // PlayerBuilder did the same via DI activation hooks.
            if (activateAudioSink && audioSink is not null)
            {
                await audioSink.ActivateAsync(cancellationToken).ConfigureAwait(false);
            }

            var load = await controller.LoadAsync(source, cancellationToken).ConfigureAwait(false);
            if (!load.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"LoadAsync failed: {load.Error?.Category} — {load.Error?.Message}",
                    load.Error?.Inner
                );
            }

            // Construct the internal MediaPlayer wrapper via the
            // InternalsVisibleTo grant in FrameFlow.Player. No owned
            // service provider — the substrate doesn't use a DI
            // container.
            var logger = loggerFactory.CreateLogger<MediaPlayerCore>();
            return new MediaPlayerCore(controller, audioSink, ownedProvider: null, logger);
        }
        catch
        {
            try
            {
                await controller.DisposeAsync().ConfigureAwait(false);
            }
            catch { /* swallow during failure cleanup */ }
            throw;
        }
    }
}
