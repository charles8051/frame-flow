// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The pacer's dispose (ADR-0086, decision 3): a second dispose waits for the first, which waits
/// for the delivery loop; and a dispose held by a sink that ignores its token says so once, naming
/// the sink. The loop is held inside the sink's present, which does not observe its token, so
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
}
