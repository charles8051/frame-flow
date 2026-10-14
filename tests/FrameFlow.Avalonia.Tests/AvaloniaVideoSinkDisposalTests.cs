using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0085 decision 4 on <see cref="AvaloniaVideoSink"/>: a disposed sink disposes the frame
/// it is given and neither counts nor keeps it, a frame whose present races the dispose is disposed
/// too, and a second dispose does nothing and logs nothing.
/// </summary>
/// <remarks>
/// The sink reads arrivals from a <see cref="FakeTimeProvider"/>, and the race is driven through
/// the frame, so nothing here depends on timing.
/// </remarks>
public sealed class AvaloniaVideoSinkDisposalTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingLoggerFactory _logs = new();

    private AvaloniaVideoSink NewSink() => new(_logs.CreateLogger<AvaloniaVideoSink>(), _time);

    [Fact]
    public async Task AFrameGivenAfterDispose_IsDisposed_AndNeitherCountedNorKept()
    {
        var sink = NewSink();

        // One frame drawn before the dispose, then a wait that a live sink would log as a PRESENT
        // GAP at the next frame.
        await sink.PresentAsync(new CountedFrame(), CancellationToken.None);
        sink.RenderPendingFrame()?.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));
        await sink.DisposeAsync();

        var arrived = 0;
        sink.FrameArrived = () => arrived++;
        var logged = _logs.Entries.Count;
        var late = new CountedFrame();

        await sink.PresentAsync(late, CancellationToken.None);

        Assert.Equal(0, late.References);
        Assert.Null(sink.RenderPendingFrame());
        Assert.Equal(1, sink.RenderedFrameCount);
        Assert.Equal(0, sink.DroppedFrameCount);
        Assert.Equal(0, arrived);
        Assert.Equal(logged, _logs.Entries.Count);
    }

    [Fact]
    public async Task AFrameWhosePresentRacesTheDispose_IsDisposed_NotLeftInTheSlot()
    {
        // The present finds the sink live and reads the frame's duration for the gap tracker before
        // it installs the frame. This frame disposes the sink at that read: the interleaving in which
        // a check of the disposed flag followed by a set left the frame in the slot after dispose's
        // final take, with nothing left to take it.
        var sink = NewSink();

        // A frame drawn, then a wait the racing frame's present logs as a PRESENT GAP. That log
        // comes after the disposed check, so it shows the present got past the check before the
        // dispose ran, and the test is still driving the race.
        await sink.PresentAsync(new CountedFrame(), CancellationToken.None);
        sink.RenderPendingFrame()?.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));

        var arrived = 0;
        sink.FrameArrived = () => arrived++;
        var racing = new CountedFrame { OnDurationRead = () => DisposeNow(sink) };

        await sink.PresentAsync(racing, CancellationToken.None);

        Assert.Single(
            _logs.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("PRESENT GAP", StringComparison.Ordinal)
        );
        Assert.Equal(0, racing.References);
        Assert.Null(sink.RenderPendingFrame());
        Assert.Equal(1, sink.RenderedFrameCount);
        Assert.Equal(0, sink.DroppedFrameCount);
        Assert.Equal(0, arrived);
    }

    [Fact]
    public async Task ASecondDispose_DoesNothing_AndLogsNothing()
    {
        var sink = NewSink();
        await sink.DisposeAsync();
        var logged = _logs.Entries.Count;

        await sink.DisposeAsync();

        Assert.Equal(logged, _logs.Entries.Count);
        Assert.Single(
            _logs.MessagesFrom<AvaloniaVideoSink>(),
            m => m.StartsWith("AvaloniaVideoSink disposed", StringComparison.Ordinal)
        );
    }

    // DisposeAsync completes synchronously, so it runs to its end inside the frame's read.
    private static void DisposeNow(AvaloniaVideoSink sink) =>
        Assert.True(sink.DisposeAsync().AsTask().IsCompletedSuccessfully);

    /// <summary>
    /// A frame that counts its references, so a test sees the sink release it, and can run a
    /// callback the first time its duration is read. Nothing here reads its pixels.
    /// </summary>
    private sealed class CountedFrame : IVideoFrame
    {
        private int _references = 1;
        private bool _durationRead;

        public Action? OnDurationRead { get; init; }

        public int References => Volatile.Read(ref _references);

        public int Width => 16;
        public int Height => 16;
        public TimeSpan Pts => TimeSpan.Zero;
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public TimeSpan Duration
        {
            get
            {
                if (!_durationRead)
                {
                    _durationRead = true;
                    OnDurationRead?.Invoke();
                }

                return TimeSpan.FromMilliseconds(33);
            }
        }

        public IVideoFrame AddRef()
        {
            Interlocked.Increment(ref _references);
            return this;
        }

        public void Dispose() => Interlocked.Decrement(ref _references);

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
