// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
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
    /// <b>Ownership (ADR-0044).</b> Whoever constructed the sink owns it: you, if you built it;
    /// the view, for a sink that came from <c>WithAvaloniaVideoView</c>; the container, for a
    /// resolved singleton. The pass never disposes it, whoever that is.
    /// </remarks>
    /// <remarks>
    /// One sink serves any number of passes in sequence, so a sink that is expensive to build —
    /// one holding a loaded inference model — is built once and handed to each of them. Passes
    /// that run concurrently over one sink are undefined, and keeping them apart is the caller's
    /// job.
    /// </remarks>
    IPassBuilder WithVideoSink(IVideoSink sink);

    /// <summary>
    /// Attaches an <see cref="IAudioSink"/> the pass will drive. Replaces any previously-attached
    /// audio sink. The pass activates it before the run.
    /// </summary>
    /// <remarks>
    /// <b>Ownership (ADR-0044).</b> Whoever constructed the sink owns it; the pass never disposes
    /// it. One sink serves any number of passes in sequence, but not two at once.
    /// </remarks>
    IPassBuilder WithAudioSink(IAudioSink sink);

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded video source and the registered
    /// <see cref="IVideoSink"/>. The configurator receives a <see cref="GraphChain{T}"/> rooted at
    /// the decoder source's output and returns the chain the sink consumes from; the builder
    /// terminates it. This is where an inference or analysis operator goes.
    /// </summary>
    /// <remarks>
    /// Unless hardware frames are off (<see cref="WithHardwareFrames"/> false, or hardware decode
    /// disabled), the configurator runs twice: once before the decoder opens, to decide where
    /// frames go and size its pool, and once for the run. The first call's graph never runs. Build new
    /// nodes on each call, wired the same way each time, and do nothing else there, since anything
    /// else happens twice. A graph disposes none of its operators or sinks, so a node that needs a
    /// resource takes it on its first item, as the library's converters and <c>Infer</c> do. A node
    /// instance is wired into one graph only, so a configurator that attaches the same node twice
    /// fails with its input already connected.
    /// </remarks>
    /// <remarks>Replaces any previously-configured video transform.</remarks>
    IPassBuilder ConfigureVideo(Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>> configure);

    /// <summary>
    /// Inserts a consumer-controlled transform between the decoded audio source and the registered
    /// <see cref="IAudioSink"/>. Replaces any previously-configured audio transform.
    /// </summary>
    IPassBuilder ConfigureAudio(
        Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>> configure
    );

    /// <summary>
    /// Configures hardware-decode policy. Defaults to <see cref="HardwareDecodeMode.Auto"/>.
    /// Where hardware-decoded frames go is <see cref="WithHardwareFrames"/>'s.
    /// </summary>
    IPassBuilder WithHardwareDecode(HardwareDecodeMode mode);

    /// <summary>
    /// Whether hardware-decoded frames reach the configurator and the sink still on the GPU,
    /// overriding what the builder derives. <see langword="true"/> keeps them there, and a path
    /// with a node or sink that does not take them is refused by <see cref="BuildAsync"/>, naming
    /// it (#435). <see langword="false"/> always downloads them to system memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unset, the builder keeps them on the GPU when every node they reach says it takes GPU
    /// frames, the sink included (<see cref="IVideoSink.AcceptedDomains"/>), and the path holds a
    /// bounded number of them; otherwise it downloads them (#294). An inference operator with a
    /// device stage says it does, and so does a <c>ToCpu</c>, which also downloads them for the
    /// nodes after it. A node or sink that says nothing is taken to read pixels on the CPU. The
    /// builder logs which it chose, and why.
    /// </para>
    /// <para>
    /// The decoder's pool is sized for what the path can hold, which the builder computes from
    /// the nodes' declared holding and the sink's <see cref="IVideoSink.MaxHeldFrames"/> (ADR-0081).
    /// With <see langword="true"/>, a path with a holder that declares no bound is refused by
    /// <see cref="BuildAsync"/>, and the message names it.
    /// </para>
    /// </remarks>
    IPassBuilder WithHardwareFrames(bool yieldHardwareFrames = true);

    /// <summary>
    /// A device the pass's video decoder borrows instead of creating its own, so the device
    /// outlives the pass and anything built on it survives it. The decoder tries only the
    /// device's backend. The caller owns <paramref name="device"/> and disposes it after the pass.
    /// </summary>
    IPassBuilder WithHardwareDevice(HardwareDevice device);

    /// <summary>
    /// Hardware decode backends the pass's video decoder tries first, in order, then the rest in
    /// the platform default order (#532). An empty list clears a previous call.
    /// </summary>
    /// <remarks>
    /// Unset, the decoder tries the video sink's <see cref="IVideoSink.PreferredBackends"/> first
    /// when hardware frames stay on the GPU, and the platform default order otherwise. A device
    /// from <see cref="WithHardwareDevice"/> fixes the backend, and this is not used.
    /// </remarks>
    IPassBuilder WithPreferredBackends(params HardwareDecodeBackendKind[] backends);

    /// <summary>
    /// Codecs the pass's video decoder decodes in software even where a hardware backend could
    /// decode them, named as <see cref="VideoStreamInfo.CodecName"/> reports them (<c>"av1"</c>,
    /// <c>"h264"</c>) and compared without regard to case. An empty list clears a previous call.
    /// </summary>
    /// <remarks>
    /// Other codecs stay on hardware, which <see cref="WithHardwareDecode"/> with
    /// <see cref="HardwareDecodeMode.Disabled"/> does not do. A device from
    /// <see cref="WithHardwareDevice"/> does not override it. Under
    /// <see cref="HardwareDecodeMode.Required"/> an excluded codec fails to load.
    /// </remarks>
    IPassBuilder WithExcludedCodecs(params string[] codecs);

    /// <summary>
    /// How much of the video the decoder skips, for the whole pass. Defaults to
    /// <see cref="DecodeDiscardLevel.None"/>, which decodes every frame.
    /// </summary>
    /// <remarks>
    /// <see cref="DecodeDiscardLevel.KeyframesOnly"/> delivers the source's keyframes and nothing
    /// between them, each with its own timestamp. They arrive at the stream's keyframe interval,
    /// not at a chosen rate, which suits a pass that samples sparsely, such as thumbnails, and is
    /// wrong for one that needs every frame. FFmpeg's HEVC decoder can drop a keyframe at this
    /// level, reporting a duplicate picture order count, so an HEVC pass can deliver fewer
    /// keyframes than the stream has. What the other levels skip depends on the stream: on one
    /// without B-frames, <see cref="DecodeDiscardLevel.Bidirectional"/> skips nothing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a defined level.</exception>
    IPassBuilder WithDecodeDiscard(DecodeDiscardLevel level);

    /// <summary>
    /// Bounds the pass to the source from <paramref name="start"/>, inclusive, to
    /// <paramref name="end"/>, exclusive, in media time. A null <paramref name="end"/> runs to
    /// the end of the source. Replaces any previously-set range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pass seeks to a keyframe at or before <paramref name="start"/> and decodes from there,
    /// delivering nothing before <paramref name="start"/>, so the first frame delivered is the
    /// first at or after it. It stops reading once every stream it decodes reaches
    /// <paramref name="end"/>. A frame at exactly <paramref name="end"/> is not delivered, so
    /// ranges that share an endpoint split a source without overlap. A range that starts past
    /// the end of the source delivers nothing.
    /// </para>
    /// <para>
    /// An audio buffer is delivered whole when its presentation time is in the range; audio is
    /// not trimmed to it. With <see cref="WithDecodeDiscard"/>, the pass delivers the frames the
    /// decoder keeps that fall in the range, so with
    /// <see cref="DecodeDiscardLevel.KeyframesOnly"/> it delivers the keyframes in it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> is negative, or <paramref name="end"/> is not after it.
    /// </exception>
    IPassBuilder WithRange(TimeSpan start, TimeSpan? end);

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
    /// failed, the source has neither a video nor an audio stream, or the pass yields hardware
    /// frames and a holder on its video path declares no bound.
    /// </exception>
    Task<MediaPass> BuildAsync(CancellationToken cancellationToken = default);
}
