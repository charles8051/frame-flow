using FrameFlow.Decoding.Tests.Doubles;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Contract tests for <see cref="IAudioDecoder"/> verified using <see cref="FakeAudioDecoder"/>.
/// These tests exercise behavioral expectations that all <see cref="IAudioDecoder"/> implementations
/// must satisfy, without requiring real FFmpeg binaries.
/// </summary>
public sealed class IAudioDecoderContractTests : IClassFixture<FfmpegBootstrapFixture>
{
    // -----------------------------------------------------------------------
    // DecodeAsync — yields blocks in order
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DecodeAsync_YieldsBlocksInOrder()
    {
        var b1 = FakeAudioDecoder.MakeBlock(pts: TimeSpan.FromSeconds(0.0));
        var b2 = FakeAudioDecoder.MakeBlock(pts: TimeSpan.FromSeconds(0.1));
        var b3 = FakeAudioDecoder.MakeBlock(pts: TimeSpan.FromSeconds(0.2));
        var decoder = new FakeAudioDecoder([b1, b2, b3]);

        var received = new List<TimeSpan>();
        await foreach (var block in decoder.DecodeAsync())
        {
            received.Add(block.PresentationTime);
            block.Dispose();
        }

        Assert.Equal(3, received.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.0), received[0]);
        Assert.Equal(TimeSpan.FromSeconds(0.1), received[1]);
        Assert.Equal(TimeSpan.FromSeconds(0.2), received[2]);
    }

    [Fact]
    public async Task DecodeAsync_YieldsNoBlocks_WhenEmpty()
    {
        var decoder = new FakeAudioDecoder([]);
        int count = 0;

        await foreach (var block in decoder.DecodeAsync())
        {
            count++;
            block.Dispose();
        }

        Assert.Equal(0, count);
    }

    // -----------------------------------------------------------------------
    // DecodeAsync — block ownership and disposal
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DecodeAsync_CallerOwnsEachBlock()
    {
        var block = FakeAudioDecoder.MakeBlock(sampleCount: 512);
        var decoder = new FakeAudioDecoder([block]);

        PcmAudioBuffer? received = null;
        await foreach (var b in decoder.DecodeAsync())
        {
            received = b;
            // Do NOT dispose yet — verify the caller owns it
        }

        Assert.NotNull(received);
        // Samples must still be accessible before disposal
        Assert.Equal(512 * 2, received.SampleCount); // 2 channels
        // Disposing at caller's discretion must not throw
        received.Dispose();
    }

    [Fact]
    public async Task DecodeAsync_EachBlock_HasNonNegativeSampleCount()
    {
        var decoder = new FakeAudioDecoder([
            FakeAudioDecoder.MakeBlock(sampleCount: 256),
            FakeAudioDecoder.MakeBlock(sampleCount: 0),
            FakeAudioDecoder.MakeBlock(sampleCount: 1024),
        ]);

        await foreach (var block in decoder.DecodeAsync())
        {
            Assert.True(block.SampleCount >= 0);
            block.Dispose();
        }
    }

    [Fact]
    public async Task DecodeAsync_EachBlock_HasPositiveSampleRate()
    {
        var decoder = new FakeAudioDecoder([
            FakeAudioDecoder.MakeBlock(sampleRate: 44_100),
            FakeAudioDecoder.MakeBlock(sampleRate: 48_000),
        ]);

        await foreach (var block in decoder.DecodeAsync())
        {
            Assert.True(block.SampleRate > 0);
            block.Dispose();
        }
    }

    [Fact]
    public async Task DecodeAsync_EachBlock_HasPositiveChannelCount()
    {
        var decoder = new FakeAudioDecoder([
            FakeAudioDecoder.MakeBlock(channels: 1),
            FakeAudioDecoder.MakeBlock(channels: 2),
        ]);

        await foreach (var block in decoder.DecodeAsync())
        {
            Assert.True(block.Channels > 0);
            block.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // DecodeAsync — cancellation (ADR-0013)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DecodeAsync_ObservesCancellation_AndTerminatesCleanly()
    {
        var blocks = Enumerable
            .Range(0, 100)
            .Select(i => FakeAudioDecoder.MakeBlock(pts: TimeSpan.FromMilliseconds(i * 20)))
            .ToList();
        var decoder = new FakeAudioDecoder(blocks);

        using var cts = new CancellationTokenSource();
        int count = 0;

        try
        {
            await foreach (var block in decoder.DecodeAsync(cts.Token))
            {
                count++;
                block.Dispose();
                if (count == 5)
                    cts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected — cancellation is normal flow per ADR-0013
        }

        // We should have received at most 6 blocks (5 + possibly 1 in-flight)
        Assert.True(count <= 6, $"Expected at most 6 blocks, got {count}");

        // Dispose remaining blocks that were pre-created
        foreach (var b in blocks.Skip(count))
            b.Dispose();
    }

    // -----------------------------------------------------------------------
    // DisposeAsync — lifecycle contract
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_CanBeCalledOnceWithoutThrowing()
    {
        var decoder = new FakeAudioDecoder();
        var ex = await Record.ExceptionAsync(() => decoder.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var decoder = new FakeAudioDecoder();

        await decoder.DisposeAsync();
        // Second dispose must not throw
        var ex = await Record.ExceptionAsync(() => decoder.DisposeAsync().AsTask());

        Assert.Null(ex);
    }

    [Fact]
    public async Task DecodeAsync_ThrowsObjectDisposedException_AfterDisposal()
    {
        var decoder = new FakeAudioDecoder();
        await decoder.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in decoder.DecodeAsync())
            {
                // should not be reached
            }
        });
    }

    // -----------------------------------------------------------------------
    // PcmAudioBuffer output format — normalisation contract (ADR-0012)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DecodeAsync_OutputBlocks_HaveSamplesAccessibleViaSamplesProperty()
    {
        var block = FakeAudioDecoder.MakeBlock(sampleCount: 128, channels: 2);
        var decoder = new FakeAudioDecoder([block]);

        await foreach (var b in decoder.DecodeAsync())
        {
            // Samples property returns a correctly sliced view
            Assert.Equal(b.SampleCount, b.Samples.Length);
            b.Dispose();
        }
    }

    [Fact]
    public async Task DecodeAsync_StereoBlock_SampleCountIsSamplesPerChannelTimesTwo()
    {
        int samplesPerChannel = 1024;
        // MakeBlock sampleCount parameter is per-channel; total = sampleCount × channels
        var block = FakeAudioDecoder.MakeBlock(sampleCount: samplesPerChannel, channels: 2);
        var decoder = new FakeAudioDecoder([block]);

        await foreach (var b in decoder.DecodeAsync())
        {
            Assert.Equal(samplesPerChannel * 2, b.SampleCount);
            b.Dispose();
        }
    }
}
