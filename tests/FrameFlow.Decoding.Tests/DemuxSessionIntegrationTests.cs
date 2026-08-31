namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Integration tests for <see cref="DemuxSession"/> and <see cref="DemuxSessionFactory"/>
/// that require real FFmpeg shared libraries and test corpus files.
/// All tests use <see cref="RequiresFfmpegAndCorpusFactAttribute"/> to skip cleanly when
/// the environment does not have FFmpeg or corpus files available.
/// The <see cref="FfmpegBootstrapFixture"/> bootstraps FFmpeg before any test runs so
/// that P/Invoke calls can resolve the shared libraries.
/// </summary>
public sealed class DemuxSessionIntegrationTests : IClassFixture<FfmpegBootstrapFixture>
{
    // When a corpus file is not present the test returns early.
    // The RequiresFfmpegAndCorpusFact attribute already skips when the whole corpus
    // is unavailable; per-file guards handle missing individual files gracefully.

    public DemuxSessionIntegrationTests(FfmpegBootstrapFixture _) { }

    private static string? Corpus(string name) => TestEnvironment.GetCorpusFile(name);

    // -------------------------------------------------------------------------
    // MediaInfo — population from real files
    // -------------------------------------------------------------------------

    [RequiresFfmpegAndCorpusFact]
    public async Task OpenAsync_PopulatesMediaInfo_ForAV_H264_AAC_MP4()
    {
        var file = Corpus("test-av-h264-aac.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        Assert.NotNull(session.MediaInfo);
        Assert.True(
            session.MediaInfo.Duration > TimeSpan.Zero,
            $"Expected duration > 0 but got {session.MediaInfo.Duration}"
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OpenAsync_PopulatesVideoStreams_ForVideoFile()
    {
        var file = Corpus("test-av-h264-aac.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        Assert.NotEmpty(session.MediaInfo.VideoStreams);
        var video = session.MediaInfo.VideoStreams[0];
        Assert.True(video.Width > 0, $"Expected width > 0 but got {video.Width}");
        Assert.True(video.Height > 0, $"Expected height > 0 but got {video.Height}");
        Assert.False(string.IsNullOrEmpty(video.CodecName), "CodecName must not be empty");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OpenAsync_PopulatesAudioStreams_ForAVFile()
    {
        var file = Corpus("test-av-h264-aac.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        Assert.NotEmpty(session.MediaInfo.AudioStreams);
        var audio = session.MediaInfo.AudioStreams[0];
        Assert.True(audio.SampleRate > 0, $"Expected sample rate > 0 but got {audio.SampleRate}");
        Assert.True(audio.Channels > 0, $"Expected channels > 0 but got {audio.Channels}");
        Assert.False(string.IsNullOrEmpty(audio.CodecName), "CodecName must not be empty");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OpenAsync_VideoOnlyFile_HasNoAudioStreams()
    {
        var file = Corpus("test-video-only.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        Assert.NotEmpty(session.MediaInfo.VideoStreams);
        Assert.Empty(session.MediaInfo.AudioStreams);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OpenAsync_AudioOnlyFile_HasNoVideoStreams()
    {
        var file = Corpus("test-audio-only.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        Assert.Empty(session.MediaInfo.VideoStreams);
        Assert.NotEmpty(session.MediaInfo.AudioStreams);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OpenAsync_1080p_VideoStream_HasCorrectDimensions()
    {
        var file = Corpus("test-1080p-h264-aac.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        var video = session.MediaInfo.VideoStreams[0];
        Assert.Equal(1920, video.Width);
        Assert.Equal(1080, video.Height);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OpenAsync_H264File_ReportsH264CodecName()
    {
        var file = Corpus("test-video-h264-yuv420p.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        Assert.NotEmpty(session.MediaInfo.VideoStreams);
        Assert.Equal("h264", session.MediaInfo.VideoStreams[0].CodecName);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OpenAsync_AacFile_ReportsAacCodecName()
    {
        var file = Corpus("test-audio-aac.m4a");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        Assert.NotEmpty(session.MediaInfo.AudioStreams);
        Assert.Equal("aac", session.MediaInfo.AudioStreams[0].CodecName);
    }

    // -------------------------------------------------------------------------
    // Packet reading
    // -------------------------------------------------------------------------

    [RequiresFfmpegAndCorpusFact]
    public async Task ReadPacketAsync_ReturnsPackets_UntilEof()
    {
        var file = Corpus("test-subsecond.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        int count = 0;
        DemuxPacket? packet;
        while ((packet = await session.ReadPacketAsync()) is not null)
        {
            Assert.True(packet.StreamIndex >= 0);
            Assert.NotNull(packet.Data);
            count++;
        }

        Assert.True(count > 0, "Expected at least one packet from a valid media file");
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ReadPacketAsync_PacketsHaveValidStreamIndices()
    {
        var file = Corpus("test-av-h264-aac.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        var knownIndices = session
            .MediaInfo.VideoStreams.Select(v => v.StreamIndex)
            .Concat(session.MediaInfo.AudioStreams.Select(a => a.StreamIndex))
            .ToHashSet();

        int count = 0;
        DemuxPacket? packet;
        while ((packet = await session.ReadPacketAsync()) is not null && count < 50)
        {
            Assert.Contains(packet.StreamIndex, knownIndices);
            count++;
        }

        Assert.True(count > 0);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ReadPacketAsync_VideoPackets_HaveNonNegativePts()
    {
        var file = Corpus("test-av-h264-aac.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        int videoStreamIndex = session.MediaInfo.VideoStreams[0].StreamIndex;
        int videoPacketsRead = 0;

        DemuxPacket? packet;
        while ((packet = await session.ReadPacketAsync()) is not null)
        {
            if (packet.StreamIndex != videoStreamIndex)
                continue;

            videoPacketsRead++;
            if (packet.HasPts)
            {
                Assert.True(
                    packet.Pts >= TimeSpan.Zero,
                    $"Video PTS should be >= 0 but got {packet.Pts}"
                );
                Assert.True(
                    packet.Pts < TimeSpan.FromHours(1),
                    $"Video PTS unexpectedly large: {packet.Pts}"
                );
            }

            if (videoPacketsRead >= 20)
                break;
        }

        Assert.True(videoPacketsRead > 0);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ReadPacketAsync_FirstVideoPacket_IsKeyFrame()
    {
        var file = Corpus("test-video-h264-yuv420p.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        int videoStreamIndex = session.MediaInfo.VideoStreams[0].StreamIndex;

        DemuxPacket? packet;
        DemuxPacket? firstVideoPacket = null;
        while ((packet = await session.ReadPacketAsync()) is not null)
        {
            if (packet.StreamIndex != videoStreamIndex)
                continue;

            firstVideoPacket = packet;
            break;
        }

        Assert.NotNull(firstVideoPacket);
        Assert.True(
            firstVideoPacket.IsKeyFrame,
            "The first video packet from the start of a file should be a key frame"
        );
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ReadPacketAsync_PacketDataIsNonEmpty_ForVideoPackets()
    {
        var file = Corpus("test-video-h264-yuv420p.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        int videoStreamIndex = session.MediaInfo.VideoStreams[0].StreamIndex;

        DemuxPacket? packet;
        while ((packet = await session.ReadPacketAsync()) is not null)
        {
            if (packet.StreamIndex != videoStreamIndex)
                continue;

            Assert.NotEmpty(packet.Data);
            break;
        }

        Assert.NotNull(packet);
    }

    // -------------------------------------------------------------------------
    // Seek behavior
    // -------------------------------------------------------------------------

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekAsync_ToStart_DoesNotThrow()
    {
        var file = Corpus("test-av-h264-aac.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        await session.SeekAsync(TimeSpan.Zero);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekAsync_AllowsReadingPacketsAfterSeek()
    {
        var file = Corpus("test-av-h264-aac.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));

        await session.SeekAsync(TimeSpan.Zero);

        int count = 0;
        DemuxPacket? packet;
        while ((packet = await session.ReadPacketAsync()) is not null && count < 10)
            count++;

        Assert.True(count > 0, "Should be able to read packets after seeking to start");
    }

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    [RequiresFfmpegAndCorpusFact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var file = Corpus("test-subsecond.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        var session = await factory.OpenAsync(MediaSource.FromFile(file));

        await session.DisposeAsync();
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var file = Corpus("test-subsecond.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        var session = await factory.OpenAsync(MediaSource.FromFile(file));

        await session.DisposeAsync();
        await session.DisposeAsync(); // Must not throw on second call
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ReadPacketAsync_ThrowsObjectDisposedException_AfterDispose()
    {
        var file = Corpus("test-subsecond.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        var session = await factory.OpenAsync(MediaSource.FromFile(file));

        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadPacketAsync().AsTask());
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekAsync_ThrowsObjectDisposedException_AfterDispose()
    {
        var file = Corpus("test-subsecond.mp4");
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        var session = await factory.OpenAsync(MediaSource.FromFile(file));

        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.SeekAsync(TimeSpan.Zero).AsTask()
        );
    }
}
