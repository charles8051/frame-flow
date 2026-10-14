// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Sdl.Tests;

/// <summary>
/// Pins ADR-0085 decision 4 on <see cref="SdlVideoSink"/>: once disposed, or once its resources are
/// destroyed, the sink disposes the frame it is given and neither counts nor keeps it, and a second
/// dispose does nothing and logs nothing. The sink is headless, so no test opens SDL.
/// </summary>
/// <remarks>
/// The race between a present and the dispose is the slot's: the sink installs straight into its
/// <see cref="LatestWinsFrameSlot"/>, whose tests drive both orders.
/// </remarks>
public sealed class SdlVideoSinkDisposalTests
{
    private readonly RecordingLogger _log = new();

    private SdlVideoSink NewSink() => SdlVideoSink.CreateHeadless(new SinkLogger(_log));

    [Fact]
    public async Task A_frame_given_after_dispose_is_disposed_and_neither_counted_nor_kept()
    {
        var sink = NewSink();
        var pending = new CountedFrame();
        await sink.PresentAsync(pending, CancellationToken.None);
        await sink.DisposeAsync();
        CountedFrame[] late = [new(), new()];

        foreach (var frame in late)
            await sink.PresentAsync(frame, CancellationToken.None);

        Assert.Equal(0, pending.References);
        Assert.All(late, frame => Assert.Equal(0, frame.References));
        Assert.Equal(0, sink.RenderedFrameCount);
        Assert.Equal(0, sink.DroppedFrameCount);
    }

    [Fact]
    public async Task A_frame_given_after_DestroyResources_is_disposed_and_neither_counted_nor_kept()
    {
        var sink = NewSink();
        sink.DestroyResources();
        CountedFrame[] late = [new(), new()];

        foreach (var frame in late)
            await sink.PresentAsync(frame, CancellationToken.None);

        Assert.All(late, frame => Assert.Equal(0, frame.References));
        Assert.Equal(0, sink.RenderedFrameCount);
        Assert.Equal(0, sink.DroppedFrameCount);
    }

    [Fact]
    public async Task A_second_dispose_does_nothing_and_logs_nothing()
    {
        var sink = NewSink();
        await sink.DisposeAsync();
        var logged = _log.Entries.Count;

        await sink.DisposeAsync();

        Assert.Equal(logged, _log.Entries.Count);
        var late = new CountedFrame();
        await sink.PresentAsync(late, CancellationToken.None);
        Assert.Equal(0, late.References);
    }

    /// <summary>The sink takes a typed logger; this one forwards to the shared recording one.</summary>
    private sealed class SinkLogger(RecordingLogger inner) : ILogger<SdlVideoSink>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>A frame that counts its references, so a test sees the sink release it.</summary>
    private sealed class CountedFrame : IVideoFrame
    {
        private int _references = 1;

        public int References => Volatile.Read(ref _references);

        public int Width => 16;
        public int Height => 16;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(33);
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
