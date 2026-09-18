using FrameFlow.Media;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Coverage for the video-format announcement (#287).
/// </summary>
/// <remarks>
/// <see cref="IVideoSink.OnFormatChangedAsync"/> was documented as the call the pipeline makes
/// when the format changes, and nothing made it. A sink that sizes its surface from the
/// announcement kept whatever geometry it had, so an item of a different size showed the
/// previous item's picture.
/// </remarks>
public sealed class VideoFormatAnnouncerTests
{
    private static readonly VideoFormatInfo Hd = new(1920, 1080, PixelFormat.Bgra32);
    private static readonly VideoFormatInfo Small = new(320, 240, PixelFormat.Bgra32);

    // ── The decision ────────────────────────────────────────────────────

    [Fact]
    public void TheFirstFormatIsAlwaysAnnounced() =>
        Assert.True(VideoFormatAnnouncer.ShouldAnnounce(announced: null, Hd));

    [Fact]
    public void ADifferentFormatIsAnnounced() =>
        Assert.True(VideoFormatAnnouncer.ShouldAnnounce(Hd, Small));

    [Fact]
    public void TheSameFormatIsNotAnnouncedTwice() =>
        Assert.False(VideoFormatAnnouncer.ShouldAnnounce(Hd, Hd with { }));

    [Fact]
    public void OnlyThePixelFormatChanging_IsStillAChange()
    {
        // A sink may hold a surface whose layout depends on it, so it is part of the shape
        // rather than a detail of it.
        var nv12 = Hd with { Format = PixelFormat.Nv12 };

        Assert.True(VideoFormatAnnouncer.ShouldAnnounce(Hd, nv12));
    }

    // ── The shell ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheFormatIsTakenFromTheFrame()
    {
        // Not from the stream's metadata: the decode path converts, so what the stream says
        // and what the sink is handed are different things, and only one of them is what a
        // surface has to match.
        var sink = new RecordingSink();
        var announcer = new VideoFormatAnnouncer();

        await announcer.AnnounceForAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);

        Assert.Equal([Small], sink.Announcements);
    }

    [Fact]
    public async Task AnUnchangedFormatIsAnnouncedOnce()
    {
        var sink = new RecordingSink();
        var announcer = new VideoFormatAnnouncer();

        for (var i = 0; i < 5; i++)
        {
            await announcer.AnnounceForAsync(
                sink,
                new StubFrame(1920, 1080, PixelFormat.Bgra32),
                default
            );
        }

        Assert.Equal([Hd], sink.Announcements);
    }

    [Fact]
    public async Task AChangeIsAnnouncedAndTheNewFormatBecomesTheBaseline()
    {
        var sink = new RecordingSink();
        var announcer = new VideoFormatAnnouncer();

        await announcer.AnnounceForAsync(sink, new StubFrame(1920, 1080, PixelFormat.Bgra32), default);
        await announcer.AnnounceForAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);
        await announcer.AnnounceForAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);

        Assert.Equal([Hd, Small], sink.Announcements);
    }

    [Fact]
    public async Task GoingBackToAnEarlierFormatIsAnnouncedAgain()
    {
        // A looping queue returns to the first item, and the sink's surface is whatever the
        // last item left it as. "Seen before" is not "current".
        var sink = new RecordingSink();
        var announcer = new VideoFormatAnnouncer();

        await announcer.AnnounceForAsync(sink, new StubFrame(1920, 1080, PixelFormat.Bgra32), default);
        await announcer.AnnounceForAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);
        await announcer.AnnounceForAsync(sink, new StubFrame(1920, 1080, PixelFormat.Bgra32), default);

        Assert.Equal([Hd, Small, Hd], sink.Announcements);
    }

    [Fact]
    public async Task ResetMakesTheNextFrameAnnounceAgain()
    {
        var sink = new RecordingSink();
        var announcer = new VideoFormatAnnouncer();

        await announcer.AnnounceForAsync(sink, new StubFrame(1920, 1080, PixelFormat.Bgra32), default);
        announcer.Reset();
        await announcer.AnnounceForAsync(sink, new StubFrame(1920, 1080, PixelFormat.Bgra32), default);

        Assert.Equal([Hd, Hd], sink.Announcements);
    }

    [Fact]
    public async Task TheBaselineMovesBeforeTheSinkIsCalled()
    {
        // A second frame of the new format arriving while a slow surface rebuild is in flight
        // must not queue a duplicate behind it, so the record happens before the await.
        var sink = new RecordingSink();
        var announcer = new VideoFormatAnnouncer();
        var frame = new StubFrame(320, 240, PixelFormat.Bgra32);

        sink.OnAnnouncement = () =>
            Assert.Equal(Small, announcer.Announced);

        await announcer.AnnounceForAsync(sink, frame, default);

        Assert.Equal([Small], sink.Announcements);
    }

    private sealed class RecordingSink : IVideoSink
    {
        private readonly List<VideoFormatInfo> _announcements = [];

        public Action? OnAnnouncement { get; set; }

        public IReadOnlyList<VideoFormatInfo> Announcements => _announcements;

        public IFramePool FramePool => null!;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
        {
            OnAnnouncement?.Invoke();
            _announcements.Add(format);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubFrame(int width, int height, PixelFormat format) : IVideoFrame
    {
        public int Width => width;
        public int Height => height;
        public PixelFormat Format => format;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;
        public IVideoFrame AddRef() => this;
        public void Dispose() { }
        public CpuFrameData? AsCpu() => null;
        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
