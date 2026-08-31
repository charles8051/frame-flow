// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Encoding;

/// <summary>
/// Transforms decoded frames into compressed packets (ADR-0040). An encoder is
/// stateful — it owns a codec context and rate-control state — and is
/// constructed once per output stream.
/// </summary>
/// <typeparam name="TFrame">The decoded input frame type.</typeparam>
/// <typeparam name="TPacket">The encoded output packet type.</typeparam>
/// <remarks>
/// <para>
/// <b>Why per-frame rather than a stream operator.</b> ADR-0040 sketched
/// <c>Encode(FramePipeline&lt;TFrame&gt;)</c>, but the current
/// <c>FrameFlow.Graph</c> substrate (ADR-0049) has no per-operator
/// end-of-stream hook, and an encoder must drain buffered packets at EOS via
/// <see cref="Flush"/>. Exposing the encoder as a stateful primitive — mirror
/// of how <c>FrameFlow.Decoding.VideoDecoder</c> is a primitive that adapters
/// wrap — keeps the flush boundary explicit and correct. The
/// <see cref="Mp4VideoWriter"/> terminal composes encoder + muxer and owns the
/// flush-then-trailer ordering.
/// </para>
/// <para>
/// <b>Input ownership.</b> <see cref="Encode"/> reads the frame's pixels and
/// does <b>not</b> dispose it; the caller retains ownership. Returned packets
/// are owned by the caller (refcount 1) and must be disposed when consumed.
/// </para>
/// </remarks>
public interface IEncoder<TFrame, TPacket> : IDisposable
    where TFrame : IDisposable
    where TPacket : class, IRefCounted
{
    /// <summary>Static description of the encoder for diagnostics.</summary>
    EncoderInfo Info { get; }

    /// <summary>
    /// Encodes one input frame, returning zero or more output packets. A
    /// well-behaved low-latency encoder (libopenh264 has no B-frames) returns
    /// roughly one packet per frame; the contract permits zero (the encoder
    /// is buffering) or many.
    /// </summary>
    IReadOnlyList<TPacket> Encode(TFrame frame);

    /// <summary>
    /// Signals end-of-stream and drains any buffered packets. Must be called
    /// exactly once after the last <see cref="Encode"/> and before the muxer's
    /// trailer is written. Returns the tail packets (empty for an encoder that
    /// buffers nothing).
    /// </summary>
    IReadOnlyList<TPacket> Flush();
}

/// <summary>
/// An H.264 (or other) video encoder: an <see cref="IEncoder{TFrame, TPacket}"/>
/// from decoded <see cref="IVideoFrame"/>s to <see cref="EncodedPacket"/>s.
/// </summary>
public interface IVideoEncoder : IEncoder<IVideoFrame, EncodedPacket>
{
    /// <summary>
    /// <see langword="true"/> once the encoder has opened its codec context
    /// (after the first <see cref="IEncoder{TFrame, TPacket}.Encode"/> call for
    /// geometry-inferring encoders). The muxer wires its stream parameters from
    /// an open encoder.
    /// </summary>
    bool IsOpen { get; }
}
