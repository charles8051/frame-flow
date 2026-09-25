using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Face.Tests;

/// <summary>ADR-0080's counting and disposing rule, for the Face assembly's items.</summary>
[Collection(RefCountingCollection.Name)]
public sealed class RefCountingRuleTests
{
    [Fact]
    public void DetectedFaceFrameRef_FollowsTheRule()
    {
        RefCountConformance.AssertFollowsTheRule<DetectedFaceFrameRef>(
            () =>
            {
                var frame = new ReleaseCountingFrame();
                var item = new DetectedFaceFrameRef(frame, []);
                return (item, () => frame.Releases);
            },
            item => item.AddRef()
        );
    }

    /// <summary>A frame that counts how many times it is released.</summary>
    private sealed class ReleaseCountingFrame : IVideoFrame
    {
        private int _releases;

        public int Releases => Volatile.Read(ref _releases);

        public int Width => 2;
        public int Height => 2;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() => Interlocked.Increment(ref _releases);

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
