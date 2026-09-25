using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// The join's count limit (ADR-0081, decision 1): which retained secondaries can still match,
/// and what the window does when it holds <c>MaxRetained</c> of them. Pure and synchronous: the
/// window takes no clock and no pump.
/// </summary>
public sealed class SyncJoinRetainedLimitTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    // ── Which secondaries can still match ───────────────────────────────

    /// <param name="spans">Pairs of <c>From</c> and <c>To</c> in ms, ordered by <c>From</c>.</param>
    /// <param name="expected">Whether each span is still a candidate.</param>
    [Theory]
    // Ends before the primary; ends at it (half-open); straddles it; starts at it; ahead of it.
    [InlineData(100, new[] { 0, 50, 50, 100, 80, 120, 100, 150, 150, 200 }, new[] { false, false, true, true, true })]
    // A long interval that started first still contains the primary.
    [InlineData(100, new[] { 0, 1000, 90, 95 }, new[] { true, false })]
    [InlineData(100, new int[0], new bool[0])]
    public void Within_KeepsIntervalsThatEndAfterThePrimary(int primary, int[] spans, bool[] expected)
    {
        Assert.Equal(expected, Mask(SyncMatch.Within, primary, spans));
    }

    [Theory]
    // Only the newest at or before the primary, and everything ahead of it.
    [InlineData(100, new[] { 0, 0, 50, 50, 100, 100, 150, 150 }, new[] { false, false, true, true })]
    [InlineData(100, new[] { 0, 0, 50, 50, 150, 150 }, new[] { false, true, true })]
    [InlineData(100, new[] { 150, 150, 200, 200 }, new[] { true, true })]
    // An interval that straddles the primary counts by its From, as matching does.
    [InlineData(100, new[] { 20, 200, 80, 120 }, new[] { false, true })]
    // Two at the same time: the later one is what a match returns.
    [InlineData(100, new[] { 50, 50, 50, 50 }, new[] { false, true })]
    public void MostRecentAtOrBefore_KeepsTheNewestAtOrBeforeThePrimaryAndEverythingAhead(
        int primary,
        int[] spans,
        bool[] expected
    )
    {
        Assert.Equal(expected, Mask(SyncMatch.MostRecentAtOrBefore, primary, spans));
    }

    [Fact]
    public void MostRecentAtOrBefore_DropsTheNewestOnceItIsTooStale()
    {
        var spans = new[] { 0, 0, 50, 50, 150, 150 };

        Assert.Equal(
            new[] { false, false, true },
            Mask(SyncMatch.MostRecentAtOrBefore, 100, spans, maxStaleness: Ms(30))
        );
        Assert.Equal(
            new[] { false, true, true },
            Mask(SyncMatch.MostRecentAtOrBefore, 100, spans, maxStaleness: Ms(50))
        );
    }

    // ── The window at its limit ─────────────────────────────────────────

    [Fact]
    public void BeforeAnyPrimary_EverySecondaryIsACandidate_SoTheReaderWaits()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        var spans = Points(0, 50, 100);

        Assert.Null(Admit(window, spans[0], limit: 2, SyncMatch.MostRecentAtOrBefore));
        Assert.Null(Admit(window, spans[1], limit: 2, SyncMatch.MostRecentAtOrBefore));
        var room = Admit(window, spans[2], limit: 2, SyncMatch.MostRecentAtOrBefore);

        Assert.NotNull(room);
        Assert.False(room.IsCompleted);
        Assert.True(window.HasRoomWaiter);
        Assert.Equal(2, window.Count);

        window.Clear();
        Assert.True(room.IsCompleted);
        spans[2].Dispose();
        Assert.All(spans, s => Assert.Equal(0, s.RefCount));
    }

    [Fact]
    public void MostRecentAtOrBefore_AtTheLimit_ReleasesTheOlderSecondariesFirst()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        var spans = Points(0, 50, 100);
        Admit(window, spans[0], limit: 2, SyncMatch.MostRecentAtOrBefore);
        Admit(window, spans[1], limit: 2, SyncMatch.MostRecentAtOrBefore);

        Match(window, 60, SyncMatch.MostRecentAtOrBefore);

        Assert.Null(Admit(window, spans[2], limit: 2, SyncMatch.MostRecentAtOrBefore));
        Assert.Equal(0, spans[0].RefCount);
        Assert.Equal(2, window.Count);

        window.Clear();
        Assert.All(spans, s => Assert.Equal(0, s.RefCount));
    }

    [Fact]
    public void Within_AtTheLimit_ReleasesAnIntervalEndingAtThePrimary_AndKeepsOneStraddlingIt()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        var ending = Interval(0, 100);
        var straddling = Interval(80, 200);
        var next = Interval(150, 250);
        Admit(window, ending, limit: 2, SyncMatch.Within);
        Admit(window, straddling, limit: 2, SyncMatch.Within);

        Match(window, 100, SyncMatch.Within);

        Assert.Null(Admit(window, next, limit: 2, SyncMatch.Within));
        Assert.Equal(0, ending.Box.RefCount);
        Assert.Equal(1, straddling.Box.RefCount);
        Assert.Equal(2, window.Count);

        window.Clear();
    }

    [Fact]
    public void WhenEveryRetainedSecondaryIsACandidate_TheReaderWaitsUntilThePrimaryPassesOne()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        var spans = Points(100, 150, 200);
        Admit(window, spans[0], limit: 2, SyncMatch.MostRecentAtOrBefore);
        Admit(window, spans[1], limit: 2, SyncMatch.MostRecentAtOrBefore);
        Match(window, 50, SyncMatch.MostRecentAtOrBefore);

        // Both are ahead of the primary.
        var room = Admit(window, spans[2], limit: 2, SyncMatch.MostRecentAtOrBefore);
        Assert.NotNull(room);

        // 100 is now the newest at or before the primary, and 150 is ahead: both still match.
        Match(window, 120, SyncMatch.MostRecentAtOrBefore);
        Assert.True(room.IsCompleted);
        room = Admit(window, spans[2], limit: 2, SyncMatch.MostRecentAtOrBefore);
        Assert.NotNull(room);
        Assert.Equal(1, spans[0].RefCount);

        // Past 150, 100 can no longer match.
        Match(window, 160, SyncMatch.MostRecentAtOrBefore);
        Assert.True(room.IsCompleted);
        Assert.Null(Admit(window, spans[2], limit: 2, SyncMatch.MostRecentAtOrBefore));
        Assert.Equal(0, spans[0].RefCount);

        window.Clear();
        Assert.All(spans, s => Assert.Equal(0, s.RefCount));
    }

    [Fact]
    public void BelowTheLimit_NothingIsReleased()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        var spans = Points(0, 50);
        Admit(window, spans[0], limit: 3, SyncMatch.MostRecentAtOrBefore);
        Match(window, 60, SyncMatch.MostRecentAtOrBefore);

        Assert.Null(Admit(window, spans[1], limit: 3, SyncMatch.MostRecentAtOrBefore));

        Assert.Equal(1, spans[0].RefCount);
        Assert.Equal(2, window.Count);
        window.Clear();
    }

    [Fact]
    public void ACountLimitBelowOne_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new SyncJoinNode<RefBox<int>, RefBox<int>, RefBox<int>>(
                    "join",
                    (primary, _, _) => ValueTask.FromResult<RefBox<int>?>(primary),
                    new SyncJoinKeys<RefBox<int>, RefBox<int>>(
                        p => Ms(p.Value),
                        s => (Ms(s.Value), Ms(s.Value))
                    ),
                    SyncMatch.MostRecentAtOrBefore,
                    window: Ms(100),
                    maxRetained: 0
                )
        );
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private sealed record Timed(RefBox<int> Box, TimeSpan From, TimeSpan To);

    private static bool[] Mask(
        SyncMatch policy,
        int primary,
        int[] spans,
        TimeSpan? maxStaleness = null
    )
    {
        var entries = new List<(TimeSpan From, TimeSpan To)>();
        for (int i = 0; i < spans.Length; i += 2)
            entries.Add((Ms(spans[i]), Ms(spans[i + 1])));
        return SecondaryCandidates.Mask(
            entries,
            Ms(primary),
            policy,
            maxStaleness ?? TimeSpan.MaxValue
        );
    }

    private static RefBox<int>[] Points(params int[] ms) => ms.Select(m => RefBox.Of(m)).ToArray();

    private static Timed Interval(int from, int to) => new(RefBox.Of(from), Ms(from), Ms(to));

    private static Task? Admit(
        SecondaryWindow<RefBox<int>> window,
        RefBox<int> point,
        int limit,
        SyncMatch policy
    ) =>
        window.TryAdmit(
            point,
            Ms(point.Value),
            Ms(point.Value),
            maxLead: null,
            limit,
            policy,
            TimeSpan.MaxValue
        );

    private static Task? Admit(
        SecondaryWindow<RefBox<int>> window,
        Timed span,
        int limit,
        SyncMatch policy
    ) => window.TryAdmit(span.Box, span.From, span.To, maxLead: null, limit, policy, TimeSpan.MaxValue);

    /// <summary>Advances the primary to <paramref name="ms"/> and releases the match it hands back.</summary>
    private static void Match(SecondaryWindow<RefBox<int>> window, int ms, SyncMatch policy) =>
        window.AdvanceAndMatch(Ms(ms), policy, Ms(10_000), TimeSpan.MaxValue)?.Dispose();
}
