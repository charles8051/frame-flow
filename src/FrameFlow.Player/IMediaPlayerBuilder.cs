// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;
using FrameFlow.Graph;

namespace FrameFlow.Player;

/// <summary>
/// Fluent builder for an <see cref="IMediaPlayer"/> — the full
/// pause/resume/seek/repeat state machine. Reached from
/// <see cref="IPlayerBuilder"/> by calling any player-only option
/// (<see cref="IPlayerBuilder.WithRepeatMode"/>,
/// <see cref="IPlayerBuilder.WithClock"/>,
/// <see cref="IPlayerBuilder.WithHardwareFrames"/>,
/// <see cref="IPlayerBuilder.WithAudioActivation"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this interface is narrower.</b> Repeat mode, an injected
/// clock, hardware-frame yield and audio activation are properties of
/// the <see cref="IMediaPlayer"/> pipeline. They mean nothing to a
/// <see cref="PlayerSession"/>, which opens a source and plays it to
/// end of stream. Once a chain has set one of them, the only terminal
/// on offer is <see cref="BuildPlayerAsync"/>. That makes the
/// mismatch a compile error instead of a silently-ignored option.
/// </para>
/// <para>
/// Every shared option — sinks, configurators, hardware-decode policy,
/// logging — is repeated here so a chain keeps flowing after the
/// narrowing step, in either order:
/// <code>
/// await using var player = await FrameFlowPlayer.Open(path)
///     .WithRepeatMode(RepeatMode.All)
///     .WithAudioSink(audio)
///     .BuildPlayerAsync(ct);
/// </code>
/// </para>
/// </remarks>
public interface IMediaPlayerBuilder
{
    /// <summary>
    /// Attaches an <see cref="IVideoSink"/> the player will drive
    /// during playback. Replaces any previously-attached video sink.
    /// </summary>
    IMediaPlayerBuilder WithVideoSink(IVideoSink sink);

    /// <summary>
    /// Attaches an <see cref="IAudioSink"/> the player will drive
    /// during playback. Replaces any previously-attached audio sink.
    /// </summary>
    IMediaPlayerBuilder WithAudioSink(IAudioSink sink);

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded
    /// video source and the registered <see cref="IVideoSink"/>.
    /// Replaces any previously-configured video transform.
    /// </summary>
    IMediaPlayerBuilder ConfigureVideo(
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>> configure
    );

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded
    /// audio source and the registered <see cref="IAudioSink"/>.
    /// Replaces any previously-configured audio transform.
    /// </summary>
    IMediaPlayerBuilder ConfigureAudio(
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>> configure
    );

    /// <summary>
    /// Configures hardware-decode policy. Defaults to
    /// <see cref="HardwareDecodeMode.Auto"/>.
    /// </summary>
    IMediaPlayerBuilder WithHardwareDecode(HardwareDecodeMode mode);

    /// <summary>
    /// Supplies the <see cref="ILoggerFactory"/> the player should use.
    /// A <see langword="null"/> factory is a no-op, so an optional
    /// logging step stays inside the chain.
    /// </summary>
    IMediaPlayerBuilder WithLogger(ILoggerFactory? loggerFactory);

    /// <summary>
    /// Sets the repeat mode the player starts in. Defaults to
    /// <see cref="RepeatMode.Off"/>.
    /// </summary>
    IMediaPlayerBuilder WithRepeatMode(RepeatMode mode);

    /// <summary>
    /// Supplies the <see cref="IPlaybackClock"/> that paces playback.
    /// When unset the player constructs its own wall-clock instance.
    /// </summary>
    IMediaPlayerBuilder WithClock(IPlaybackClock clock);

    /// <summary>
    /// Requests that hardware-decoded frames reach the video sink
    /// still on the GPU rather than being downloaded to system memory.
    /// Defaults to <see langword="false"/>; set it when the sink
    /// reports <c>PrefersHardwareFrames</c>.
    /// </summary>
    IMediaPlayerBuilder WithHardwareFrames(bool yieldHardwareFrames = true);

    /// <summary>
    /// Controls whether <see cref="IAudioSink.ActivateAsync"/> is
    /// called before the built player is handed back. Defaults to
    /// <see langword="true"/>; pass <see langword="false"/> to
    /// activate the sink yourself later.
    /// </summary>
    IMediaPlayerBuilder WithAudioActivation(bool activateAudioSink = true);

    /// <summary>
    /// Bootstraps the FFmpeg native runtime, loads the media source
    /// into a <see cref="PlaybackController"/>, and returns a ready
    /// <see cref="IMediaPlayer"/>. Playback is not started — the
    /// caller invokes <see cref="IMediaPlayer.PlayAsync"/> explicitly.
    /// </summary>
    Task<IMediaPlayer> BuildPlayerAsync(CancellationToken cancellationToken = default);
}
