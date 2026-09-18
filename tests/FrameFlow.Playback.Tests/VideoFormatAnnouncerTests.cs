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

        await announcer.PresentAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);

        Assert.Equal([Small], sink.Announcements);
    }

    [Fact]
    public async Task AnUnchangedFormatIsAnnouncedOnce()
    {
        var sink = new RecordingSink();
        var announcer = new VideoFormatAnnouncer();

        for (var i = 0; i < 5; i++)
        {
            await announcer.PresentAsync(
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

        await announcer.PresentAsync(sink, new StubFrame(1920, 1080, PixelFormat.Bgra32), default);
        await announcer.PresentAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);
        await announcer.PresentAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);

        Assert.Equal([Hd, Small], sink.Announcements);
    }

    [Fact]
    public async Task GoingBackToAnEarlierFormatIsAnnouncedAgain()
    {
        // A looping queue returns to the first item, and the sink's surface is whatever the
        // last item left it as. "Seen before" is not "current".
        var sink = new RecordingSink();
        var announcer = new VideoFormatAnnouncer();

        await announcer.PresentAsync(sink, new StubFrame(1920, 1080, PixelFormat.Bgra32), default);
        await announcer.PresentAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);
        await announcer.PresentAsync(sink, new StubFrame(1920, 1080, PixelFormat.Bgra32), default);

        Assert.Equal([Hd, Small, Hd], sink.Announcements);
    }

    [Fact]
    public async Task AFailedAnnouncementIsRetriedOnTheNextFrame()
    {
        // The baseline moves only after the sink call returns. Recording it first would leave
        // a format marked announced that the sink was never successfully told, and every later
        // frame of that format would skip the call.
        var sink = new RecordingSink
        {
            ThrowOnAnnouncement = new InvalidOperationException("surface"),
        };
        var announcer = new VideoFormatAnnouncer();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await announcer.PresentAsync(
                sink,
                new StubFrame(320, 240, PixelFormat.Bgra32),
                default
            )
        );

        Assert.Null(announcer.Announced);
        Assert.Empty(sink.Presents);

        sink.ThrowOnAnnouncement = null;
        await announcer.PresentAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);

        Assert.Equal([Small], sink.Announcements);
        Assert.Single(sink.Presents);
    }

    [Fact]
    public async Task ACancelledAnnouncementIsRetriedOnTheNextFrame()
    {
        var sink = new RecordingSink { ThrowOnAnnouncement = new OperationCanceledException() };
        var announcer = new VideoFormatAnnouncer();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await announcer.PresentAsync(
                sink,
                new StubFrame(320, 240, PixelFormat.Bgra32),
                default
            )
        );

        Assert.Null(announcer.Announced);

        sink.ThrowOnAnnouncement = null;
        await announcer.PresentAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default);

        Assert.Equal([Small], sink.Announcements);
    }

    [Fact]
    public async Task AnotherPacersFrameCannotOvertakeAnAnnouncementInFlight()
    {
        // Two sessions are live at a queue boundary, because the next item is created and
        // warmed while the current one still plays, so two pacers reach the same sink through
        // one announcer. Unserialized, the second could announce and present while the first
        // was still inside its callback, and the sink would be handed a frame whose shape is
        // not the last shape it was told.
        var sink = new RecordingSink();
        var announcer = new VideoFormatAnnouncer();
        var insideFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        sink.OnAnnouncementAsync = async () =>
        {
            if (sink.Announcements.Count > 0)
                return;

            insideFirst.TrySetResult();
            await releaseFirst.Task;
        };

        var first = announcer
            .PresentAsync(sink, new StubFrame(1920, 1080, PixelFormat.Bgra32), default)
            .AsTask();
        await insideFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = announcer
            .PresentAsync(sink, new StubFrame(320, 240, PixelFormat.Bgra32), default)
            .AsTask();

        Assert.False(second.IsCompleted, "the second present overtook an announcement in flight");

        releaseFirst.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        // Every frame is preceded by the announcement that describes it.
        Assert.Equal(
            ["format 1920x1080", "frame 1920x1080", "format 320x240", "frame 320x240"],
            sink.Log
        );
    }

    private sealed class RecordingSink : IVideoSink
    {
        private readonly List<VideoFormatInfo> _announcements = [];
        private readonly List<string> _presents = [];
        private readonly List<string> _log = [];

        /// <summary>Runs inside the announcement, for interleaving a second caller.</summary>
        public Func<Task>? OnAnnouncementAsync { get; set; }

        /// <summary>When set, the announcement throws it instead of succeeding.</summary>
        public Exception? ThrowOnAnnouncement { get; set; }

        public IReadOnlyList<VideoFormatInfo> Announcements => _announcements;

        public IReadOnlyList<string> Presents => _presents;

        /// <summary>Announcements and frames in the order the sink saw them.</summary>
        public IReadOnlyList<string> Log => _log;

        public IFramePool FramePool => null!;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            _presents.Add($"{frame.Width}x{frame.Height}");
            _log.Add($"frame {frame.Width}x{frame.Height}");
            return ValueTask.CompletedTask;
        }

        public async ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
        {
            if (OnAnnouncementAsync is { } hook)
                await hook();

            if (ThrowOnAnnouncement is { } ex)
                throw ex;

            _announcements.Add(format);
            _log.Add($"format {format.Width}x{format.Height}");
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
