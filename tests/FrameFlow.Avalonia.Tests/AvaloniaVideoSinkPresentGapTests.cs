using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins #576 through the sink: a still held for its dwell logs no PRESENT GAP, and a frame that
/// arrives late past the previous frame's display still does.
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

    private static async Task PresentAsync(AvaloniaVideoSink sink, TimeSpan duration)
    {
        await sink.PresentAsync(new HeldFrame(duration), CancellationToken.None);
        sink.RenderPendingFrame()?.Dispose();
    }

    /// <summary>A frame that only says how long it shows. Nothing here reads its pixels.</summary>
    private sealed class HeldFrame(TimeSpan duration) : IVideoFrame
    {
        public int Width => 16;
        public int Height => 16;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => duration;
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() =>
            throw new NotSupportedException("One-shot frame: ref counting is not supported.");

        public void Dispose() { }

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }

    /// <summary>Keeps the PRESENT GAP warnings the sink logs.</summary>
    private sealed class RecordingLogger : ILogger<AvaloniaVideoSink>
    {
        private readonly List<string> _gaps = [];

        public IReadOnlyList<string> Gaps => _gaps;

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
        }
    }
}
