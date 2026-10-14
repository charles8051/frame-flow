// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Avalonia.Windows;
using FrameFlow.Media;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// Pins ADR-0085 decision 6 on <see cref="CompositionInteropVideoSink"/>: it is an <see cref="IClaimableSink"/> that one
/// claimant holds at a time, and it takes no claim once disposed.
/// </summary>
public sealed class CompositionInteropVideoSinkClaimTests
{
    [Fact]
    public async Task OneClaimantAtATime_AndNoClaimOnceDisposed()
    {
        var sink = new CompositionInteropVideoSink();
        IClaimableSink claimable = sink;

        var lease = claimable.TryClaim("a player");
        Assert.NotNull(lease);
        Assert.Null(claimable.TryClaim("another player"));
        Assert.Equal("a player", claimable.Claimant);
        lease!.Dispose();
        Assert.Null(claimable.Claimant);

        await sink.DisposeAsync();

        var error = Assert.Throws<ObjectDisposedException>(() => claimable.TryClaim("another player"));
        Assert.Equal(typeof(CompositionInteropVideoSink).FullName, error.ObjectName);
    }
}
