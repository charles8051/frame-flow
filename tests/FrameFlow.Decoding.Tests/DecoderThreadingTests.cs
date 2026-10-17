using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// How many threads a video decoder opens with, and of which kind (#788). Pure: every combination
/// of hardware, codec support, requested count and processor count.
/// </summary>
public sealed class DecoderThreadingTests
{
    private static readonly CodecThreadSupport[] Supports =
    [
        .. Enumerable
            .Range(0, 8)
            .Select(bits => (CodecThreadSupport)bits),
    ];

    private static readonly int[] Requests = [0, 1, 2, 3, 8, 9, 64];

    private static readonly int[] Processors = [0, 1, 2, 4, 8, 9, 24, 128];

    public static TheoryData<int, int, int> Everything()
    {
        var data = new TheoryData<int, int, int>();
        foreach (var support in Supports)
            foreach (var requested in Requests)
                foreach (var processors in Processors)
                    data.Add((int)support, requested, processors);
        return data;
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public void AHardwareContextIsLeftAsAllocated(int supportBits, int requested, int processors) =>
        Assert.Null(DecoderThreading.For(hardware: true, (CodecThreadSupport)supportBits, requested, processors));

    [Theory]
    [MemberData(nameof(Everything))]
    public void ASoftwareContextAlwaysGetsSettings(int supportBits, int requested, int processors)
    {
        var support = (CodecThreadSupport)supportBits;
        var settings = DecoderThreading.For(hardware: false, support, requested, processors);

        Assert.NotNull(settings);
        Assert.True(settings.Value.Count >= 1);
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public void OneThreadUsesNoThreadingKind(int supportBits, int requested, int processors)
    {
        var support = (CodecThreadSupport)supportBits;
        var settings = DecoderThreading.For(hardware: false, support, requested, processors)!.Value;

        if (settings.Count == 1)
            Assert.Equal(DecoderThreadKinds.None, settings.Kinds);
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public void AnExplicitCountIsUsedAsGiven(int supportBits, int requested, int processors)
    {
        var support = (CodecThreadSupport)supportBits;
        if (requested == DecoderThreading.Automatic || support == CodecThreadSupport.None)
            return;

        Assert.Equal(requested, DecoderThreading.For(hardware: false, support, requested, processors)!.Value.Count);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(9, 8)]
    [InlineData(24, 8)]
    [InlineData(128, 8)]
    public void AutomaticFollowsTheProcessorsUpToTheCap(int processors, int expected)
    {
        var all = CodecThreadSupport.Frame | CodecThreadSupport.Slice;

        Assert.Equal(expected, DecoderThreading.For(false, all, DecoderThreading.Automatic, processors)!.Value.Count);
    }

    [Theory]
    [InlineData((int)(CodecThreadSupport.Frame), (int)(DecoderThreadKinds.Frame))]
    [InlineData((int)(CodecThreadSupport.Slice), (int)(DecoderThreadKinds.Slice))]
    [InlineData((int)(CodecThreadSupport.Frame | CodecThreadSupport.Slice), (int)(DecoderThreadKinds.Frame | DecoderThreadKinds.Slice))]
    [InlineData((int)(CodecThreadSupport.Other), (int)(DecoderThreadKinds.None))]
    [InlineData((int)(CodecThreadSupport.Frame | CodecThreadSupport.Other), (int)(DecoderThreadKinds.Frame))]
    [InlineData((int)(CodecThreadSupport.Slice | CodecThreadSupport.Other), (int)(DecoderThreadKinds.Slice))]
    [InlineData(
        (int)(CodecThreadSupport.Frame | CodecThreadSupport.Slice | CodecThreadSupport.Other),
        (int)(DecoderThreadKinds.Frame | DecoderThreadKinds.Slice)
    )]
    public void TheKindsAreTheOnesTheCodecSupports(int supportBits, int expectedBits)
    {
        var support = (CodecThreadSupport)supportBits;
        var expected = (DecoderThreadKinds)expectedBits;
        foreach (var requested in new[] { DecoderThreading.Automatic, 2, 16 })
        {
            var settings = DecoderThreading.For(false, support, requested, processorCount: 24)!.Value;
            Assert.Equal(expected, settings.Kinds);
            Assert.True(settings.Count > 1);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(24)]
    public void ACodecWithNoThreadingGetsOneThread(int requested) =>
        Assert.Equal(
            new DecoderThreadSettings(1, DecoderThreadKinds.None),
            DecoderThreading.For(false, CodecThreadSupport.None, requested, processorCount: 24)
        );

    [Fact]
    public void OneRequestedThreadIsOneThreadWhateverTheCodecSupports()
    {
        foreach (var support in Supports)
            Assert.Equal(
                new DecoderThreadSettings(1, DecoderThreadKinds.None),
                DecoderThreading.For(false, support, requested: 1, processorCount: 24)
            );
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ANegativeCountIsRefused(int requested)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DecoderThreading.For(false, CodecThreadSupport.Frame, requested, 8)
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DecoderThreading.For(true, CodecThreadSupport.Frame, requested, 8)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoDecoderOptions { DecoderThreads = requested });
    }

    [Fact]
    public void TheKindsAreFfmpegsThreadTypeValues()
    {
        Assert.Equal(FFmpeg.AutoGen.Abstractions.ffmpeg.FF_THREAD_FRAME, (int)DecoderThreadKinds.Frame);
        Assert.Equal(FFmpeg.AutoGen.Abstractions.ffmpeg.FF_THREAD_SLICE, (int)DecoderThreadKinds.Slice);
    }
}
