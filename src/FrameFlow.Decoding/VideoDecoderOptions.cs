// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Decoding;

/// <summary>
/// Configuration options for <see cref="VideoDecoder"/>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="AudioDecoderOptions"/>. Kept deliberately small — the
/// runtime full-queue policy (<see cref="VideoDecoder.DropNewestWhenQueueFull"/>)
/// and hardware-frame yield (<see cref="VideoDecoder.YieldHardwareFrames"/>) stay
/// mutable on the decoder because callers toggle them after construction; only
/// settings fixed when the decoder opens live here.
/// </remarks>
public sealed class VideoDecoderOptions
{
    private readonly int _heldHardwareFrames;
    private readonly int _decoderThreads;
    private readonly ColorMatrix _colorMatrix;

    /// <summary>
    /// Capacity of the decoder's internal bounded packet queue, in packets.
    /// The demux pump writes cloned packets into this queue; once it is full the
    /// pump either blocks (<see cref="VideoDecoder.DropNewestWhenQueueFull"/> is
    /// <see langword="false"/> — the no-audio/block default) or drops the newest
    /// packet (<see cref="VideoDecoder.DropNewestWhenQueueFull"/> is
    /// <see langword="true"/> — when an audio stream shares the pump). Defaults to
    /// 512. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For video this is a read-ahead bound in frames.</b> The demuxer returns
    /// one packet per coded frame, so the depth is ~1:1 with frames: 512 ≈ 512
    /// frames ≈ ~20 s at 25 fps (~17 s at 30 fps, ~8.5 s at 60 fps) of compressed
    /// read-ahead.
    /// </para>
    /// <para>
    /// <b>Block mode (no audio consumer)</b> paces the pump to the video consumer's
    /// drain rate and never drops a packet, so a smaller value (e.g. 64 ≈ ~2.5 s at
    /// 25 fps) is safe and trims read-ahead latency and memory. <b>Drop mode (audio
    /// present)</b> sheds the newest packet when full; keep this large (≥ 512) so a
    /// post-seek GOP burst cannot fill the queue and trip drop-newest mid-GOP, which
    /// would break P-frame reconstruction and garble the GOP (see
    /// <see cref="VideoDecoder.SendPacketAsync"/> and the reset-path note in
    /// <see cref="VideoDecoder.ResetPacketQueue"/>).
    /// </para>
    /// <para>
    /// Lowering this is otherwise primarily useful for tests that need to observe the
    /// backpressure boundary deterministically without buffering hundreds of frames
    /// first. Production callers rarely need to change it.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Null derives the capacity from the stream's frame rate via
    /// <see cref="ReadAheadCapacity"/>, so the queue holds a comparable amount of <i>time</i>
    /// to the audio queue rather than a comparable number of packets. Set a value only to
    /// pin it; a fixed count reintroduces the frame-rate dependence described there.
    /// </remarks>
    public int? PacketQueueCapacity { get; init; }

    /// <summary>
    /// How many hardware frames the caller holds at once after the decoder yields them
    /// (<see cref="VideoDecoder.YieldHardwareFrames"/>). Defaults to 0. Must not be negative.
    /// </summary>
    /// <remarks>
    /// A decoder on a fixed pool (D3D11VA, DXVA2) asks FFmpeg for the surfaces this needs
    /// beyond the pool's spare ones (<c>extra_hw_frames</c>), and waits for a release when this
    /// many are out (ADR-0081). A growable pool (VideoToolbox) ignores it. Software decode and the
    /// readback path hold no pool surfaces.
    /// </remarks>
    public int HeldHardwareFrames
    {
        get => _heldHardwareFrames;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _heldHardwareFrames = value;
        }
    }

    /// <summary>
    /// How many threads a software decoder uses. 0, the default, chooses from the processor count,
    /// up to 8. 1 decodes on one thread. Must not be negative.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set it when several decoders run at once and should share the cores, or to 1 for the lowest
    /// decode delay. The decoder uses frame threading where the codec supports it, which holds up to
    /// one frame per extra thread in flight: the first frame after an open or a seek waits for that
    /// many more packets, and each frame in flight holds its picture's memory.
    /// </para>
    /// <para>
    /// A hardware decoder ignores it. So does a codec that decodes on one thread.
    /// </para>
    /// </remarks>
    public int DecoderThreads
    {
        get => _decoderThreads;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _decoderThreads = value;
        }
    }

    /// <summary>
    /// The matrix every frame converts to RGB with (#841). <see cref="ColorMatrix.Auto"/>, the
    /// default, takes the one the stream signals, and for a stream that signals none BT.709 at 720
    /// rows or more and BT.601 below.
    /// </summary>
    /// <remarks>
    /// It applies to the software decoder's <see cref="PixelFormat.Bgra32"/> frames, to
    /// <see cref="GpuVideoFrame.ReadbackToCpuBgra32"/> and to the D3D11 and D3D12 presenters, so a
    /// hardware frame and a software frame of the same stream convert alike. Pick a fixed matrix to
    /// keep a consumer calibrated on one conversion, such as a model's embeddings or stored image
    /// hashes, whatever the stream signals.
    /// </remarks>
    public ColorMatrix ColorMatrix
    {
        get => _colorMatrix;
        init
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined color matrix.");
            _colorMatrix = value;
        }
    }

    /// <summary>
    /// A device the decoder borrows instead of creating its own. The decoder tries only that
    /// device's backend, and the device outlives the decoder. Null, the default, lets the decoder
    /// create a device as the hardware-decode options choose. Unused when hardware decode is
    /// <see cref="FrameFlow.Media.HardwareDecodeMode.Disabled"/>. A decoder opened on a device
    /// that is lost fails with <see cref="FrameFlow.Media.DeviceLostException"/> (#759).
    /// </summary>
    public HardwareDevice? Device { get; init; }

    /// <summary>
    /// The devices a player keeps across its items. The decoder borrows the cached device for
    /// each backend it tries instead of creating one, and still chooses the backend as it would
    /// without it. Ignored when <see cref="Device"/> is set.
    /// </summary>
    internal HardwareDeviceCache? DeviceCache { get; init; }
}
