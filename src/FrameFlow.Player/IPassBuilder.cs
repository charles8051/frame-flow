// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Player;

/// <summary>
/// Fluent builder for a <see cref="MediaPass"/>. Returned by <see cref="FrameFlowPass.Create(string)"/>;
/// each method returns the builder so chains flow naturally until <see cref="BuildAsync"/> resolves.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is missing, and why.</b> There is no repeat mode, no clock, no queue and no transport.
/// A pass runs its source through once, waits on no presentation time, and ends. Everything on
/// that list belongs to <see cref="FrameFlowPlayer"/>, which is the other entry point and the one
/// to reach for when a human is watching.
/// </para>
/// <para>
/// The source is named at <see cref="FrameFlowPass.Create(string)"/> rather than here, because a
/// pass with no source has nothing to do. A player takes its media as an option instead, and
/// accepts none, because a player with an empty queue is a thing a host wants.
/// </para>
/// </remarks>
public interface IPassBuilder
{
    /// <summary>
    /// Attaches an <see cref="IVideoSink"/> the pass will drive. Replaces any previously-attached
    /// video sink.
    /// </summary>
    /// <remarks>
    /// The sink is yours. You constructed it, so you dispose it; the pass never does (ADR-0044).
    /// One sink serves any number of passes in sequence, so a sink that is expensive to build —
    /// one holding a loaded inference model — is built once and handed to each of them.
    /// </remarks>
    IPassBuilder WithVideoSink(IVideoSink sink);

    /// <summary>
    /// Attaches an <see cref="IAudioSink"/> the pass will drive. Replaces any previously-attached
    /// audio sink. The pass activates it before the run.
    /// </summary>
    /// <remarks>
    /// The sink is yours. You constructed it, so you dispose it; the pass never does (ADR-0044).
    /// One sink serves any number of passes in sequence.
    /// </remarks>
    IPassBuilder WithAudioSink(IAudioSink sink);

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded video source and the registered
    /// <see cref="IVideoSink"/>. The configurator receives a <see cref="GraphChain{T}"/> rooted at
    /// the decoder source's output and returns the chain the sink consumes from; the builder
    /// terminates it. This is where an inference or analysis operator goes.
    /// </summary>
    /// <remarks>Replaces any previously-configured video transform.</remarks>
    IPassBuilder ConfigureVideo(Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>> configure);

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded audio source and the registered
    /// <see cref="IAudioSink"/>. Replaces any previously-configured audio transform.
    /// </summary>
    IPassBuilder ConfigureAudio(
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>> configure
    );

    /// <summary>
    /// Configures hardware-decode policy. Defaults to <see cref="HardwareDecodeMode.Auto"/>.
    /// Hardware-decoded frames are downloaded to system memory before they reach the sink; a pass
    /// has no equivalent of the player's <c>WithHardwareFrames</c>.
    /// </summary>
    IPassBuilder WithHardwareDecode(HardwareDecodeMode mode);

    /// <summary>
    /// Supplies the <see cref="ILoggerFactory"/> the pass should use. When unset, logging is
    /// silent. A <see langword="null"/> factory is a no-op, so an optional logging step stays
    /// inside the chain instead of forcing the caller out to a local.
    /// </summary>
    IPassBuilder WithLogger(ILoggerFactory? loggerFactory);

    /// <summary>
    /// Bootstraps the FFmpeg native runtime, opens the source, constructs the decoders and returns
    /// a ready <see cref="MediaPass"/>. Nothing runs yet: the caller invokes
    /// <see cref="MediaPass.RunToCompletionAsync"/> explicitly.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No sink was attached, a configurator was given without its sink, the FFmpeg bootstrap
    /// failed, or the source has neither a video nor an audio stream.
    /// </exception>
    Task<MediaPass> BuildAsync(CancellationToken cancellationToken = default);
}
