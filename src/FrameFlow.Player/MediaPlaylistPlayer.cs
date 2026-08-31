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
/// Factory for an <see cref="IMediaPlaylistPlayer"/>: a player that presents an
/// ordered, optionally looping sequence of sources through ONE warm video sink
/// and ONE warm audio sink, swapping only the per-item decode source at each
/// boundary so the presenter (sink + GPU resources) is never rebuilt.
/// </summary>
/// <remarks>
/// The shape mirrors <see cref="MediaPlayer.CreateAsync"/> — the same sinks,
/// hardware-decode policy, and pipeline configurators — but takes an ordered set
/// of sources instead of one, and the returned player exposes the playlist
/// transport (<see cref="IMediaPlaylistPlayer.EnqueueAsync"/>,
/// <see cref="IMediaPlaylistPlayer.SkipToNextAsync"/>,
/// <see cref="IMediaPlaylistPlayer.SourceTransitioned"/>). The sinks are attached
/// once and stay warm for the life of the playlist.
/// </remarks>
public static class MediaPlaylistPlayer
{
    /// <summary>
    /// Builds an <see cref="IMediaPlaylistPlayer"/> seeded with
    /// <paramref name="sources"/> and begins by loading the first item. The
    /// supplied sinks are attached once and reused for every item.
    /// </summary>
    /// <param name="sources">
    /// The initial play queue, in order. Must contain at least one source. More
    /// can be added later via <see cref="IMediaPlaylistPlayer.EnqueueAsync"/>.
    /// </param>
    /// <param name="videoSink">Optional video sink, kept warm across all items.</param>
    /// <param name="audioSink">
    /// Optional audio sink, kept warm across all items. Doubles as the master
    /// clock for items that carry audio (per-item selection happens inside the
    /// session); silent items fall back to the wallclock pacer.
    /// </param>
    /// <param name="hardwareDecodeMode">Hardware-decode policy (ADR-0033).</param>
    /// <param name="yieldHardwareFrames">
    /// When <see langword="true"/>, decoded frames are delivered GPU-resident for
    /// zero-copy presentation.
    /// </param>
    /// <param name="initialRepeatMode">
    /// Starting loop policy. <see cref="RepeatMode.All"/> (the default) loops the
    /// whole queue; <see cref="RepeatMode.Off"/> ends after the last item;
    /// <see cref="RepeatMode.One"/> loops the current item.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="activateAudioSink">
    /// When <see langword="true"/> (the default), activates the audio sink before
    /// the first item loads — matching <see cref="MediaPlayer.CreateAsync"/>.
    /// </param>
    /// <param name="configureVideo">Optional per-item video-chain configurator (see <see cref="MediaPlayer.CreateAsync"/>).</param>
    /// <param name="configureAudio">Optional per-item audio-chain configurator.</param>
    /// <param name="cancellationToken">Cancels the initial load.</param>
    public static async Task<IMediaPlaylistPlayer> CreateAsync(
        IEnumerable<IMediaSource> sources,
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Auto,
        bool yieldHardwareFrames = false,
        RepeatMode initialRepeatMode = RepeatMode.All,
        ILoggerFactory? loggerFactory = null,
        bool activateAudioSink = true,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? configureVideo = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? configureAudio = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        loggerFactory ??= NullLoggerFactory.Instance;

        var initial = sources.ToList();
        if (initial.Count == 0)
            throw new ArgumentException(
                "A playlist requires at least one source.",
                nameof(sources)
            );

        // Bootstrap the FFmpeg native runtime (idempotent), matching
        // MediaPlayer.CreateAsync.
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
            clock: null,
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
            // session pops it from the coordinator, so the two stay in lockstep.
            var load = await controller
                .LoadAsync(initial[0], cancellationToken)
                .ConfigureAwait(false);
            if (!load.IsSuccess)
                throw new InvalidOperationException(
                    $"LoadAsync failed: {load.Error?.Category} — {load.Error?.Message}",
                    load.Error?.Inner
                );

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
