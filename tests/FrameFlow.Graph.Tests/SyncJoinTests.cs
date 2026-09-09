using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type
// (FrameFlow.Graph.Graph). Alias the type so the tests can sit in the
// conventional FrameFlow.Graph.Tests namespace without the clash.
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// Behaviour and ownership tests for <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}"/>
/// (the sync-window-join ADR). Covers both match policies, the no-match path,
/// the two termination rules, and refcount balance across all of them.
/// </summary>
/// <remarks>
/// Every flowing item is a <see cref="RefBox{T}"/> so each test can assert the
/// substrate settles every ref to zero. Each pump terminates before
/// <see cref="GraphRunner.RunAsync"/> returns, so asserting immediately
/// afterward observes the settled state.
/// </remarks>
public sealed class SyncJoinTests
{
    // ── Item shapes ─────────────────────────────────────────────────────

    /// <summary>A primary item: its own media time.</summary>
    private sealed record Tick(TimeSpan At);

    /// <summary>A secondary item: a media-time span and a tag to assert on.</summary>
    private sealed record Span(TimeSpan From, TimeSpan To, string Tag);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    // ── Helpers ─────────────────────────────────────────────────────────

    private static readonly SyncJoinKeys<RefBox<Tick>, RefBox<Span>> Keys =
        new(p => p.Value.At, s => (s.Value.From, s.Value.To));

    /// <summary>
    /// A source that hands the substrate <paramref name="items"/> in order, one
    /// per pull, then EOS. The source pump is single-threaded, so the plain
    /// index needs no synchronization.
    /// </summary>
    private static SourceNode<T> Emit<T>(string id, params T[] items)
        where T : class, IRefCounted
    {
        int i = 0;
        return new SourceNode<T>(
            id,
            _ => ValueTask.FromResult<T?>(i < items.Length ? items[i++] : null)
        );
    }

    /// <summary>
    /// A primary source that waits, <b>once</b>, for the join's window to hold
    /// <paramref name="awaitRetained"/> secondaries, then emits. Removes the
    /// admit-versus-match race so the assertions are deterministic rather than
    /// timing-dependent.
    /// </summary>
    /// <remarks>
    /// The gate is deliberately one-shot. Re-checking it on later pulls
    /// deadlocks the source once eviction takes the window back below the
    /// threshold, which is the state most of these tests are built to reach.
    /// </remarks>
    private static SourceNode<RefBox<Tick>> EmitAfterRetained<TOut>(
        SyncJoinNode<RefBox<Tick>, RefBox<Span>, TOut> join,
        int awaitRetained,
        params RefBox<Tick>[] items
    )
        where TOut : class, IRefCounted
    {
        int i = 0;
        return new SourceNode<RefBox<Tick>>(
            "primary",
            async ct =>
            {
                if (i == 0)
                    await SpinUntil(() => join.RetainedCount >= awaitRetained, ct)
                        .ConfigureAwait(false);
                return i < items.Length ? items[i++] : null;
            }
        );
    }

    /// <summary>
    /// The join body used by most tests: records <c>time=tag</c> for a match,
    /// <c>time=none</c> otherwise, and forwards the record downstream.
    /// </summary>
    private static SyncJoinOperator<RefBox<Tick>, RefBox<Span>, RefBox<string>> Record() =>
        (primary, secondary, _) =>
            ValueTask.FromResult<RefBox<string>?>(
                RefBox.Of(
                    $"{primary.Value.At.TotalMilliseconds:0}={secondary?.Value.Tag ?? "none"}"
                )
            );

    private static async Task SpinUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
    }

    private static int Collected(List<string> sink)
    {
        lock (sink)
            return sink.Count;
    }

    private static SinkNode<RefBox<string>> CollectInto(List<string> sink) =>
        new(
            "collect",
            (item, _) =>
            {
                lock (sink)
                    sink.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

    private static SyncJoinNode<RefBox<Tick>, RefBox<Span>, RefBox<string>> Join(
        SyncMatch policy,
        TimeSpan window,
        TimeSpan? maxStaleness = null
    ) => new("join", Record(), Keys, policy, window, maxStaleness);

    // ── Match policies ──────────────────────────────────────────────────

    [Fact]
    public async Task Within_MatchesTheIntervalContainingThePrimary()
    {
        var join = Join(SyncMatch.Within, window: Ms(10_000));

        var spans = new[]
        {
            RefBox.Of(new Span(Ms(0), Ms(100), "a")),
            RefBox.Of(new Span(Ms(100), Ms(200), "b")),
            RefBox.Of(new Span(Ms(200), Ms(300), "c")),
        };
        var ticks = new[]
        {
            RefBox.Of(new Tick(Ms(50))),
            RefBox.Of(new Tick(Ms(150))),
            RefBox.Of(new Tick(Ms(250))),
        };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 3, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(new[] { "50=a", "150=b", "250=c" }, got);
        Assert.All(spans, s => Assert.Equal(0, s.RefCount));
        Assert.All(ticks, t => Assert.Equal(0, t.RefCount));
    }

    [Fact]
    public async Task MostRecentAtOrBefore_UsesNewestSecondaryAtOrBeforeThePrimary()
    {
        var join = Join(SyncMatch.MostRecentAtOrBefore, window: Ms(10_000));

        // Point-valued secondaries: a detection stamped with the frame PTS it
        // ran on. Zero width, so Within would never match these.
        var spans = new[]
        {
            RefBox.Of(new Span(Ms(0), Ms(0), "det0")),
            RefBox.Of(new Span(Ms(100), Ms(100), "det1")),
        };
        var ticks = new[]
        {
            RefBox.Of(new Tick(Ms(50))),   // newest at-or-before is det0
            RefBox.Of(new Tick(Ms(150))),  // now det1
            RefBox.Of(new Tick(Ms(160))),  // still det1; detection is slower
        };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 2, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(new[] { "50=det0", "150=det1", "160=det1" }, got);
        Assert.All(spans, s => Assert.Equal(0, s.RefCount));
    }

    [Fact]
    public async Task ZeroWidthInterval_NeverMatchesUnderWithin()
    {
        var join = Join(SyncMatch.Within, window: Ms(10_000));

        var spans = new[] { RefBox.Of(new Span(Ms(100), Ms(100), "point")) };
        var ticks = new[] { RefBox.Of(new Tick(Ms(100))) };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 1, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(new[] { "100=none" }, got);
    }

    [Fact]
    public async Task MostRecentAtOrBefore_HonoursMaxStaleness()
    {
        var join = Join(
            SyncMatch.MostRecentAtOrBefore,
            window: Ms(10_000),
            maxStaleness: Ms(100)
        );

        var spans = new[] { RefBox.Of(new Span(Ms(0), Ms(0), "old")) };
        var ticks = new[]
        {
            RefBox.Of(new Tick(Ms(50))),   // 50ms stale, inside the bound
            RefBox.Of(new Tick(Ms(500))),  // 500ms stale, past it
        };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 1, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(new[] { "50=old", "500=none" }, got);
    }

    // ── No match emits ──────────────────────────────────────────────────

    [Fact]
    public async Task NoMatch_InvokesBodyWithNullAndStillEmits()
    {
        // No secondary at all. Every primary must still reach the sink — the
        // display cannot blank while waiting for the first transcription.
        var ticks = new[]
        {
            RefBox.Of(new Tick(Ms(10))),
            RefBox.Of(new Tick(Ms(20))),
        };

        var sawNull = 0;
        var body = new SyncJoinOperator<RefBox<Tick>, RefBox<Span>, RefBox<string>>(
            (primary, secondary, _) =>
            {
                if (secondary is null)
                    Interlocked.Increment(ref sawNull);
                return ValueTask.FromResult<RefBox<string>?>(
                    RefBox.Of($"{primary.Value.At.TotalMilliseconds:0}=none")
                );
            }
        );
        var nullJoin = new SyncJoinNode<RefBox<Tick>, RefBox<Span>, RefBox<string>>(
            "join",
            body,
            Keys,
            SyncMatch.Within,
            Ms(10_000)
        );

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", Array.Empty<RefBox<Span>>())).ToSecondary(nullJoin);
        graph.Pipeline(Emit("primary", ticks)).ToPrimary(nullJoin);
        graph.Pipeline(nullJoin.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(new[] { "10=none", "20=none" }, got);
        Assert.Equal(2, sawNull);
        Assert.All(ticks, t => Assert.Equal(0, t.RefCount));
    }

    // ── Window ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Eviction_DropsSecondariesAgedOutOfTheWindow()
    {
        // Window of 100ms: the [0,10] span is evicted once a primary at 500ms
        // advances the high-water mark past 110ms.
        var join = Join(SyncMatch.MostRecentAtOrBefore, window: Ms(100));

        var spans = new[] { RefBox.Of(new Span(Ms(0), Ms(10), "early")) };
        var ticks = new[]
        {
            RefBox.Of(new Tick(Ms(20))),   // still retained
            RefBox.Of(new Tick(Ms(500))),  // evicts, then finds nothing
        };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 1, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(new[] { "20=early", "500=none" }, got);
        Assert.All(spans, s => Assert.Equal(0, s.RefCount));
    }

    [Fact]
    public async Task ResetWindow_DropsRetainedSecondaries()
    {
        // The seek path: a pre-reset secondary must not match a post-reset
        // primary. Driven from the primary source so the reset lands between
        // two known primaries without a timing race.
        var join = Join(SyncMatch.Within, window: Ms(10_000));

        var spans = new[] { RefBox.Of(new Span(Ms(0), Ms(1000), "pre-seek")) };
        var ticks = new[]
        {
            RefBox.Of(new Tick(Ms(100))),  // before the reset: matches
            RefBox.Of(new Tick(Ms(200))),  // after it: window is empty
        };

        var got = new List<string>();

        // Gate the reset on the sink rather than on RetainedCount: the first
        // primary has demonstrably been matched once its output lands, so the
        // reset cannot race ahead of it. The source pump is single-threaded, so
        // `i` needs no synchronization.
        int i = 0;
        var primary = new SourceNode<RefBox<Tick>>(
            "primary",
            async ct =>
            {
                if (i == 0)
                {
                    await SpinUntil(() => join.RetainedCount >= 1, ct).ConfigureAwait(false);
                }
                else if (i == 1)
                {
                    await SpinUntil(() => Collected(got) >= 1, ct).ConfigureAwait(false);
                    join.ResetWindow();
                }
                return i < ticks.Length ? ticks[i++] : null;
            }
        );

        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(primary).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(new[] { "100=pre-seek", "200=none" }, got);
        Assert.All(spans, s => Assert.Equal(0, s.RefCount));
    }

    // ── Termination ─────────────────────────────────────────────────────

    [Fact]
    public async Task SecondaryEos_DoesNotEndTheJoin()
    {
        var join = Join(SyncMatch.MostRecentAtOrBefore, window: Ms(10_000));

        // One secondary, then EOS. Primaries keep flowing and keep matching it.
        var spans = new[] { RefBox.Of(new Span(Ms(0), Ms(0), "only")) };
        var ticks = new[]
        {
            RefBox.Of(new Tick(Ms(10))),
            RefBox.Of(new Tick(Ms(20))),
            RefBox.Of(new Tick(Ms(30))),
        };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 1, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(new[] { "10=only", "20=only", "30=only" }, got);
    }

    [Fact]
    public async Task PrimaryEos_TerminatesAgainstAnUnboundedSecondary()
    {
        // An unbounded secondary keeps the join pump alive past primary EOS, by
        // design: it must keep draining or the secondary's upstream blocks. The
        // graph therefore ends on cancellation, and must actually end — a pump
        // that waited on the secondary's writer completing would hang here.
        var join = Join(SyncMatch.MostRecentAtOrBefore, window: Ms(10_000));

        var emitted = 0;
        var unbounded = new SourceNode<RefBox<Span>>(
            "unbounded-secondary",
            async ct =>
            {
                await Task.Delay(1, ct).ConfigureAwait(false);
                var n = Interlocked.Increment(ref emitted);
                return RefBox.Of(new Span(Ms(0), Ms(0), $"s{n}"));
            }
        );

        var ticks = new[]
        {
            RefBox.Of(new Tick(Ms(10))),
            RefBox.Of(new Tick(Ms(20))),
        };

        var got = new List<string>();
        var allSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var sink = new SinkNode<RefBox<string>>(
            "collect",
            (item, _) =>
            {
                lock (got)
                {
                    got.Add(item.Value);
                    if (got.Count == ticks.Length)
                        allSeen.TrySetResult();
                }
                return ValueTask.CompletedTask;
            }
        );

        using var cts = new CancellationTokenSource();
        var graph = new GraphRunner();
        graph.Pipeline(unbounded).ToSecondary(join, EdgeOptions.Buffered(4));
        graph.Pipeline(EmitAfterRetained(join, 1, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(sink);

        var run = graph.RunAsync(cts.Token);

        try
        {
            await allSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Cancel before the CTS is disposed, or a timeout here leaves the
            // graph running against a disposed token for the rest of the run.
            cts.Cancel();
            throw;
        }
        Assert.Equal(2, got.Count);

        // Both primaries are out. Cancel so the unbounded source stops, and
        // assert the graph actually winds up rather than hanging on the
        // secondary.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(10))
        );

        Assert.Equal(0, join.RetainedCount);
        Assert.All(ticks, t => Assert.Equal(0, t.RefCount));
    }

    [Fact]
    public async Task PrimaryEos_DrainsALaggingSecondarySoTheGraphCompletes()
    {
        // A finite secondary with more pending items than the edge's free
        // capacity. If the pump stopped reading at primary EOS, that upstream
        // would block in WriteAsync and RunAsync would never return — which is
        // what SubstrateSession waits on before raising OnEndOfStream.
        var join = Join(SyncMatch.MostRecentAtOrBefore, window: Ms(10_000));

        var spans = Enumerable
            .Range(0, 12)
            .Select(i => RefBox.Of(new Span(Ms(i * 10), Ms(i * 10), $"s{i}")))
            .ToArray();

        int s = 0;
        var lagging = new SourceNode<RefBox<Span>>(
            "lagging-secondary",
            async ct =>
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
                return s < spans.Length ? spans[s++] : null;
            }
        );

        var ticks = new[] { RefBox.Of(new Tick(Ms(0))), RefBox.Of(new Tick(Ms(10))) };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(lagging).ToSecondary(join, EdgeOptions.Buffered(4));
        graph.Pipeline(EmitAfterRetained(join, 1, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, got.Count);
        Assert.Equal(0, join.RetainedCount);
        // Every secondary is accounted for, admitted or discarded post-EOS.
        Assert.All(spans, x => Assert.Equal(0, x.RefCount));
        Assert.All(ticks, t => Assert.Equal(0, t.RefCount));
    }

    // ── Failure policy ──────────────────────────────────────────────────

    private sealed class BoomException : Exception { }

    [Theory]
    [InlineData(FailureResponse.Propagate)]
    [InlineData(FailureResponse.Discard)]
    public async Task BodyThrow_HonoursTheFailurePolicy(FailureResponse policy)
    {
        var ticks = new[] { RefBox.Of(new Tick(Ms(10))), RefBox.Of(new Tick(Ms(20))) };
        var calls = 0;

        var join = new SyncJoinNode<RefBox<Tick>, RefBox<Span>, RefBox<string>>(
            "join",
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new BoomException();
            },
            Keys,
            SyncMatch.Within,
            Ms(10_000),
            onError: policy
        );

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", Array.Empty<RefBox<Span>>())).ToSecondary(join);
        graph.Pipeline(Emit("primary", ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        if (policy == FailureResponse.Propagate)
        {
            await Assert.ThrowsAsync<BoomException>(() => graph.RunAsync());
            Assert.Equal(1, calls);
        }
        else
        {
            await graph.RunAsync();
            Assert.Equal(2, calls);
        }

        Assert.Empty(got);
        Assert.All(ticks, t => Assert.Equal(0, t.RefCount));
    }

    [Fact]
    public async Task SecondaryKeySelectorThrow_FaultsPromptlyUnderPropagate()
    {
        // The fault must reach the caller even though the primary never EOSes
        // on its own: cancelling at the fault site is what stops the primary,
        // and the secondary's exception has to survive that cancellation.
        var spans = new[] { RefBox.Of(new Span(Ms(0), Ms(10), "bad")) };

        var join = new SyncJoinNode<RefBox<Tick>, RefBox<Span>, RefBox<string>>(
            "join",
            Record(),
            new SyncJoinKeys<RefBox<Tick>, RefBox<Span>>(
                p => p.Value.At,
                _ => throw new BoomException()
            ),
            SyncMatch.MostRecentAtOrBefore,
            Ms(10_000)
        );

        var endless = new SourceNode<RefBox<Tick>>(
            "endless-primary",
            async ct =>
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
                return RefBox.Of(new Tick(Ms(0)));
            }
        );

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(4));
        graph.Pipeline(endless).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await Assert.ThrowsAsync<BoomException>(
            () => graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15))
        );

        Assert.All(spans, x => Assert.Equal(0, x.RefCount));
    }

    // ── Pass-through ────────────────────────────────────────────────────

    [Fact]
    public async Task BodyReturningTheSecondary_ForwardsItRatherThanDisposingIt()
    {
        // The example relies on primary pass-through; this is the other half of
        // the same rule. Disposing the returned secondary here would release the
        // window's own ref, and the next Evict would throw on a live entry.
        var join = new SyncJoinNode<RefBox<Tick>, RefBox<Span>, RefBox<Span>>(
            "join",
            (_, secondary, _) => ValueTask.FromResult(secondary),
            Keys,
            SyncMatch.MostRecentAtOrBefore,
            Ms(10_000)
        );

        var spans = new[] { RefBox.Of(new Span(Ms(0), Ms(0), "shared")) };
        var ticks = new[] { RefBox.Of(new Tick(Ms(10))), RefBox.Of(new Tick(Ms(20))) };

        var seen = new List<string>();
        var sink = new SinkNode<RefBox<Span>>(
            "collect",
            (item, _) =>
            {
                lock (seen)
                    seen.Add(item.Value.Tag);
                return ValueTask.CompletedTask;
            }
        );

        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 1, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(sink);

        await graph.RunAsync();

        Assert.Equal(new[] { "shared", "shared" }, seen);
        Assert.All(spans, x => Assert.Equal(0, x.RefCount));
        Assert.All(ticks, t => Assert.Equal(0, t.RefCount));
    }

    // ── Window ordering ─────────────────────────────────────────────────

    [Fact]
    public async Task Eviction_DoesNotLetALongIntervalShieldExpiredEntries()
    {
        // Entries are ordered by From, not by To. A long span admitted first
        // must not stop the eviction scan and keep later, expired entries alive.
        var join = Join(SyncMatch.MostRecentAtOrBefore, window: Ms(100));

        var spans = new[]
        {
            RefBox.Of(new Span(Ms(0), Ms(100_000), "long")),  // To is far ahead
            RefBox.Of(new Span(Ms(10), Ms(20), "short")),     // aged out at t=500
        };
        var ticks = new[] { RefBox.Of(new Tick(Ms(500))) };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 2, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        // "short" is evicted despite sitting behind "long"; only "long" remains
        // to match, and it is the newest entry at-or-before 500ms that survives.
        Assert.Equal(new[] { "500=long" }, got);
        Assert.All(spans, x => Assert.Equal(0, x.RefCount));
    }

    // ── Re-run ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SecondRunAsync_StartsFromAnEmptyWindow()
    {
        // RepeatMode.One rewinds by re-running the graph instance. A join must
        // not match post-rewind primaries against pre-rewind secondaries.
        var join = Join(SyncMatch.MostRecentAtOrBefore, window: Ms(10_000));

        var spans = new[] { RefBox.Of(new Span(Ms(0), Ms(0), "first-run")) };
        var ticks = new[] { RefBox.Of(new Tick(Ms(10))) };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 1, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();
        Assert.Equal(new[] { "10=first-run" }, got);
        Assert.Equal(0, join.RetainedCount);

        // Both sources are exhausted, so the second run has no secondary at all.
        // The primary must still be emitted, unmatched.
        got.Clear();
        await graph.RunAsync();

        Assert.Empty(got);
        Assert.Equal(0, join.RetainedCount);
        Assert.All(spans, x => Assert.Equal(0, x.RefCount));
    }

    // ── Ownership ───────────────────────────────────────────────────────

    [Fact]
    public async Task EveryRefSettlesToZero()
    {
        var join = Join(SyncMatch.Within, window: Ms(50));

        // Mixture of matched, unmatched and evicted secondaries in one run.
        var spans = new[]
        {
            RefBox.Of(new Span(Ms(0), Ms(20), "evicted")),
            RefBox.Of(new Span(Ms(100), Ms(200), "matched")),
            RefBox.Of(new Span(Ms(900), Ms(950), "never-reached")),
        };
        var ticks = new[]
        {
            RefBox.Of(new Tick(Ms(150))),  // matches "matched"
            RefBox.Of(new Tick(Ms(400))),  // matches nothing
        };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 3, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(new[] { "150=matched", "400=none" }, got);
        Assert.All(spans, s => Assert.Equal(0, s.RefCount));
        Assert.All(ticks, t => Assert.Equal(0, t.RefCount));
        Assert.Equal(0, join.RetainedCount);
    }
}
