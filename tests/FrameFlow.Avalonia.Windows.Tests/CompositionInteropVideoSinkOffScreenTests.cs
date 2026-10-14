// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Avalonia.Windows;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Pins ADR-0085 decisions 2 and 3 on <see cref="CompositionInteropVideoSink"/>'s side: while no view
/// presents the sink, it keeps only its newest frame for a player that holds its claim, keeps nothing
/// without one, and lets the kept frame go when the claim is released. None of it counts as a drop.
/// </summary>
/// <remarks>
/// The view sets the mode as it starts and stops presenting; these drive it directly, as the view's
/// reattach tests in FrameFlow.Avalonia.Tests drive it through the view. The slot's own tests cover
/// the two orders of a change of mode racing an install.
/// </remarks>
public sealed class CompositionInteropVideoSinkOffScreenTests
{
    [Fact]
    public async Task NotPresented_AndClaimed_KeepsOnlyTheNewestFrame_WithoutCountingADrop()
    {
        var sink = new CompositionInteropVideoSink();
        sink.SetPresented(false);
        using var lease = ((IClaimableSink)sink).TryClaim("a player");
        var first = new CountedFrame();
        var second = new CountedFrame();

        await sink.PresentAsync(first, CancellationToken.None);
        await sink.PresentAsync(second, CancellationToken.None);

        Assert.Equal(0, first.References);
        Assert.Equal(1, second.References);
        Assert.True(sink.HasPendingFrame);
        Assert.Equal(0, sink.FramesSuperseded);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);
    }

    [Fact]
    public async Task NotPresented_AndUnclaimed_KeepsNothing()
    {
        // A graph built by hand takes no claim, so nothing would say when to let a kept frame go.
        var sink = new CompositionInteropVideoSink();
        sink.SetPresented(false);
        var arrived = 0;
        sink.FrameArrived = () => arrived++;
        var frame = new CountedFrame();

        await sink.PresentAsync(frame, CancellationToken.None);

        Assert.Equal(0, frame.References);
        Assert.False(sink.HasPendingFrame);
        Assert.Equal(0, sink.FramesAccepted);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);
        Assert.Equal(0, arrived);
    }

    [Fact]
    public async Task ReleasingTheClaim_WhileNotPresented_DisposesTheKeptFrame()
    {
        var sink = new CompositionInteropVideoSink();
        sink.SetPresented(false);
        var lease = ((IClaimableSink)sink).TryClaim("a player")!;
        var kept = new CountedFrame();
        await sink.PresentAsync(kept, CancellationToken.None);
        Assert.Equal(1, kept.References);

        lease.Dispose();

        Assert.Equal(0, kept.References);
        Assert.False(sink.HasPendingFrame);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);
    }

    [Fact]
    public async Task ReleasingTheClaim_WhilePresented_LeavesThePendingFrame()
    {
        var sink = new CompositionInteropVideoSink();
        var lease = ((IClaimableSink)sink).TryClaim("a player")!;
        var pending = new CountedFrame();
        await sink.PresentAsync(pending, CancellationToken.None);

        lease.Dispose();

        Assert.Equal(1, pending.References);
        Assert.Same(pending, sink.TakePendingFrame());
    }

    [Fact]
    public async Task AClaimTakenAfterOneWasReleased_KeepsAFrameAgain()
    {
        var sink = new CompositionInteropVideoSink();
        sink.SetPresented(false);
        ((IClaimableSink)sink).TryClaim("the first player")!.Dispose();
        using var lease = ((IClaimableSink)sink).TryClaim("the second player");
        var kept = new CountedFrame();

        await sink.PresentAsync(kept, CancellationToken.None);

        Assert.Equal(1, kept.References);
        Assert.True(sink.HasPendingFrame);
    }

    [Fact]
    public async Task StoppingPresentation_LetsGoOfAnUnclaimedSinksPendingFrame()
    {
        var sink = new CompositionInteropVideoSink();
        var pending = new CountedFrame();
        await sink.PresentAsync(pending, CancellationToken.None);

        sink.SetPresented(false);

        Assert.Equal(0, pending.References);
        Assert.False(sink.HasPendingFrame);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);
        Assert.Equal(0, sink.FramesSuperseded);
    }

    [Fact]
    public void StoppingPresentation_KeepsTheFrameTheViewPresentedLast_ForAClaimant()
    {
        var sink = new CompositionInteropVideoSink();
        var lease = ((IClaimableSink)sink).TryClaim("a paused player")!;
        var shown = new CountedFrame();

        sink.StopPresenting(shown);

        Assert.Equal(1, shown.References);
        Assert.Same(shown, sink.TakePendingFrame());
        Assert.Equal(0, sink.FramesAccepted); // accepted once already, when it first arrived
        shown.Dispose();
        lease.Dispose();
    }

    [Fact]
    public void StoppingPresentation_WithNoClaim_DisposesTheFrameTheViewPresentedLast()
    {
        var sink = new CompositionInteropVideoSink();
        var shown = new CountedFrame();

        sink.StopPresenting(shown);

        Assert.Equal(0, shown.References);
        Assert.False(sink.HasPendingFrame);
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
