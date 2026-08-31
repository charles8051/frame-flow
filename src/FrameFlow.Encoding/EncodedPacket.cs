// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Encoding;

/// <summary>
/// One unit of encoded, compressed output produced by an
/// <see cref="IEncoder{TFrame, TPacket}"/> — a managed copy of an
/// <c>AVPacket</c>'s payload plus the timing metadata a muxer needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Substrate contract (ADR-0040, ADR-0049).</b> Encoded packets are
/// <see cref="IRefCounted"/> so they can flow through <c>FrameFlow.Graph</c>
/// edges just like decoded frames — enabling future compositions such as
/// encode → broadcast → [mp4, hls]. The payload is plain managed memory, so
/// "release" at refcount zero frees nothing native; the counter exists purely
/// to satisfy the substrate's uniform AddRef/Dispose ownership protocol.
/// </para>
/// <para>
/// <b>Timestamps.</b> <see cref="Pts"/>, <see cref="Dts"/>, and
/// <see cref="Duration"/> are expressed in the encoder's time base
/// (<see cref="TimeBaseNumerator"/> / <see cref="TimeBaseDenominator"/>). The
/// muxer rescales them into the container stream's time base before writing.
/// </para>
/// </remarks>
public sealed class EncodedPacket : IRefCounted
{
    private readonly byte[] _data;
    private int _refCount = 1;

    /// <summary>
    /// Creates an encoded packet. The packet starts with a reference count of
    /// one (owned by the creator).
    /// </summary>
    /// <param name="data">The compressed payload (copied out of the native packet; ownership transfers to this instance).</param>
    /// <param name="pts">Presentation timestamp in the encoder time base.</param>
    /// <param name="dts">Decompression timestamp in the encoder time base.</param>
    /// <param name="duration">Packet duration in the encoder time base.</param>
    /// <param name="timeBaseNumerator">Encoder time base numerator.</param>
    /// <param name="timeBaseDenominator">Encoder time base denominator.</param>
    /// <param name="isKeyFrame">Whether this packet begins a keyframe (IDR).</param>
    /// <param name="streamIndex">Source stream index assigned by the encoder (0 for a single video stream).</param>
    public EncodedPacket(
        byte[] data,
        long pts,
        long dts,
        long duration,
        int timeBaseNumerator,
        int timeBaseDenominator,
        bool isKeyFrame,
        int streamIndex = 0
    )
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        Pts = pts;
        Dts = dts;
        Duration = duration;
        TimeBaseNumerator = timeBaseNumerator;
        TimeBaseDenominator = timeBaseDenominator;
        IsKeyFrame = isKeyFrame;
        StreamIndex = streamIndex;
    }

    /// <summary>The compressed payload bytes.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>Presentation timestamp in the encoder time base.</summary>
    public long Pts { get; }

    /// <summary>Decompression timestamp in the encoder time base.</summary>
    public long Dts { get; }

    /// <summary>Packet duration in the encoder time base (0 when unknown).</summary>
    public long Duration { get; }

    /// <summary>Numerator of the encoder time base these timestamps use.</summary>
    public int TimeBaseNumerator { get; }

    /// <summary>Denominator of the encoder time base these timestamps use.</summary>
    public int TimeBaseDenominator { get; }

    /// <summary>Whether this packet starts a keyframe (carries AV_PKT_FLAG_KEY).</summary>
    public bool IsKeyFrame { get; }

    /// <summary>Source stream index the encoder assigned this packet.</summary>
    public int StreamIndex { get; }

    /// <inheritdoc/>
    public IRefCounted AddRef()
    {
        Interlocked.Increment(ref _refCount);
        return this;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Managed payload — nothing native to free when the count hits zero.
        // The decrement keeps the substrate's ownership accounting balanced.
        Interlocked.Decrement(ref _refCount);
    }
}
