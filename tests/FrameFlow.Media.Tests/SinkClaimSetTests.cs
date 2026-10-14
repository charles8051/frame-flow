// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins how one claimant claims several sinks (ADR-0085, decisions 6 and 8): all or none, in the
/// one claim order whatever order they are given in, each claim order once, and released together.
/// </summary>
/// <remarks>
/// Two claimants racing for overlapping sets cannot both fail because both take the sinks in the
/// same order. That order is a pure function of the sinks, and
/// <see cref="TakesTheSinks_InClaimOrder_WhateverOrderTheyAreGivenIn"/> pins it directly.
/// </remarks>
public sealed class SinkClaimSetTests
{
    [Fact]
    public void TakesTheSinks_InClaimOrder_WhateverOrderTheyAreGivenIn()
    {
        var log = new List<string>();
        var first = new ClaimableSink("first", log);
        var second = new ClaimableSink("second", log);

        using var claims = SinkClaimSet.TryClaim([second, first], "a player", out var held);

        Assert.NotNull(claims);
        Assert.Null(held);
        Assert.Equal(["first", "second"], log);
        Assert.Equal([first, second], SinkClaimSet.InClaimOrder([second, first]));
        Assert.Equal([first, second], SinkClaimSet.InClaimOrder([first, second]));
    }

    [Fact]
    public void WhenOneIsHeld_ItReleasesThoseItTook_AndNamesTheHeldOne()
    {
        var first = new ClaimableSink("first");
        var second = new ClaimableSink("second");
        using var other = second.TryClaim("another player");

        var claims = SinkClaimSet.TryClaim([first, second], "a player", out var held);

        Assert.Null(claims);
        Assert.Same(second, held);
        Assert.Null(first.Claimant);
        Assert.Equal("another player", second.Claimant);
    }

    [Fact]
    public void ASinkGivenTwice_IsClaimedOnce()
    {
        var log = new List<string>();
        var sink = new ClaimableSink("sink", log);

        using var claims = SinkClaimSet.TryClaim([sink, sink], "a player", out _);

        Assert.NotNull(claims);
        Assert.Equal(["sink"], log);
    }

    [Fact]
    public void Dispose_ReleasesThemAll_AndASecondDisposeLeavesALaterClaimStanding()
    {
        var first = new ClaimableSink("first");
        var second = new ClaimableSink("second");
        var claims = SinkClaimSet.TryClaim([first, second], "a player", out _)!;

        claims.Dispose();

        Assert.Null(first.Claimant);
        Assert.Null(second.Claimant);
        using var later = first.TryClaim("the next player");
        claims.Dispose();
        Assert.Equal("the next player", first.Claimant);
    }

    [Fact]
    public async Task ADisposedSink_Throws_AndLeavesNoneOfTheOthersClaimed()
    {
        var first = new ClaimableSink("first");
        var second = new ClaimableSink("second");
        await second.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() =>
            SinkClaimSet.TryClaim([first, second], "a player", out _)
        );

        Assert.Null(first.Claimant);
    }

    [Fact]
    public void NoSinks_IsAnEmptyClaim()
    {
        using var claims = SinkClaimSet.TryClaim([], "a player", out var held);

        Assert.NotNull(claims);
        Assert.Null(held);
    }

    /// <summary>A claimable sink that records each claim it grants, in order, to a shared log.</summary>
    private sealed class ClaimableSink(string name, List<string>? log = null) : IClaimableSink, IAsyncDisposable
    {
        private readonly SinkClaimGate _gate = new(typeof(ClaimableSink));

        public IDisposable? TryClaim(string claimant)
        {
            var lease = _gate.TryClaim(claimant);
            if (lease is not null)
                log?.Add(name);
            return lease;
        }

        public string? Claimant => _gate.Claimant;

        public long ClaimOrder => _gate.Order;

        public ValueTask DisposeAsync()
        {
            _gate.Close();
            return ValueTask.CompletedTask;
        }

        public override string ToString() => name;
    }
}
