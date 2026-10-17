// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Player;

/// <summary>
/// Builds a <see cref="PlaylistMediaPlayerCore"/>: bootstraps FFmpeg, stands up the coordinator
/// and the controller, activates the audio sink and loads the first item.
/// </summary>
/// <remarks>
/// Internal, and reached only through <see cref="IPlayerBuilder.BuildPlayerAsync"/>. This was
/// <c>MediaPlayer.CreateCoreAsync</c>, the shared body under a public positional factory, until
/// that factory was retired for being a strict subset of the builder.
/// </remarks>
internal static class PlayerFactory
{
    internal static async Task<PlaylistMediaPlayerCore> CreateAsync(
        IReadOnlyList<MediaClip> initial,
        IVideoSink? videoSink,
        IAudioSink? audioSink,
        HardwareDecodeMode hardwareDecodeMode,
        bool? yieldHardwareFrames,
        RepeatMode initialRepeatMode,
        ILoggerFactory? loggerFactory,
        bool activateAudioSink,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? configureVideo,
        Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? configureAudio,
        IPlaybackClock? clock,
        LatenessRecoveryOptions? latenessRecovery,
        TimeProvider? timeProvider,
        HardwareDevice? hardwareDevice,
        IReadOnlyList<HardwareDecodeBackendKind> preferredBackends,
        IReadOnlyList<string> excludedCodecs,
        int decoderThreads,
        ColorMatrix colorMatrix,
        bool prepareNextItem,
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

        var coordinator = PlaylistCoordinator.Of(initial, initialRepeatMode);

        // The controller claims the sinks before it builds anything (ADR-0085, decision 6). A sink
        // another player or pass holds fails that claim before the controller exists, so only the
        // coordinator is left to dispose.
        IPlaybackController controller;
        try
        {
#pragma warning disable CA2000 // controller ownership transfers to the player returned below; disposed via Dispose
            controller = PlaybackController.CreatePlaylist(
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
                configureAudio: configureAudio,
                latenessRecovery: latenessRecovery,
                timeProvider: timeProvider,
                hardwareDevice: hardwareDevice,
                preferredBackends: preferredBackends,
                excludedCodecs: excludedCodecs,
                decoderThreads: decoderThreads,
                colorMatrix: colorMatrix,
                prepareNextItem: prepareNextItem,
                claimant: initial.Count > 0
                    ? $"a player built for '{initial[0].Source.DisplayName}'"
                    : "a player built with an empty queue"
            );
#pragma warning restore CA2000
        }
        catch
        {
            coordinator.Dispose();
            throw;
        }

        // From here on a failure or a cancellation disposes the controller, which releases the
        // claims on the sinks before the exception leaves.
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
                    .LoadAsync(initial[0].Source, cancellationToken)
                    .ConfigureAwait(false);
                if (!load.IsSuccess)
                    throw new InvalidOperationException(
                        $"LoadAsync failed: {load.Error.Category} — {load.Error.Message}",
                        load.Error.Inner
                    );
            }

            return new PlaylistMediaPlayerCore(controller, coordinator, audioSink);
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
