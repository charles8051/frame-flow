using System.Buffers;
using FrameFlow.Media.Tests.Doubles;

namespace FrameFlow.Media.Tests;

public sealed class PcmAudioBlockTests
{
    // -----------------------------------------------------------------------
    // Construction — property storage
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_StoresSampleRate()
    {
        using var owner = FakeMemoryOwner<short>.OfLength(0);
        using var block = new PcmAudioBuffer(
            owner,
            sampleCount: 0,
            sampleRate: 44100,
            channels: 2,
            TimeSpan.Zero
        );
        Assert.Equal(44100, block.SampleRate);
    }

    [Fact]
    public void Constructor_StoresChannels()
    {
        using var owner = FakeMemoryOwner<short>.OfLength(0);
        using var block = new PcmAudioBuffer(
            owner,
            sampleCount: 0,
            sampleRate: 44100,
            channels: 2,
            TimeSpan.Zero
        );
        Assert.Equal(2, block.Channels);
    }

    [Fact]
    public void Constructor_StoresPresentationTime()
    {
        var pts = TimeSpan.FromSeconds(1.5);
        using var owner = FakeMemoryOwner<short>.OfLength(0);
        using var block = new PcmAudioBuffer(
            owner,
            sampleCount: 0,
            sampleRate: 44100,
            channels: 2,
            pts
        );
        Assert.Equal(pts, block.PresentationTime);
    }

    [Fact]
    public void Constructor_StoresSampleCount()
    {
        using var owner = FakeMemoryOwner<short>.OfLength(8);
        using var block = new PcmAudioBuffer(
            owner,
            sampleCount: 6,
            sampleRate: 44100,
            channels: 2,
            TimeSpan.Zero
        );
        Assert.Equal(6, block.SampleCount);
    }

    // -----------------------------------------------------------------------
    // Samples property — sliced view
    // -----------------------------------------------------------------------

    [Fact]
    public void Samples_ReturnsSlicedToSampleCount()
    {
        var data = new short[] { 10, 20, 30, 40, 50 };
        var owner = FakeMemoryOwner<short>.FromArray(data);
        using var block = new PcmAudioBuffer(
            owner,
            sampleCount: 3,
            sampleRate: 44100,
            channels: 1,
            TimeSpan.Zero
        );

        var samples = block.Samples;

        Assert.Equal(3, samples.Length);
        Assert.Equal(10, samples.Span[0]);
        Assert.Equal(20, samples.Span[1]);
        Assert.Equal(30, samples.Span[2]);
    }

    [Fact]
    public void Samples_WhenSampleCountEqualsBufferLength_ReturnsFullBuffer()
    {
        var data = new short[] { 1, 2, 3 };
        var owner = FakeMemoryOwner<short>.FromArray(data);
        using var block = new PcmAudioBuffer(
            owner,
            sampleCount: 3,
            sampleRate: 48000,
            channels: 1,
            TimeSpan.Zero
        );

        Assert.Equal(3, block.Samples.Length);
    }

    [Fact]
    public void Samples_WhenSampleCountIsZero_ReturnsEmpty()
    {
        using var owner = FakeMemoryOwner<short>.OfLength(4);
        using var block = new PcmAudioBuffer(
            owner,
            sampleCount: 0,
            sampleRate: 44100,
            channels: 1,
            TimeSpan.Zero
        );

        Assert.Equal(0, block.Samples.Length);
    }

    // -----------------------------------------------------------------------
    // Ownership and disposal
    // -----------------------------------------------------------------------

    [Fact]
    public void Dispose_DisposesUnderlyingMemoryOwner()
    {
        var owner = FakeMemoryOwner<short>.OfLength(4);
        var block = new PcmAudioBuffer(
            owner,
            sampleCount: 4,
            sampleRate: 44100,
            channels: 2,
            TimeSpan.Zero
        );

        block.Dispose();

        Assert.True(owner.IsDisposed);
    }

    [Fact]
    public void Dispose_CallsUnderlyingOwnerExactlyOnce()
    {
        var owner = FakeMemoryOwner<short>.OfLength(4);
        var block = new PcmAudioBuffer(
            owner,
            sampleCount: 4,
            sampleRate: 44100,
            channels: 2,
            TimeSpan.Zero
        );

        block.Dispose();

        Assert.Equal(1, owner.DisposeCallCount);
    }

    [Fact]
    public void SampleData_ExposesUnderlyingOwner()
    {
        var owner = FakeMemoryOwner<short>.OfLength(4);
        using var block = new PcmAudioBuffer(
            owner,
            sampleCount: 4,
            sampleRate: 44100,
            channels: 2,
            TimeSpan.Zero
        );

        Assert.Same(owner, block.SampleData);
    }

    // -----------------------------------------------------------------------
    // Edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void ZeroSampleCount_IsValid()
    {
        using var owner = FakeMemoryOwner<short>.OfLength(0);
        var ex = Record.Exception(() =>
        {
            using var block = new PcmAudioBuffer(
                owner,
                sampleCount: 0,
                sampleRate: 44100,
                channels: 2,
                TimeSpan.Zero
            );
        });
        Assert.Null(ex);
    }

    [Fact]
    public void ZeroPresentationTime_IsValid()
    {
        using var owner = FakeMemoryOwner<short>.OfLength(0);
        using var block = new PcmAudioBuffer(
            owner,
            sampleCount: 0,
            sampleRate: 44100,
            channels: 2,
            TimeSpan.Zero
        );
        Assert.Equal(TimeSpan.Zero, block.PresentationTime);
    }
}
