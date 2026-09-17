// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Pins <see cref="IFramePresentedSource"/> on the zero-copy surface, the half of the
/// contract the CPU sink's tests do not reach.
/// </summary>
/// <remarks>
/// <para>
/// The two sinks raise the event from opposite places. <c>AvaloniaVideoSink</c> owns its
/// swap and raises at it. This sink never sees the screen: it hands the frame to
/// <c>CompositionInteropVideoView</c>, and the view calls <c>RaiseFramePresented</c> from
/// the continuation of the compositor hand-off. A live compositor is not available here, so
/// these drive that entry point directly and cover what it promises: one report per present,
/// the PTS the caller named, and no subscriber able to take down another.
/// </para>
/// <para>
/// Nothing here observes wall-clock time. The only assertion about <c>PresentedAtUtc</c> is
/// its kind.
/// </para>
/// </remarks>
public sealed class CompositionInteropVideoSinkFramePresentedTests
{
    private static CompositionInteropVideoSink NewSink() =>
        new(new CpuFramePool(NullLogger<CpuFramePool>.Instance));

    private static TimeSpan Pts(int n) => TimeSpan.FromMilliseconds(n * 16);

    [Fact]
    public void TheEventCarriesThePtsThePresenterReported()
    {
        var sink = NewSink();
        var presented = new List<TimeSpan>();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);

        sink.RaiseFramePresented(Pts(7));

        Assert.Equal([Pts(7)], presented);
    }

    [Fact]
    public void TheSenderIsTheSink()
    {
        var sink = NewSink();
        object? sender = null;
        sink.FramePresented += (s, _) => sender = s;

        sink.RaiseFramePresented(Pts(1));

        Assert.Same(sink, sender);
    }

    [Fact]
    public void PresentedAtUtcIsUtc()
    {
        var sink = NewSink();
        DateTime? stamp = null;
        sink.FramePresented += (_, e) => stamp = e.PresentedAtUtc;

        sink.RaiseFramePresented(Pts(1));

        Assert.Equal(DateTimeKind.Utc, stamp!.Value.Kind);
    }

    [Fact]
    public async Task AcceptingAFrameDoesNotReportIt()
    {
        // The sink's intake is not a present. A frame installed in the slot may still be
        // superseded before the view draws it, and reporting it here would tell an overlay
        // to draw over a picture nobody saw.
        var sink = NewSink();
        var presented = new List<TimeSpan>();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);

        await sink.PresentAsync(new StubFrame(), CancellationToken.None);

        Assert.True(sink.HasPendingFrame);
        Assert.Empty(presented);
    }

    [Fact]
    public void EveryPresentIsReportedOnce_InOrder()
    {
        var sink = NewSink();
        var presented = new List<TimeSpan>();
        sink.FramePresented += (_, e) => presented.Add(e.PresentationTime);

        for (int i = 1; i <= 5; i++)
            sink.RaiseFramePresented(Pts(i));

        Assert.Equal([Pts(1), Pts(2), Pts(3), Pts(4), Pts(5)], presented);
    }

    [Fact]
    public void AThrowingHandlerDoesNotStarveTheOneBehindIt()
    {
        // The raise walks the invocation list one subscriber at a time. A chain invocation
        // would abandon every handler after the one that threw, so a broken consumer would
        // silently stop an unrelated overlay updating.
        var sink = NewSink();
        var seen = new List<TimeSpan>();
        sink.FramePresented += (_, _) => throw new InvalidOperationException("handler failed");
        sink.FramePresented += (_, e) => seen.Add(e.PresentationTime);

        sink.RaiseFramePresented(Pts(2));
        sink.RaiseFramePresented(Pts(3));

        Assert.Equal([Pts(2), Pts(3)], seen);
    }

    [Fact]
    public void AThrowingHandlerDoesNotFailThePresent()
    {
        // The raise runs on the compositor hand-off continuation. A consumer that cannot
        // process one present is not a reason to stall the present loop.
        var sink = NewSink();
        sink.FramePresented += (_, _) => throw new InvalidOperationException("handler failed");

        sink.RaiseFramePresented(Pts(1));
        sink.RaiseFramePresented(Pts(2));
    }

    [Fact]
    public void UnsubscribingStopsTheReports()
    {
        var sink = NewSink();
        var presented = new List<TimeSpan>();
        void Handler(object? _, FramePresentedInfo e) => presented.Add(e.PresentationTime);

        sink.FramePresented += Handler;
        sink.RaiseFramePresented(Pts(1));
        sink.FramePresented -= Handler;
        sink.RaiseFramePresented(Pts(2));

        Assert.Equal([Pts(1)], presented);
    }

    [Fact]
    public void WithNoSubscriberTheRaiseIsANoOp()
    {
        // The sink is usable without a consumer: a host that never asks for presents still
        // drives the same present loop.
        var sink = NewSink();

        sink.RaiseFramePresented(Pts(1));
    }

    private sealed class StubFrame : IVideoFrame
    {
        public int Width => 1920;
        public int Height => 1080;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(16);
        public PixelFormat Format => PixelFormat.Bgra32;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() { }

        public CpuFrameData? AsCpu() => null;

        public CpuFrameData ToCpu() => throw new NotSupportedException();
    }
}
