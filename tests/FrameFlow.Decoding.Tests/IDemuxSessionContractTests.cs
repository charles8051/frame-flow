using FrameFlow.Decoding.Tests.Doubles;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests that exercise the <see cref="IDemuxSession"/> contract using
/// <see cref="FakeDemuxSession"/> as the implementation under test.
/// These tests validate the behavioral contract that all implementations must honor,
/// without requiring real FFmpeg binaries.
/// </summary>
public sealed class IDemuxSessionContractTests : IClassFixture<FfmpegBootstrapFixture>
{
    private static MediaInfo MakeMediaInfo(
        string container = "mp4",
        double durationSeconds = 10.0,
        VideoStreamInfo[]? video = null,
        AudioStreamInfo[]? audio = null
    )
    {
        return new MediaInfo(
            container,
            TimeSpan.FromSeconds(durationSeconds),
            video ?? [new VideoStreamInfo(0, "h264", 1920, 1080, 30.0)],
            audio ?? [new AudioStreamInfo(1, "aac", 44100, 2)]
        );
    }

    private static DemuxPacket MakeVideoPacket(
        int streamIndex = 0,
        double ptsSeconds = 0.0,
        bool keyFrame = true
    )
    {
        return new DemuxPacket(
            streamIndex,
            TimeSpan.FromSeconds(ptsSeconds),
            hasPts: true,
            TimeSpan.FromSeconds(ptsSeconds),
            hasDts: true,
            duration: TimeSpan.FromSeconds(1.0 / 30.0),
            data: [0x00, 0x01, 0x02],
            isKeyFrame: keyFrame
        );
    }

    // -------------------------------------------------------------------------
    // MediaInfo — availability after open
    // -------------------------------------------------------------------------

    [Fact]
    public void MediaInfo_IsAvailable_AfterOpen()
    {
        var info = MakeMediaInfo("mkv", 60.0);
        var session = new FakeDemuxSession(info);
        Assert.NotNull(session.MediaInfo);
    }

    [Fact]
    public void MediaInfo_ReturnsExpectedContainer()
    {
        var info = MakeMediaInfo("matroska");
        var session = new FakeDemuxSession(info);
        Assert.Equal("matroska", session.MediaInfo.ContainerName);
    }

    [Fact]
    public void MediaInfo_ReturnsExpectedDuration()
    {
        var info = MakeMediaInfo(durationSeconds: 123.456);
        var session = new FakeDemuxSession(info);
        Assert.Equal(TimeSpan.FromSeconds(123.456), session.MediaInfo.Duration);
    }

    [Fact]
    public void MediaInfo_ReturnsVideoStreams()
    {
        var video = new[] { new VideoStreamInfo(0, "h264", 1280, 720, 25.0) };
        var info = MakeMediaInfo(video: video);
        var session = new FakeDemuxSession(info);
        Assert.Single(session.MediaInfo.VideoStreams);
        Assert.Equal("h264", session.MediaInfo.VideoStreams[0].CodecName);
    }

    [Fact]
    public void MediaInfo_ReturnsAudioStreams()
    {
        var audio = new[] { new AudioStreamInfo(1, "aac", 48000, 2) };
        var info = MakeMediaInfo(audio: audio);
        var session = new FakeDemuxSession(info);
        Assert.Single(session.MediaInfo.AudioStreams);
        Assert.Equal("aac", session.MediaInfo.AudioStreams[0].CodecName);
    }

    // -------------------------------------------------------------------------
    // ReadPacketAsync — normal packet reading
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReadPacketAsync_ReturnsPacket_WhenPacketsAvailable()
    {
        var packet = MakeVideoPacket();
        var session = new FakeDemuxSession(MakeMediaInfo(), [packet]);

        var result = await session.ReadPacketAsync();

        Assert.NotNull(result);
        Assert.Equal(0, result.StreamIndex);
    }

    [Fact]
    public async Task ReadPacketAsync_ReturnsNull_AtEndOfStream()
    {
        var session = new FakeDemuxSession(MakeMediaInfo());

        var result = await session.ReadPacketAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadPacketAsync_ReturnsPacketsInOrder()
    {
        var p1 = MakeVideoPacket(ptsSeconds: 0.0);
        var p2 = MakeVideoPacket(ptsSeconds: 0.033);
        var p3 = MakeVideoPacket(ptsSeconds: 0.066);
        var session = new FakeDemuxSession(MakeMediaInfo(), [p1, p2, p3]);

        var r1 = await session.ReadPacketAsync();
        var r2 = await session.ReadPacketAsync();
        var r3 = await session.ReadPacketAsync();
        var eof = await session.ReadPacketAsync();

        Assert.Equal(TimeSpan.FromSeconds(0.0), r1!.Pts);
        Assert.Equal(TimeSpan.FromSeconds(0.033), r2!.Pts);
        Assert.Equal(TimeSpan.FromSeconds(0.066), r3!.Pts);
        Assert.Null(eof);
    }

    [Fact]
    public async Task ReadPacketAsync_PacketCarriesKeyFrameFlag()
    {
        var keyPacket = MakeVideoPacket(keyFrame: true);
        var session = new FakeDemuxSession(MakeMediaInfo(), [keyPacket]);

        var result = await session.ReadPacketAsync();

        Assert.NotNull(result);
        Assert.True(result.IsKeyFrame);
    }

    [Fact]
    public async Task ReadPacketAsync_PacketWithMissingPts_HasHasPtsFalse()
    {
        var packet = new DemuxPacket(
            streamIndex: 0,
            pts: TimeSpan.Zero,
            hasPts: false,
            dts: TimeSpan.FromSeconds(1.0),
            hasDts: true,
            duration: TimeSpan.FromSeconds(0.033),
            data: [0x00],
            isKeyFrame: false
        );

        var session = new FakeDemuxSession(MakeMediaInfo(), [packet]);
        var result = await session.ReadPacketAsync();

        Assert.NotNull(result);
        Assert.False(result.HasPts);
        Assert.Equal(TimeSpan.Zero, result.Pts);
    }

    // -------------------------------------------------------------------------
    // ReadPacketAsync — cancellation behavior (ADR-0013)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReadPacketAsync_ThrowsOperationCanceledException_WhenTokenCancelled()
    {
        var session = new FakeDemuxSession(MakeMediaInfo(), [MakeVideoPacket()]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            session.ReadPacketAsync(cts.Token).AsTask()
        );
    }

    [Fact]
    public async Task ReadPacketAsync_SessionRemainsUsable_AfterCancellation()
    {
        var packet = MakeVideoPacket();
        var session = new FakeDemuxSession(MakeMediaInfo(), [packet]);

        // First read: cancel
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await session.ReadPacketAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        // Second read: should still work
        var result = await session.ReadPacketAsync(CancellationToken.None);
        Assert.NotNull(result);
    }

    // -------------------------------------------------------------------------
    // SeekAsync — normal seek behavior
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SeekAsync_RecordsSeekPosition()
    {
        var session = new FakeDemuxSession(MakeMediaInfo());
        var target = TimeSpan.FromSeconds(30.0);

        await session.SeekAsync(target);

        Assert.Single(session.SeekHistory);
        Assert.Equal(target, session.SeekHistory[0]);
    }

    [Fact]
    public async Task SeekAsync_MultipleSeeks_AllRecorded()
    {
        var session = new FakeDemuxSession(MakeMediaInfo());

        await session.SeekAsync(TimeSpan.FromSeconds(10));
        await session.SeekAsync(TimeSpan.FromSeconds(20));
        await session.SeekAsync(TimeSpan.Zero);

        Assert.Equal(3, session.SeekHistory.Count);
    }

    [Fact]
    public async Task SeekAsync_ThrowsOperationCanceledException_WhenTokenCancelled()
    {
        var session = new FakeDemuxSession(MakeMediaInfo());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            session.SeekAsync(TimeSpan.FromSeconds(5), cts.Token).AsTask()
        );
    }

    [Fact]
    public async Task SeekAsync_DoesNotThrow_ForZeroPosition()
    {
        var session = new FakeDemuxSession(MakeMediaInfo());

        // Should not throw
        await session.SeekAsync(TimeSpan.Zero);

        Assert.Single(session.SeekHistory);
    }

    // -------------------------------------------------------------------------
    // Disposal behavior
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_SetsIsDisposed()
    {
        var session = new FakeDemuxSession(MakeMediaInfo());
        Assert.False(session.IsDisposed);

        await session.DisposeAsync();

        Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var session = new FakeDemuxSession(MakeMediaInfo());

        await session.DisposeAsync();
        // Second dispose must not throw
        await session.DisposeAsync();

        Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task ReadPacketAsync_ThrowsObjectDisposedException_AfterDisposal()
    {
        var session = new FakeDemuxSession(MakeMediaInfo());
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadPacketAsync().AsTask());
    }

    [Fact]
    public async Task SeekAsync_ThrowsObjectDisposedException_AfterDisposal()
    {
        var session = new FakeDemuxSession(MakeMediaInfo());
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.SeekAsync(TimeSpan.Zero).AsTask()
        );
    }
}
