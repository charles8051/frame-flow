using FrameFlow.Avalonia.Windows;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Pins ADR-0085 decision 4 on <see cref="CompositionInteropVideoSink"/> (#664): once disposed, the
/// sink disposes the frame it is given, does not count it in <c>FramesAccepted</c>, keeps nothing
/// and schedules no present, and a second dispose does nothing and logs nothing.
/// </summary>
/// <remarks>
/// The race between a present and the dispose is the slot's: the sink installs straight into its
/// <see cref="LatestWinsFrameSlot"/>, whose tests drive both orders.
/// </remarks>
public sealed class CompositionInteropVideoSinkDisposalTests
{
    [Fact]
    public async Task AFrameGivenAfterDispose_IsDisposed_AndNeitherCountedNorKept()
    {
        var sink = new CompositionInteropVideoSink();
        var pending = new CountedFrame();
        await sink.PresentAsync(pending, CancellationToken.None);
        await sink.DisposeAsync();

        var arrived = 0;
        sink.FrameArrived = () => arrived++;
        CountedFrame[] late = [new(), new()];

        foreach (var frame in late)
            await sink.PresentAsync(frame, CancellationToken.None);

        Assert.Equal(0, pending.References);
        Assert.All(late, frame => Assert.Equal(0, frame.References));
        Assert.Equal(1, sink.FramesAccepted); // the frame before the dispose
        Assert.Equal(0, sink.FramesSuperseded);
        Assert.False(sink.HasPendingFrame);
        Assert.Null(sink.TakePendingFrame());
        Assert.Equal(0, arrived);
    }

    [Fact]
    public async Task ASecondDispose_DoesNothing_AndLogsNothing()
    {
        var log = new RecordingLogger();
        var sink = new CompositionInteropVideoSink(log);
        await sink.DisposeAsync();
        var logged = log.Count;

        await sink.DisposeAsync();

        Assert.Equal(logged, log.Count);
        var late = new CountedFrame();
        await sink.PresentAsync(late, CancellationToken.None);
        Assert.Equal(0, late.References);
        Assert.Equal(0, sink.FramesAccepted);
    }

    /// <summary>Counts what the sink logs.</summary>
    private sealed class RecordingLogger : ILogger<CompositionInteropVideoSink>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Interlocked.Increment(ref _count);
    }

    /// <summary>A frame that counts its references, so a test sees the sink release it.</summary>
    private sealed class CountedFrame : IVideoFrame
    {
        private int _references = 1;

        public int References => Volatile.Read(ref _references);

        public int Width => 1920;
        public int Height => 1080;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

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
