using FrameFlow.Media.Tests.Doubles;

namespace FrameFlow.Media.Tests;

/// <summary>
/// <see cref="PcmAudioBuffer.Create{TState}"/>: the one way to make a PCM buffer (ADR-0080,
/// decision 5). The fill writes up to the capacity and reports how many samples it wrote.
/// </summary>
public sealed class PcmAudioBlockTests
{
    private static PcmAudioBuffer Ramp(
        int capacity,
        int written,
        int sampleRate = 44_100,
        int channels = 2,
        TimeSpan pts = default,
        CountingArrayPool<short>? pool = null
    ) =>
        PcmAudioBuffer.Create(
            capacity,
            sampleRate,
            channels,
            pts,
            written,
            static (span, written) =>
            {
                for (int i = 0; i < written; i++)
                    span[i] = (short)((i + 1) * 10);
                return written;
            },
            pool
        );

    // ── Metadata ──────────────────────────────────────────────────────

    [Fact]
    public void Create_StoresTheBuffersMetadata()
    {
        using var block = Ramp(8, 6, sampleRate: 48_000, channels: 2, pts: TimeSpan.FromSeconds(1.5));

        Assert.Equal(48_000, block.SampleRate);
        Assert.Equal(2, block.Channels);
        Assert.Equal(TimeSpan.FromSeconds(1.5), block.PresentationTime);
        Assert.Equal(6, block.SampleCount);
        Assert.Equal(3, block.FrameCount);
    }

    // ── Samples ───────────────────────────────────────────────────────

    [Fact]
    public void Samples_AreTheOnesTheFillReportedWriting()
    {
        using var block = Ramp(capacity: 5, written: 3, channels: 1);

        Assert.Equal(new short[] { 10, 20, 30 }, block.Samples.ToArray());
    }

    [Fact]
    public void Samples_CanFillTheWholeCapacity()
    {
        using var block = Ramp(capacity: 3, written: 3, channels: 1);

        Assert.Equal(3, block.Samples.Length);
    }

    [Fact]
    public void AFillThatWritesNothing_MakesAnEmptyBuffer()
    {
        using var block = Ramp(capacity: 4, written: 0);

        Assert.Equal(0, block.SampleCount);
        Assert.True(block.Samples.IsEmpty);
    }

    [Fact]
    public void ZeroCapacity_IsValid_AndReturnsNothingToThePool()
    {
        var pool = new CountingArrayPool<short>();
        var block = Ramp(capacity: 0, written: 0, pool: pool);

        block.Dispose();

        Assert.Equal(0, block.SampleCount);
        Assert.Equal(0, pool.Returns);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void AFillReportingACountOutsideTheCapacity_Throws_AndReturnsTheStorage(int written)
    {
        var pool = new CountingArrayPool<short>();

        Assert.Throws<InvalidOperationException>(() =>
            PcmAudioBuffer.Create(4, 48_000, 2, TimeSpan.Zero, written, static (_, n) => n, pool)
        );

        Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public void Create_RejectsANegativeCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Ramp(capacity: -1, written: 0));
    }

    // ── Storage ───────────────────────────────────────────────────────

    [Fact]
    public void TheFinalRelease_ReturnsTheStorageOnce()
    {
        var pool = new CountingArrayPool<short>();
        var block = Ramp(4, 4, pool: pool);
        var shared = block.AddRef();

        block.Dispose();
        Assert.Equal(0, pool.Returns);

        shared.Dispose();
        Assert.Equal(1, pool.Rents);
        Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public void AFillThatThrows_ReturnsTheStorage_AndPropagatesItsException()
    {
        var pool = new CountingArrayPool<short>();
        var thrown = new InvalidOperationException("fill failed");

        var caught = Assert.Throws<InvalidOperationException>(() =>
            PcmAudioBuffer.Create(4, 48_000, 2, TimeSpan.Zero, thrown, static (_, ex) => throw ex, pool)
        );

        Assert.Same(thrown, caught);
        Assert.Equal(1, pool.Rents);
        Assert.Equal(1, pool.Returns);
    }

#if DEBUG
    [Fact]
    public void ReleasedStorage_ReadsAsTheReleaseFill_ThroughAViewKeptPastTheRelease()
    {
        var block = Ramp(4, 4, pool: new CountingArrayPool<short>());
        var kept = block.Samples;
        Assert.Equal(10, kept.Span[0]);

        block.Dispose();

        Assert.All(kept.ToArray(), s => Assert.Equal(PcmAudioBuffer.ReleasedFill, s));
    }
#endif
}
