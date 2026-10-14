// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Pins ADR-0085 decisions 2 and 3 on <see cref="AvaloniaVideoSink"/>'s side: while no view
/// presents the sink, it keeps only its newest frame for a player that holds its claim, keeps nothing
/// without one, and lets the kept frame go when the claim is released. None of it counts as a drop.
/// </summary>
/// <remarks>
/// The view sets the mode as it starts and stops presenting; these drive it directly, as the view's
/// tests drive it through the view. A player holding the sink is played by a claim taken through
/// <see cref="IClaimableSink"/>, which is what the player builder does at build.
/// </remarks>
public sealed class AvaloniaVideoSinkOffScreenTests
{
    [Fact]
    public async Task NotPresented_AndClaimed_KeepsOnlyTheNewestFrame_WithoutCountingADrop()
    {
        var sink = new AvaloniaVideoSink();
        sink.SetPresented(false);
        using var lease = ((IClaimableSink)sink).TryClaim("a player");
        var first = new CountedFrame();
        var second = new CountedFrame();

        await sink.PresentAsync(first, CancellationToken.None);
        await sink.PresentAsync(second, CancellationToken.None);

        Assert.Equal(0, first.References);
        Assert.Equal(1, second.References);
        Assert.True(sink.HasPendingFrame);
        Assert.Equal(0, sink.DroppedFrameCount);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);
    }

    [Fact]
    public async Task NotPresented_AndUnclaimed_KeepsNothing()
    {
        // A graph built by hand takes no claim, so nothing would say when to let a kept frame go.
        var sink = new AvaloniaVideoSink();
        sink.SetPresented(false);
        var arrived = 0;
        sink.FrameArrived = () => arrived++;
        var frame = new CountedFrame();

        await sink.PresentAsync(frame, CancellationToken.None);

        Assert.Equal(0, frame.References);
        Assert.False(sink.HasPendingFrame);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);
        Assert.Equal(0, sink.DroppedFrameCount);
        Assert.Equal(0, arrived);
    }

    [Fact]
    public async Task ReleasingTheClaim_WhileNotPresented_DisposesTheKeptFrame()
    {
        var sink = new AvaloniaVideoSink();
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
        // A view on screen draws it; the claim's end is no reason to take it away.
        var sink = new AvaloniaVideoSink();
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
        var sink = new AvaloniaVideoSink();
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
        var sink = new AvaloniaVideoSink();
        var pending = new CountedFrame();
        await sink.PresentAsync(pending, CancellationToken.None);

        sink.SetPresented(false);

        Assert.Equal(0, pending.References);
        Assert.False(sink.HasPendingFrame);
        Assert.Equal(1, sink.FramesDiscardedOffScreen);
        Assert.Equal(0, sink.DroppedFrameCount);
    }

    /// <summary>A frame that counts its references, so a test sees the sink release it.</summary>
    private sealed class CountedFrame : IVideoFrame
    {
        private int _references = 1;

        public int References => Volatile.Read(ref _references);

        public int Width => 32;
        public int Height => 16;
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
