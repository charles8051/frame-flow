using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.MotionClip.Tests;

/// <summary>ADR-0080's counting and disposing rule, for the MotionClip assembly's items.</summary>
[Collection(RefCountingCollection.Name)]
public sealed class RefCountingRuleTests
{
    [Fact]
    public void ClipSegment_FollowsTheRule()
    {
        RefCountConformance.AssertFollowsTheRule<ClipSegment>(
            () =>
            {
                var frame = new DisposeCountingFrame();
                var segment = new ClipSegment(
                    [frame],
                    DateTime.UnixEpoch,
                    preRollCount: 0,
                    ClipEndReason.Flushed
                );
                return (segment, () => frame.Disposals);
            },
            segment => segment.AddRef()
        );
    }

    private sealed class DisposeCountingFrame : IVideoFrame
    {
        public int Disposals { get; private set; }

        public int Width => 2;
        public int Height => 2;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => throw new NotSupportedException();

        public CpuFrameData ToCpu() => throw new NotSupportedException();

        public void Dispose() => Disposals++;
    }
}
