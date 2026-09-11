// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;
using FrameFlow.Graph;

namespace FrameFlow.Player;

/// <summary>
/// Fluent builder for playback. Returned by
/// <see cref="FrameFlowPlayer.Open(string)"/>; each method returns
/// the builder so chains flow naturally until a terminal resolves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two terminals.</b> <see cref="BuildAsync"/> returns a
/// <see cref="PlayerSession"/> — open a source, play it to end of
/// stream, done. <see cref="BuildPlayerAsync"/> returns an
/// <see cref="IMediaPlayer"/>, the full state machine with pause,
/// resume, seek, repeat, position and diagnostics. Pick by which you
/// need; everything before the terminal is the same chain.
/// </para>
/// <para>
/// The player-only options (<see cref="WithRepeatMode"/>,
/// <see cref="WithClock"/>, <see cref="WithHardwareFrames"/>,
/// <see cref="WithAudioActivation"/>) narrow the chain to
/// <see cref="IMediaPlayerBuilder"/>, which offers
/// <see cref="IMediaPlayerBuilder.BuildPlayerAsync"/> and no
/// <c>BuildAsync</c>. Those settings have no meaning for a
/// <see cref="PlayerSession"/>, so the narrowing turns a would-be
/// silently-ignored option into a compile error.
/// </para>
/// <para>
/// A minimum-viable build is
/// <c>FrameFlowPlayer.Open(path).BuildAsync()</c> — that produces a
/// session that opens the file and probes streams but has no sinks
/// wired so calling <see cref="PlayerSession.PlayToCompletionAsync"/>
/// is a no-op for any stream that lacks a sink. For real playback,
/// attach at least one sink via <see cref="WithVideoSink"/> or
/// <see cref="WithAudioSink"/>.
/// </para>
/// <para>
/// <b>Configurator shape.</b> The
/// <see cref="ConfigureVideo"/> / <see cref="ConfigureAudio"/> hooks
/// receive a <see cref="GraphChain{T}"/> rooted at the decoder
/// source's output and must return a chain that the builder will
/// terminate at the sink. Use these to insert resize/convert
/// operators, tee off inference branches, etc.
/// </para>
/// </remarks>
public interface IPlayerBuilder
{
    /// <summary>
    /// Attaches an <see cref="IVideoSink"/> the player will drive
    /// during playback. Replaces any previously-attached video sink.
    /// </summary>
    IPlayerBuilder WithVideoSink(IVideoSink sink);

    /// <summary>
    /// Attaches an <see cref="IAudioSink"/> the player will drive
    /// during playback. Replaces any previously-attached audio sink.
    /// </summary>
    IPlayerBuilder WithAudioSink(IAudioSink sink);

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded
    /// video source and the registered <see cref="IVideoSink"/>. The
    /// configurator receives a <see cref="GraphChain{T}"/> rooted at
    /// the decoder source's output; whatever chain it returns is
    /// terminated at the sink.
    /// </summary>
    /// <param name="configure">
    /// Receives the post-decode chain; returns the chain the sink
    /// will consume from. Do NOT call <see cref="GraphChain{T}.To"/>
    /// inside the configurator — the builder calls it on the returned
    /// chain after attaching the sink.
    /// </param>
    /// <remarks>
    /// Replaces any previously-configured video transform.
    /// </remarks>
    IPlayerBuilder ConfigureVideo(
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>> configure
    );

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded
    /// audio source and the registered <see cref="IAudioSink"/>.
    /// </summary>
    /// <remarks>
    /// Replaces any previously-configured audio transform.
    /// </remarks>
    IPlayerBuilder ConfigureAudio(
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>> configure
    );

    /// <summary>
    /// Configures hardware-decode policy. Defaults to
    /// <see cref="HardwareDecodeMode.Auto"/>.
    /// </summary>
    IPlayerBuilder WithHardwareDecode(HardwareDecodeMode mode);

    /// <summary>
    /// Supplies the <see cref="ILoggerFactory"/> the player should use.
    /// When unset, logging is silent. A <see langword="null"/> factory
    /// is a no-op, so an optional logging step stays inside the chain
    /// instead of forcing the caller out to a local.
    /// </summary>
    IPlayerBuilder WithLogger(ILoggerFactory? loggerFactory);

    /// <summary>
    /// Sets the repeat mode the built player starts in. Defaults to
    /// <see cref="RepeatMode.Off"/>. Narrows the chain to
    /// <see cref="IMediaPlayerBuilder"/>.
    /// </summary>
    IMediaPlayerBuilder WithRepeatMode(RepeatMode mode);

    /// <summary>
    /// Supplies the <see cref="IPlaybackClock"/> that paces playback.
    /// When unset the player constructs its own wall-clock instance.
    /// Narrows the chain to <see cref="IMediaPlayerBuilder"/>.
    /// </summary>
    IMediaPlayerBuilder WithClock(IPlaybackClock clock);

    /// <summary>
    /// Requests that hardware-decoded frames reach the video sink
    /// still on the GPU rather than being downloaded to system memory.
    /// Defaults to <see langword="false"/>; set it when the sink
    /// reports <c>PrefersHardwareFrames</c>. Narrows the chain to
    /// <see cref="IMediaPlayerBuilder"/>.
    /// </summary>
    IMediaPlayerBuilder WithHardwareFrames(bool yieldHardwareFrames = true);

    /// <summary>
    /// Controls whether <see cref="IAudioSink.ActivateAsync"/> is
    /// called before the built player is handed back. Defaults to
    /// <see langword="true"/>. Narrows the chain to
    /// <see cref="IMediaPlayerBuilder"/>.
    /// </summary>
    IMediaPlayerBuilder WithAudioActivation(bool activateAudioSink = true);

    /// <summary>
    /// Bootstraps the FFmpeg native runtime, opens the media source,
    /// constructs the decoders, builds the playback graph, and returns
    /// a ready <see cref="PlayerSession"/>. Playback is not started —
    /// the caller invokes
    /// <see cref="PlayerSession.PlayToCompletionAsync"/> explicitly.
    /// </summary>
    Task<PlayerSession> BuildAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Bootstraps the FFmpeg native runtime, loads the media source
    /// into a <see cref="PlaybackController"/>, and returns a ready
    /// <see cref="IMediaPlayer"/> — the full pause/resume/seek/repeat
    /// state machine. Playback is not started; the caller invokes
    /// <see cref="IMediaPlayer.PlayAsync"/> explicitly.
    /// </summary>
    Task<IMediaPlayer> BuildPlayerAsync(CancellationToken cancellationToken = default);
}
