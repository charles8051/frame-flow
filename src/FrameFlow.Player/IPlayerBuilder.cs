// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Player;

/// <summary>
/// Fluent builder for a player. Returned by <see cref="FrameFlowPlayer.Create"/>; each method
/// returns the builder so chains flow naturally until <see cref="BuildPlayerAsync"/> resolves.
/// </summary>
/// <remarks>
/// <para>
/// <b>One terminal.</b> Every option here means something to the player this builds, so there is
/// nothing to refuse and no narrowing. An unpaced run over one source is
/// <see cref="FrameFlowPass"/>, a separate entry point, because the difference between the two is
/// a clock rather than a transport surface.
/// </para>
/// <para>
/// <b>Media is an option.</b> Every player is a queue and a queue can be empty, so
/// <see cref="WithMedia(string)"/> is a step in the chain rather than an argument to the entry. A
/// chain that names none builds a player with its sinks warm and nothing loaded; the first
/// <see cref="IMediaPlayer.PlayAsync"/> starts whatever
/// <see cref="IMediaPlaylistPlayer.AddAsync"/> has put in the queue by then.
/// </para>
/// <para>
/// <b>Configurator shape.</b> The <see cref="ConfigureVideo"/> / <see cref="ConfigureAudio"/>
/// hooks receive a <see cref="GraphChain{T}"/> rooted at the decoder source's output and must
/// return a chain that the builder will terminate at the sink. Use these to insert resize and
/// convert operators, tee off inference branches, and so on.
/// </para>
/// </remarks>
public interface IPlayerBuilder
{
    /// <summary>
    /// Names the media the built player starts with, as the file at <paramref name="path"/>.
    /// Replaces anything a previous <c>WithMedia</c> named. The file is not opened until
    /// <see cref="BuildPlayerAsync"/> resolves.
    /// </summary>
    IPlayerBuilder WithMedia(string path);

    /// <summary>
    /// Names the media the built player starts with. Replaces anything a previous
    /// <c>WithMedia</c> named.
    /// </summary>
    IPlayerBuilder WithMedia(IMediaSource source);

    /// <summary>
    /// Names an ordered queue the built player starts with, replacing anything a previous
    /// <c>WithMedia</c> named. The queue may be empty, and the player is then built with nothing
    /// loaded.
    /// </summary>
    IPlayerBuilder WithMedia(IEnumerable<IMediaSource> sources);

    /// <summary>
    /// Attaches an <see cref="IVideoSink"/> the player will drive during playback. Replaces any
    /// previously-attached video sink.
    /// </summary>
    /// <remarks>
    /// <b>Ownership (ADR-0044).</b> Whoever constructed the sink owns it: you, if you built it;
    /// the view, for a sink that came from <c>WithAvaloniaVideoView</c>; the container, for a
    /// resolved singleton. The player never disposes it, whoever that is.
    /// </remarks>
    /// <remarks>
    /// One sink serves one player at a time, and may serve several in sequence, which is what
    /// keeps a presenter warm across a playlist's items. Nothing enforces the "at a time":
    /// attaching one sink to two players that run concurrently is undefined, and keeping them
    /// apart is the caller's job.
    /// </remarks>
    IPlayerBuilder WithVideoSink(IVideoSink sink);

    /// <summary>
    /// Attaches an <see cref="IAudioSink"/> the player will drive during playback. Replaces any
    /// previously-attached audio sink.
    /// </summary>
    /// <remarks>
    /// <b>Ownership (ADR-0044).</b> Whoever constructed the sink owns it: you, if you built it;
    /// the view, for a sink that came from <c>WithAvaloniaVideoView</c>; the container, for a
    /// resolved singleton. The player never disposes it, whoever that is.
    /// </remarks>
    /// <remarks>
    /// One sink serves one player at a time, and may serve several in sequence, which is what
    /// keeps a presenter warm across a playlist's items. Nothing enforces the "at a time":
    /// attaching one sink to two players that run concurrently is undefined, and keeping them
    /// apart is the caller's job.
    /// </remarks>
    IPlayerBuilder WithAudioSink(IAudioSink sink);

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded video source and the registered
    /// <see cref="IVideoSink"/>. The configurator receives a <see cref="GraphChain{T}"/> rooted at
    /// the decoder source's output; whatever chain it returns is terminated at the sink.
    /// </summary>
    /// <param name="configure">
    /// Receives the post-decode chain; returns the chain the sink will consume from. Do NOT call
    /// <see cref="GraphChain{T}.To"/> inside the configurator — the builder calls it on the
    /// returned chain after attaching the sink.
    /// </param>
    /// <remarks>Replaces any previously-configured video transform.</remarks>
    IPlayerBuilder ConfigureVideo(
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>> configure
    );

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded audio source and the registered
    /// <see cref="IAudioSink"/>.
    /// </summary>
    /// <remarks>Replaces any previously-configured audio transform.</remarks>
    IPlayerBuilder ConfigureAudio(
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>> configure
    );

    /// <summary>
    /// Configures hardware-decode policy. Defaults to <see cref="HardwareDecodeMode.Auto"/>.
    /// </summary>
    IPlayerBuilder WithHardwareDecode(HardwareDecodeMode mode);

    /// <summary>
    /// Supplies the <see cref="ILoggerFactory"/> the player should use. When unset, logging is
    /// silent. A <see langword="null"/> factory is a no-op, so an optional logging step stays
    /// inside the chain instead of forcing the caller out to a local.
    /// </summary>
    IPlayerBuilder WithLogger(ILoggerFactory? loggerFactory);

    /// <summary>
    /// Sets the repeat mode the built player starts in. Defaults to
    /// <see cref="RepeatMode.Off"/>.
    /// </summary>
    IPlayerBuilder WithRepeatMode(RepeatMode mode);

    /// <summary>
    /// Supplies the <see cref="IPlaybackClock"/> that paces playback. When unset the player
    /// constructs its own wall-clock instance.
    /// </summary>
    IPlayerBuilder WithClock(IPlaybackClock clock);

    /// <summary>
    /// Supplies the wall time the loop-stall watchdog and the position ticker run on, and that
    /// the player's own <see cref="PlaybackClock"/> uses when <see cref="WithClock"/> named none.
    /// Defaults to <see cref="TimeProvider.System"/>.
    /// </summary>
    /// <remarks>
    /// This is elapsed real time, not the presentation timeline an <see cref="IPlaybackClock"/>
    /// carries. The watchdog's job is to notice that the timeline advanced while frames stopped,
    /// so it cannot read the clock it supervises. A harness on simulated time wants both: name a
    /// provider here, and either let the player build its clock on it or pass a
    /// <see cref="PlaybackClock"/> built on the same provider.
    /// </remarks>
    IPlayerBuilder WithTimeProvider(TimeProvider timeProvider);

    /// <summary>
    /// Tunes the lateness-recovery walk, which sheds decode work when presentation falls behind
    /// the clock. It applies to every item the player plays. When unset the walk stays off.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> fails <see cref="LatenessRecoveryOptions.Validate"/>.
    /// </exception>
    IPlayerBuilder WithLatenessRecovery(LatenessRecoveryOptions options);

    /// <summary>
    /// Requests that hardware-decoded frames reach the video sink still on the GPU rather than
    /// being downloaded to system memory. Defaults to <see langword="false"/>; set it when the
    /// sink reports <c>PrefersHardwareFrames</c>.
    /// </summary>
    IPlayerBuilder WithHardwareFrames(bool yieldHardwareFrames = true);

    /// <summary>
    /// Controls whether <see cref="IAudioSink.ActivateAsync"/> is called before the built player
    /// is handed back. Defaults to <see langword="true"/>; pass <see langword="false"/> to
    /// activate the sink yourself later.
    /// </summary>
    IPlayerBuilder WithAudioActivation(bool activateAudioSink = true);

    /// <summary>
    /// Bootstraps the FFmpeg native runtime, loads the first item into a
    /// <see cref="PlaybackController"/>, and returns a ready player — the full
    /// pause/resume/seek/repeat state machine, over the queue the chain named. Playback is not
    /// started; the caller invokes <see cref="IMediaPlayer.PlayAsync"/> explicitly. A caller who
    /// wants the small surface names <see cref="IMediaPlayer"/>.
    /// </summary>
    Task<IMediaPlaylistPlayer> BuildPlayerAsync(CancellationToken cancellationToken = default);
}
