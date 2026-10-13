using FrameFlow.Graph;
using FrameFlow.Tests.Shared;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Frames and buffers carry the timeline they belong to (ADR-0084, decisions 3 and 4): the
/// storage types keep it, the factories that existed before keep producing
/// <see cref="TimelineId.None"/>, an implementation that knows nothing of timelines reports none,
/// and presentation reports the presented frame's.
/// </summary>
public sealed class TimelineCarryTests
{
    // ── Storage ───────────────────────────────────────────────────────

    [Fact]
    public void ACpuFrame_KeepsTheTimelineItWasCreatedOn()
    {
        var timeline = TimelineId.Next();

        using var frame = CpuVideoFrame.Create(
            PixelFormat.Bgra32, 4, 2, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40), timeline,
            0, static (_, _) => { });

        Assert.Equal(timeline, frame.Timeline);
        Assert.Equal(timeline, ((IFrame)frame).Timeline);
        Assert.Equal(TimeSpan.FromSeconds(1), frame.Pts);
    }

    [Fact]
    public void ACpuFrameFromTheFactoryWithoutATimeline_IsOnNone()
    {
        using var frame = CpuVideoFrame.Create(
            PixelFormat.Bgra32, 4, 2, TimeSpan.Zero, TimeSpan.Zero, 0, static (_, _) => { });

        Assert.Equal(TimelineId.None, frame.Timeline);
    }

    [Fact]
    public void APcmBuffer_KeepsTheTimelineItWasCreatedOn()
    {
        var timeline = TimelineId.Next();

        using var buffer = PcmAudioBuffer.Create(
            4, 48_000, 2, TimeSpan.FromSeconds(2), timeline, 0, static (samples, _) => samples.Length);

        Assert.Equal(timeline, buffer.Timeline);
        Assert.Equal(timeline, ((IAudioBuffer)buffer).Timeline);
        Assert.Equal(TimeSpan.FromSeconds(2), buffer.PresentationTime);
    }

    [Fact]
    public void APcmBufferFromTheFactoryWithoutATimeline_IsOnNone()
    {
        using var buffer = PcmAudioBuffer.Create(4, 48_000, 2, TimeSpan.Zero, 0, static (samples, _) => samples.Length);

        Assert.Equal(TimelineId.None, buffer.Timeline);
    }

    [Fact]
    public void AnImplementationThatStoresNoTimeline_ReportsNone()
    {
        // The members are default interface members, so a frame or buffer written before
        // timelines compiles unchanged and reads as untracked.
        IFrame frame = new UntrackedFrame();
        IAudioBuffer buffer = new UntrackedBuffer();

        Assert.Equal(TimelineId.None, frame.Timeline);
        Assert.Equal(TimelineId.None, buffer.Timeline);
    }

    // ── Derived items ─────────────────────────────────────────────────

    [Theory]
    [InlineData(PixelFormat.Bgra32)]
    [InlineData(PixelFormat.Yuv420P)]
    [InlineData(PixelFormat.Nv12)]
    public void AClone_CarriesItsSourcesLineage(PixelFormat format)
    {
        using var source = DerivedItemConformance.VideoInput(format);

        using var clone = source.CloneCpu();

        DerivedItemConformance.AssertCarriesLineage(source, clone);
    }

    // ── Presentation ──────────────────────────────────────────────────

    [Fact]
    public void FramePresentedInfo_KeepsItsTwoMemberForm()
    {
        // Timeline is an init-only property, not a positional member, so code that constructs or
        // deconstructs the two-member form compiles and runs unchanged.
        var at = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var info = new FramePresentedInfo(TimeSpan.FromSeconds(1), at);

        var (pts, presentedAt) = info;

        Assert.Equal(TimeSpan.FromSeconds(1), pts);
        Assert.Equal(at, presentedAt);
        Assert.Equal(TimelineId.None, info.Timeline);
    }

    [Fact]
    public void FramePresentedInfo_CarriesTheTimeline_AndComparesOnIt()
    {
        var timeline = TimelineId.Next();
        var at = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

        var info = new FramePresentedInfo(TimeSpan.FromSeconds(1), at) { Timeline = timeline };

        Assert.Equal(timeline, info.Timeline);
        Assert.NotEqual(info with { Timeline = TimelineId.Next() }, info);
    }

    private sealed class UntrackedFrame : IFrame
    {
        public int Width => 1;
        public int Height => 1;
        public TimeSpan Timestamp => TimeSpan.Zero;
        public void Dispose() { }
    }

    private sealed class UntrackedBuffer : IAudioBuffer
    {
        public TimeSpan Timestamp => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public int SampleRate => 48_000;
        public int ChannelCount => 2;
        public int FrameCount => 0;
        public AudioSampleFormat SampleFormat => AudioSampleFormat.Int16;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;
        public IAudioBuffer AddRef() => this;
        public void Dispose() { }
    }
}
