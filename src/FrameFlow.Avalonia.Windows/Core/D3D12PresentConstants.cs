// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Avalonia.Windows.Core;

/// <summary>
/// The root constants the D3D12 presenter's pixel shader reads for one frame: where the frame sits
/// in its texture, how a sample becomes limited-range BT.709 luma and chroma, and which part of the
/// frame the ring holds at what size (#774). Pure: sizes and a sample format in, sixteen values out.
/// </summary>
/// <remarks>
/// The conversion is the D3D11 presenter's (ADR-0063): BT.709, studio range, whatever the stream
/// says, since frames carry no colour metadata. The decode texture can be taller or wider than the
/// frame, so the shader scales its coordinates to the frame's part of the texture and clamps chroma
/// to the frame's last chroma sample, where the D3D11 path clamps at a frame-sized copy's edge.
/// <see cref="Sampling"/> is the D3D11 shader's <see cref="ShaderSampling"/>, in the frame's own
/// coordinates, so both converters read the same part with the same area filter (#767).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct D3D12PresentConstants(
    float UScale,
    float VScale,
    float ChromaUMax,
    float ChromaVMax,
    float YOffset,
    float YScale,
    float COffset,
    float CScale,
    ShaderSampling Sampling)
{
    /// <summary>The number of 32-bit values, as the root signature declares them.</summary>
    public const int Count = 16;

    /// <summary>
    /// The constants that draw <paramref name="source"/> of a <paramref name="frameWidth"/> x
    /// <paramref name="frameHeight"/> frame, decoded into a <paramref name="textureWidth"/> x
    /// <paramref name="textureHeight"/> texture, into a <paramref name="ring"/>-sized target.
    /// </summary>
    public static D3D12PresentConstants Create(
        YuvSampleFormat samples, int textureWidth, int textureHeight, int frameWidth, int frameHeight,
        SourceRect source, RingSize ring)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(textureWidth, frameWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(textureHeight, frameHeight);

        var levels = YuvLevels.For(samples);

        // The chroma plane is half the texture in each direction; the frame's last chroma sample
        // is the centre of chroma texel ceil(frame / 2) - 1.
        double chromaWidth = textureWidth / 2.0;
        double chromaHeight = textureHeight / 2.0;
        int frameChromaWidth = (frameWidth + 1) / 2;
        int frameChromaHeight = (frameHeight + 1) / 2;

        return new D3D12PresentConstants(
            UScale: (float)((double)frameWidth / textureWidth),
            VScale: (float)((double)frameHeight / textureHeight),
            ChromaUMax: (float)((frameChromaWidth - 0.5) / chromaWidth),
            ChromaVMax: (float)((frameChromaHeight - 0.5) / chromaHeight),
            YOffset: levels.YOffset,
            YScale: levels.YScale,
            COffset: levels.COffset,
            CScale: levels.CScale,
            Sampling: ShaderSampling.For(source, frameWidth, frameHeight, ring));
    }

    /// <summary>The constants that draw the whole frame at its own size.</summary>
    public static D3D12PresentConstants Create(
        YuvSampleFormat samples, int textureWidth, int textureHeight, int frameWidth, int frameHeight) =>
        Create(samples, textureWidth, textureHeight, frameWidth, frameHeight,
            SourceRect.Whole(frameWidth, frameHeight), new RingSize(frameWidth, frameHeight));
}

/// <summary>
/// The fence values one frame's hand-off from D3D12 to D3D11 uses, on the single shared fence both
/// devices signal in turn. Frame <c>n</c>, counting from 1, draws once the previous frame's copy is
/// done (<see cref="CopiedBefore"/>), signals <see cref="Drawn"/>, and the D3D11 copy waits for that
/// and signals <see cref="Copied"/>. The values rise strictly, so a signal never lowers the fence.
/// </summary>
internal readonly record struct BridgeFenceValues(ulong CopiedBefore, ulong Drawn, ulong Copied)
{
    public static BridgeFenceValues For(ulong frame)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frame);
        return new BridgeFenceValues(2 * (frame - 1), 2 * frame - 1, 2 * frame);
    }
}
