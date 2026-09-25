using FrameFlow.Media.Tests.Doubles;
using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Media.Tests;

/// <summary>ADR-0080's counting and disposing rule, for the Media assembly's items.</summary>
[Collection(RefCountingCollection.Name)]
public sealed class RefCountingRuleTests
{
    [Fact]
    public void PcmAudioBuffer_FollowsTheRule()
    {
        RefCountConformance.AssertFollowsTheRule<PcmAudioBuffer>(
            () =>
            {
                var pool = new CountingArrayPool<short>();
                var buffer = PcmAudioBuffer.Create(
                    64,
                    48_000,
                    1,
                    TimeSpan.Zero,
                    0,
                    static (span, _) => span.Length,
                    pool
                );
                return (buffer, () => pool.Returns);
            },
            buffer => buffer.AddRef()
        );
    }

    [Fact]
    public void CpuVideoFrame_FollowsTheRule()
    {
        RefCountConformance.AssertFollowsTheRule<CpuVideoFrame>(
            () =>
            {
                var pool = new CountingArrayPool<byte>();
                var frame = CpuVideoFrame.Create(
                    PixelFormat.Bgra32,
                    2,
                    2,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    0,
                    static (_, _) => { },
                    pool
                );
                return (frame, () => pool.Returns);
            },
            frame => frame.AddRef()
        );
    }
}
