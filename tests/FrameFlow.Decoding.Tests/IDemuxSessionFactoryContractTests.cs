using FrameFlow.Decoding.Tests.Doubles;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for the <see cref="IDemuxSessionFactory"/> contract using
/// <see cref="FakeDemuxSessionFactory"/> as the implementation under test.
/// </summary>
public sealed class IDemuxSessionFactoryContractTests : IClassFixture<FfmpegBootstrapFixture>
{
    private static MediaInfo MakeMediaInfo() =>
        new MediaInfo("mp4", TimeSpan.FromSeconds(10), [], []);

    // -------------------------------------------------------------------------
    // OpenAsync — successful open
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OpenAsync_ReturnsSession_WhenSessionQueued()
    {
        var factory = new FakeDemuxSessionFactory();
        var expected = new FakeDemuxSession(MakeMediaInfo());
        factory.EnqueueSession(expected);

        var source = MediaSource.FromFile(@"C:\fake\video.mp4");
        var session = await factory.OpenAsync(source);

        Assert.NotNull(session);
        Assert.Same(expected, session);
    }

    [Fact]
    public async Task OpenAsync_RecordsOpenedSource()
    {
        var factory = new FakeDemuxSessionFactory();
        factory.EnqueueSession(new FakeDemuxSession(MakeMediaInfo()));

        var source = MediaSource.FromFile(@"C:\fake\video.mp4");
        await factory.OpenAsync(source);

        Assert.Single(factory.OpenHistory);
        Assert.Same(source, factory.OpenHistory[0]);
    }

    [Fact]
    public async Task OpenAsync_CanOpenMultipleSources_Sequentially()
    {
        var factory = new FakeDemuxSessionFactory();
        factory.EnqueueSession(new FakeDemuxSession(MakeMediaInfo()));
        factory.EnqueueSession(new FakeDemuxSession(MakeMediaInfo()));

        var s1 = MediaSource.FromFile(@"C:\fake\a.mp4");
        var s2 = MediaSource.FromFile(@"C:\fake\b.mp4");

        await factory.OpenAsync(s1);
        await factory.OpenAsync(s2);

        Assert.Equal(2, factory.OpenHistory.Count);
    }

    // -------------------------------------------------------------------------
    // OpenAsync — error cases
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OpenAsync_ThrowsInvalidOperationException_OnError()
    {
        var factory = new FakeDemuxSessionFactory();
        factory.SetThrowOnOpen("Could not open file");

        var source = MediaSource.FromFile(@"C:\fake\missing.mp4");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.OpenAsync(source).AsTask()
        );
    }

    [Fact]
    public async Task OpenAsync_ThrowsArgumentNullException_ForNullSource()
    {
        var factory = new FakeDemuxSessionFactory();

        await Assert.ThrowsAsync<ArgumentNullException>(() => factory.OpenAsync(null!).AsTask());
    }

    // -------------------------------------------------------------------------
    // Cancellation behavior (ADR-0013)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OpenAsync_ThrowsOperationCanceledException_WhenTokenCancelled()
    {
        var factory = new FakeDemuxSessionFactory();
        factory.EnqueueSession(new FakeDemuxSession(MakeMediaInfo()));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var source = MediaSource.FromFile(@"C:\fake\video.mp4");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            factory.OpenAsync(source, cts.Token).AsTask()
        );
    }
}
