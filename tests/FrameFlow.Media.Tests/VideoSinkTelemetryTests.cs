using FrameFlow.Graph;
using FrameFlow.Media.Diagnostics;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Tests for <see cref="VideoSinkTelemetry"/> — the frame accounting shell extracted from
/// <c>AvaloniaVideoSink</c> and <c>SdlVideoSink</c>. FFmpeg-free: the slot is driven with a
/// stub <see cref="IVideoFrame"/> so supersede drops can be produced directly.
/// </summary>
/// <remarks>
/// The invariant worth pinning is the double-count boundary. A supersede is counted once, by
/// <see cref="LatestWinsFrameSlot"/>; the telemetry only meters it. An extra drop is a loss the
/// slot never sees, so the telemetry counts that one itself. Getting this wrong in either
/// direction makes <see cref="VideoSinkDiagnosticsSnapshot.FramesDropped"/> silently lie, which
/// is exactly the number the snapshot exists to report honestly.
/// </remarks>
public sealed class VideoSinkTelemetryTests
{
    private static VideoSinkMeters Meters() =>
        new("FrameFlow.Test.Sink", "frameflow.test.sink", "TestSink");

    private static (VideoSinkTelemetry Telemetry, LatestWinsFrameSlot Slot) Subject()
    {
        var slot = new LatestWinsFrameSlot();
        return (new VideoSinkTelemetry(Meters(), slot), slot);
    }

    private static TimeSpan Pts(int n) => TimeSpan.FromMilliseconds(n * 33);

    [Fact]
    public void FreshTelemetry_ReportsZeroCounts_AndNoStamp()
    {
        var (telemetry, _) = Subject();

        Assert.Equal(0, telemetry.PresentedCount);
        Assert.Equal(0, telemetry.DroppedCount);

        var snapshot = telemetry.Snapshot();

        Assert.Equal(0, snapshot.FramesPresented);
        Assert.Equal(0, snapshot.FramesDropped);
        Assert.Null(snapshot.LastPresentedPresentationTime);
        Assert.Null(snapshot.LastPresentedAtUtc);
    }

    [Fact]
    public void RecordPresented_CountsAndStampsTheLatestPts()
    {
        var (telemetry, _) = Subject();

        telemetry.RecordPresented(Pts(1));
        telemetry.RecordPresented(Pts(2));

        Assert.Equal(2, telemetry.PresentedCount);

        var snapshot = telemetry.Snapshot();

        Assert.Equal(2, snapshot.FramesPresented);
        Assert.Equal(Pts(2), snapshot.LastPresentedPresentationTime);
        Assert.NotNull(snapshot.LastPresentedAtUtc);
        Assert.Equal(DateTimeKind.Utc, snapshot.LastPresentedAtUtc!.Value.Kind);
    }

    [Fact]
    public void RecordPresented_WithZeroPts_StillStamps()
    {
        // TimeSpan.Zero is a real PTS (the first frame). The sentinel for "nothing presented
        // yet" is -1 ticks, so a zero-PTS frame must read back as 0, never as null.
        var (telemetry, _) = Subject();

        telemetry.RecordPresented(TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, telemetry.Snapshot().LastPresentedPresentationTime);
    }

    [Fact]
    public void SupersededDrop_IsCountedByTheSlot_NotTwice()
    {
        var (telemetry, slot) = Subject();

        Assert.False(slot.TrySet(new StubFrame()));
        Assert.True(slot.TrySet(new StubFrame())); // supersedes the first
        telemetry.RecordSupersededDrop();

        Assert.Equal(1, slot.Dropped);
        Assert.Equal(1, telemetry.DroppedCount);
        Assert.Equal(1, telemetry.Snapshot().FramesDropped);
    }

    [Fact]
    public void ExtraDrop_IsCountedByTheTelemetry_BecauseTheSlotCannotSeeIt()
    {
        var (telemetry, slot) = Subject();

        telemetry.RecordExtraDrop();

        Assert.Equal(0, slot.Dropped);
        Assert.Equal(1, telemetry.DroppedCount);
    }

    [Fact]
    public void BothDropKinds_Sum()
    {
        var (telemetry, slot) = Subject();

        Assert.False(slot.TrySet(new StubFrame()));
        Assert.True(slot.TrySet(new StubFrame()));
        telemetry.RecordSupersededDrop();

        telemetry.RecordExtraDrop();
        telemetry.RecordExtraDrop();

        Assert.Equal(3, telemetry.DroppedCount);
        Assert.Equal(3, telemetry.Snapshot().FramesDropped);
    }

    [Fact]
    public void PresentedAndDropped_AreIndependent()
    {
        var (telemetry, _) = Subject();

        telemetry.RecordPresented(Pts(1));
        telemetry.RecordExtraDrop();

        var snapshot = telemetry.Snapshot();

        Assert.Equal(1, snapshot.FramesPresented);
        Assert.Equal(1, snapshot.FramesDropped);
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(
            () => new VideoSinkTelemetry(null!, new LatestWinsFrameSlot())
        );
        Assert.Throws<ArgumentNullException>(() => new VideoSinkTelemetry(Meters(), null!));
    }

    [Theory]
    [InlineData("", "prefix", "sink")]
    [InlineData("meter", "", "sink")]
    [InlineData("meter", "prefix", "")]
    [InlineData(" ", "prefix", "sink")]
    public void Meters_RejectBlankNames(string meterName, string prefix, string sinkName)
    {
        Assert.Throws<ArgumentException>(
            () => new VideoSinkMeters(meterName, prefix, sinkName)
        );
    }

    private sealed class StubFrame : IVideoFrame
    {
        public int Width => 4;
        public int Height => 4;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(33);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() { }

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
