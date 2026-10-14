// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The pacer's dispose (ADR-0086, decision 3): a second dispose waits for the first, which waits
/// for the delivery loop; a dispose held by a sink that ignores its token says so once, naming
/// the sink; and what the loop's teardown throws is thrown by the dispose rather than dropped. The loop is held inside the sink's present, which does not observe its token, so
/// neither dispose can end before the test releases it, and the warning's interval runs on a
/// <see cref="FakeTimeProvider"/>, whose timers fire inside <see cref="FakeTimeProvider.Advance"/>.
/// </summary>
public sealed partial class ClockSelectVideoSinkTests
{
    [Fact]
    public async Task ASecondDispose_WaitsForTheFirst_WhichWaitsForTheDeliveryLoop()
    {
        var sink = new ScriptedSink { HoldPresent = 1 };
        var pacer = NewPacer(sink, new SinkFailureRecorder());
        await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
        await sink.Held;

        var first = pacer.DisposeAsync().AsTask();
        var second = pacer.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        sink.ReleaseHeld();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, sink.Calls);
    }

    [Fact]
    public async Task ADisposeHeldByASinkThatIgnoresItsToken_SaysSoOnce_NamingTheSink()
    {
        var time = new FakeTimeProvider();
        var logger = new LevelCountingLogger();
        var sink = new ScriptedSink { HoldPresent = 1 };
        var pacer = new ClockSelectVideoSink(sink, new FakeClock(), logger, capacity: 4, timeProvider: time);
        await pacer.PresentAsync(new CountedFrame(TimeSpan.Zero), default);
        await sink.Held;
        var interval = ClockSelectVideoSink.DisposeWarningInterval;

        var disposing = pacer.DisposeAsync().AsTask();

        time.Advance(interval - TimeSpan.FromTicks(1));
        Assert.Equal(0, logger.Count(LogLevel.Warning, "dispose has waited"));
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, logger.Count(LogLevel.Warning, $"the video sink {nameof(ScriptedSink)}"));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, logger.Count(LogLevel.Warning, "dispose has waited"));

        sink.ReleaseHeld();
        await disposing.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task WhatTheLoopsTeardownThrows_IsThrownByTheDispose_AndEveryBufferedFrameIsStillDisposed()
    {
        // The frames are due long after the clock, which never moves, so both are still in the
        // ring when the dispose stops the loop, and the loop's teardown disposes them.
        var sink = new ScriptedSink();
        var pacer = NewPacer(sink, new SinkFailureRecorder());
        var failing = new FailingDisposeFrame(TimeSpan.FromSeconds(10));
        var after = new CountedFrame(TimeSpan.FromSeconds(11));
        await pacer.PresentAsync(failing, default);
        await pacer.PresentAsync(after, default);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pacer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30))
        );

        Assert.Same(failing.Failure, thrown);
        Assert.Equal(0, after.References);
        Assert.Equal(0, sink.Calls);
    }

    /// <summary>A frame whose dispose throws.</summary>
    private sealed class FailingDisposeFrame(TimeSpan pts) : IVideoFrame
    {
        public InvalidOperationException Failure { get; } = new("the frame's dispose failed");

        public int Width => 320;
        public int Height => 240;
        public TimeSpan Pts => pts;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(33);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() => throw Failure;

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
