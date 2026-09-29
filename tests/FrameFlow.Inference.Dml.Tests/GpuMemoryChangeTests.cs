namespace FrameFlow.Inference.Dml.Tests;

/// <summary>The comparison of two GPU memory snapshots (#503). Pure.</summary>
public sealed class GpuMemoryChangeTests
{
    private const ulong Adapter = 0x0000_0001_0000_ABCD;
    private const long MiB = 1024 * 1024;

    public static TheoryData<string, GpuMemorySegment, GpuMemorySegment, GpuMemorySegment, GpuMemorySegment, long, long> Cases => new()
    {
        {
            "a session that fits",
            new(12_000 * MiB, 1_000 * MiB), new(16_000 * MiB, 50 * MiB),
            new(12_000 * MiB, 1_640 * MiB), new(16_000 * MiB, 50 * MiB),
            640 * MiB, 0
        },
        {
            "a session opened past the local budget",
            new(10_600 * MiB, 10_300 * MiB), new(48_000 * MiB, 190 * MiB),
            new(10_700 * MiB, 11_300 * MiB), new(48_000 * MiB, 1_230 * MiB),
            1_000 * MiB, 1_040 * MiB
        },
        {
            "memory released",
            new(12_000 * MiB, 3_000 * MiB), new(16_000 * MiB, 200 * MiB),
            new(12_000 * MiB, 2_000 * MiB), new(16_000 * MiB, 0),
            -1_000 * MiB, -200 * MiB
        },
        {
            "the budget moving with usage unchanged",
            new(12_000 * MiB, 1_000 * MiB), new(16_000 * MiB, 50 * MiB),
            new(9_000 * MiB, 1_000 * MiB), new(15_000 * MiB, 50 * MiB),
            0, 0
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheChange_IsUsageAfterLessUsageBefore(
        string because,
        GpuMemorySegment localBefore,
        GpuMemorySegment nonLocalBefore,
        GpuMemorySegment localAfter,
        GpuMemorySegment nonLocalAfter,
        long localChange,
        long nonLocalChange)
    {
        var change = GpuMemoryChange.Between(
            new GpuMemorySnapshot(Adapter, localBefore, nonLocalBefore),
            new GpuMemorySnapshot(Adapter, localAfter, nonLocalAfter));

        Assert.True(
            new GpuMemoryChange(localChange, nonLocalChange) == change,
            $"{because}: expected ({localChange}, {nonLocalChange}), got ({change.LocalBytes}, {change.NonLocalBytes}).");
    }

    [Fact]
    public void SnapshotsOfDifferentAdapters_AreRefused()
    {
        var segment = new GpuMemorySegment(12_000 * MiB, 1_000 * MiB);

        var error = Assert.Throws<ArgumentException>(
            () => GpuMemoryChange.Between(new(Adapter, segment, segment), new(Adapter + 1, segment, segment)));

        Assert.Equal("after", error.ParamName);
    }
}
