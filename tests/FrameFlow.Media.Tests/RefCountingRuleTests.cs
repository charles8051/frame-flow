using System.Buffers;
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
                var owner = new CountingOwner<short>(64);
                var buffer = new PcmAudioBuffer(owner, 64, 48_000, 1, TimeSpan.Zero);
                return (buffer, () => owner.Disposals);
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
                var owner = new CountingOwner<byte>(16);
                var frame = new CpuVideoFrame(owner, 2, 2, 8, PixelFormat.Bgra32, TimeSpan.Zero);
                return (frame, () => owner.Disposals);
            },
            frame => frame.AddRef()
        );
    }

    private sealed class CountingOwner<T>(int length) : IMemoryOwner<T>
    {
        private readonly T[] _array = new T[length];

        public int Disposals { get; private set; }

        public Memory<T> Memory => _array;

        public void Dispose() => Disposals++;
    }
}
