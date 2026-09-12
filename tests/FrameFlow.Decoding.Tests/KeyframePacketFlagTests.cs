using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Pins the one part of the #134 fix that <see cref="GopShedGateTests"/> cannot cover: that
/// <c>VideoDecoder.IsKeyframePacket</c> reads <c>AV_PKT_FLAG_KEY</c> off the real
/// <c>AVPacket</c> layout.
/// </summary>
/// <remarks>
/// The gate's safety argument rests entirely on this bit. Read it as always false and the
/// gate never reopens, so video stops at the first shed. Read it as always true and the gate
/// never engages, so the corruption is back. Neither failure shows up in the gate's own
/// tests, and neither makes FFmpeg report an error.
/// </remarks>
public sealed class KeyframePacketFlagTests : IClassFixture<FfmpegBootstrapFixture>
{
    public KeyframePacketFlagTests(FfmpegBootstrapFixture _) { }

    [RequiresFfmpegFact]
    public unsafe void TheKeyFlagIsReadFromTheRealPacketLayout()
    {
        var ptr = FFAvCodec.av_packet_alloc();
        Assert.NotEqual(nint.Zero, ptr);

        try
        {
            // A freshly allocated packet carries no flags.
            Assert.False(VideoDecoder.IsKeyframePacket(ptr));

            ref var pkt = ref Unsafe.AsRef<AVPacket>((void*)ptr);

            pkt.flags |= FFmpegConstants.PktFlagKey;
            Assert.True(VideoDecoder.IsKeyframePacket(ptr));

            pkt.flags &= ~FFmpegConstants.PktFlagKey;
            Assert.False(VideoDecoder.IsKeyframePacket(ptr));
        }
        finally
        {
            FFAvCodec.av_packet_free(ref ptr);
        }
    }

    [Fact]
    public void ANullPacketIsNotAKeyframe()
    {
        // The send path guards on this before dereferencing; a null pointer must read as
        // "not a keyframe" rather than crash or reopen the gate.
        Assert.False(VideoDecoder.IsKeyframePacket(nint.Zero));
    }
}
