using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #576 and #624 through the sink: a still held for its dwell, and the next item after the
/// player sat at Ended, log no PRESENT GAP, and a frame that arrives late past the previous frame's
/// display still does. The wait before a new item is logged at Debug instead.
/// </summary>
/// <remarks>
/// The sink reads arrivals from a <see cref="FakeTimeProvider"/> the test advances, and each present
/// is drained the way a host pulling frames would, so nothing here observes wall-clock time.
/// <see cref="PresentGapTests"/> covers the decision itself.
/// </remarks>
public sealed class AvaloniaVideoSinkPresentGapTests
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(333_333); // 30 fps
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Transition = TimeSpan.FromMilliseconds(110);

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingLogger _log = new();

    [Fact]
    public async Task AStillHeldForItsDwell_LogsNoGap()
    {
        await using var sink = new AvaloniaVideoSink(_log, _time);

        await PresentAsync(sink, Dwell); // the still
        _time.Advance(Dwell + Transition);
        await PresentAsync(sink, FrameInterval); // the next item's first frame

        Assert.Empty(_log.Gaps);
    }

    [Fact]
    public async Task AFrameLatePastAStillsDwell_LogsAGap()
    {
        await using var sink = new AvaloniaVideoSink(_log, _time);

        await PresentAsync(sink, Dwell);
        _time.Advance(Dwell + TimeSpan.FromMilliseconds(600));
        await PresentAsync(sink, FrameInterval);

        var gap = Assert.Single(_log.Gaps);
        Assert.Contains("PRESENT GAP 3600ms after a frame meant to show for 3000ms", gap);
    }

    [Fact]
    public async Task AFrameLateInAClip_LogsAGap()
    {
        await using var sink = new AvaloniaVideoSink(_log, _time);

        for (int i = 0; i < 3; i++)
        {
            await PresentAsync(sink, FrameInterval);
            _time.Advance(FrameInterval);
        }

        Assert.Empty(_log.Gaps);

        _time.Advance(TimeSpan.FromMilliseconds(600));
        await PresentAsync(sink, FrameInterval);

        Assert.Single(_log.Gaps);
    }

    [Fact]
    public async Task AStillsDwell_DoesNotExcuseTheWaitBeforeIt()
    {
        await using var sink = new AvaloniaVideoSink(_log, _time);

        // A clip's frame, then a still two seconds later: the dwell is the time after the still.
        await PresentAsync(sink, FrameInterval);
        _time.Advance(TimeSpan.FromSeconds(2));
        await PresentAsync(sink, Dwell);

        Assert.Single(_log.Gaps);
    }

    [Fact]
    public async Task ANewItemAfterThePlayerSatAtEnded_LogsNoGap()
    {
        // #624: the item played to its end, and the player was given the next one 3 s later.
        await using var sink = new AvaloniaVideoSink(_log, _time);
        var ended = TimelineId.Next();
        var given = TimelineId.Next();

        await PresentAsync(sink, FrameInterval, ended);
        _time.Advance(TimeSpan.FromMilliseconds(3193));
        await PresentAsync(sink, FrameInterval, given);

        Assert.Empty(_log.Gaps);
    }

    [Fact]
    public async Task ANewItemAfterThePlayerSatAtEnded_LogsTheWaitAtDebug()
    {
        // Not a stall, but a slow seek, loop or open stays visible to someone reading the log.
        await using var sink = new AvaloniaVideoSink(_log, _time);
        var ended = TimelineId.Next();
        var given = TimelineId.Next();

        await PresentAsync(sink, FrameInterval, ended);
        _time.Advance(TimeSpan.FromMilliseconds(3193));
        await PresentAsync(sink, FrameInterval, given);

        var wait = Assert.Single(_log.Waits);
        Assert.Contains($"present gap=3193.0ms starts {given}", wait);
    }

    [Fact]
    public async Task AFrameLateInTheNewItem_LogsAGap()
    {
        await using var sink = new AvaloniaVideoSink(_log, _time);
        var ended = TimelineId.Next();
        var given = TimelineId.Next();

        await PresentAsync(sink, FrameInterval, ended);
        _time.Advance(TimeSpan.FromMilliseconds(3193));
        await PresentAsync(sink, FrameInterval, given);
        _time.Advance(FrameInterval + TimeSpan.FromMilliseconds(600));
        await PresentAsync(sink, FrameInterval, given);

        var gap = Assert.Single(_log.Gaps);
        Assert.Contains("PRESENT GAP 633ms after a frame meant to show for 33ms", gap);
        Assert.Contains($"on {given})", gap);
    }

    [Fact]
    public async Task ALateFrameThatNamesNoTimeline_IsReportedOnTheTimelineItWasJudgedOn()
    {
        await using var sink = new AvaloniaVideoSink(_log, _time);
        var playing = TimelineId.Next();

        await PresentAsync(sink, FrameInterval, playing);
        _time.Advance(FrameInterval + TimeSpan.FromMilliseconds(600));
        await PresentAsync(sink, FrameInterval, TimelineId.None);

        var gap = Assert.Single(_log.Gaps);
        Assert.Contains($"on {playing})", gap);
    }

    private static async Task PresentAsync(
        AvaloniaVideoSink sink,
        TimeSpan duration,
        TimelineId timeline = default
    )
    {
        await sink.PresentAsync(new HeldFrame(duration, timeline), CancellationToken.None);
        sink.RenderPendingFrame()?.Dispose();
    }

    /// <summary>
    /// A frame that only says how long it shows and which timeline it belongs to. Nothing here
    /// reads its pixels.
    /// </summary>
    private sealed class HeldFrame(TimeSpan duration, TimelineId timeline) : IVideoFrame
    {
        public int Width => 16;
        public int Height => 16;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => duration;
        public TimelineId Timeline => timeline;
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() =>
            throw new NotSupportedException("One-shot frame: ref counting is not supported.");

        public void Dispose() { }

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }

    /// <summary>
    /// Keeps the PRESENT GAP warnings the sink logs, and the Debug lines for waits before the first
    /// frame of a new timeline.
    /// </summary>
    private sealed class RecordingLogger : ILogger<AvaloniaVideoSink>
    {
        private readonly List<string> _gaps = [];
        private readonly List<string> _waits = [];

        public IReadOnlyList<string> Gaps => _gaps;
        public IReadOnlyList<string> Waits => _waits;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Warning && message.Contains("PRESENT GAP", StringComparison.Ordinal))
                _gaps.Add(message);
            else if (logLevel == LogLevel.Debug && message.Contains(" starts ", StringComparison.Ordinal))
                _waits.Add(message);
        }
    }
}
