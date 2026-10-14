// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Sdl.Tests;

/// <summary>
/// Pins ADR-0085 decision 6 on <see cref="SdlVideoSink"/>: it is an <see cref="IClaimableSink"/> that one
/// claimant holds at a time, and it takes no claim once disposed.
/// </summary>
public sealed class SdlVideoSinkClaimTests
{
    [Fact]
    public async Task One_claimant_at_a_time_and_no_claim_once_disposed()
    {
        var sink = SdlVideoSink.CreateHeadless();
        IClaimableSink claimable = sink;

        var lease = claimable.TryClaim("a player");
        Assert.NotNull(lease);
        Assert.Null(claimable.TryClaim("another player"));
        Assert.Equal("a player", claimable.Claimant);
        lease!.Dispose();
        Assert.Null(claimable.Claimant);

        await sink.DisposeAsync();

        var error = Assert.Throws<ObjectDisposedException>(() => claimable.TryClaim("another player"));
        Assert.Equal(typeof(SdlVideoSink).FullName, error.ObjectName);
    }
}
