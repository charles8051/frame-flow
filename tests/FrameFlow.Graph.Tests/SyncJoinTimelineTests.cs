using FrameFlow.Graph.Core;
using Xunit;

// `Graph` is both a namespace (FrameFlow.Graph) and a type
// (FrameFlow.Graph.Graph). Alias the type so the tests can sit in the
// conventional FrameFlow.Graph.Tests namespace without the clash.
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}"/> follows the timeline of each input
/// (ADR-0084, decision 5). The window tests are synchronous and take no clock and no pump; the two
/// graph tests prove the pump hands each input's timeline to the window.
/// </summary>
public sealed class SyncJoinTimelineTests
{
    private static readonly TimeSpan Window = Ms(100);
    private static readonly TimeSpan Lead = Ms(50);

    // ── The window ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1020)]
    [InlineData(960)]
    public void APrimaryOnANewTimeline_ReleasesTheWindow_WhereverItsTimeGoes(int nextMs)
    {
        // Forward, or back by less than the window: the presentation-time rule sees no break.
        var window = new SecondaryWindow<RefBox<int>>();
        var old = RefBox.Of(950);
        Assert.Null(Admit(window, old));
        Assert.Equal(950, Match(window, 1000, TimelineStep.Untracked));

        Assert.Null(Match(window, nextMs, TimelineStep.NewTimeline));
        Assert.Equal(0, old.RefCount);
        Assert.Equal(0, window.Count);
    }

    [Fact]
    public void OnATimelineThePrimaryContinues_AStepBackIsNotABreak()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        var entry = RefBox.Of(1000);
        Assert.Null(Admit(window, entry));
        Assert.Equal(1000, Match(window, 1000, TimelineStep.Untracked));

        // More than the window behind: a primary on None would start a new timeline here.
        Assert.Null(Match(window, 0, TimelineStep.Continues));
        Assert.Equal(1, entry.RefCount);
        Assert.Equal(1000, Match(window, 1010, TimelineStep.Continues));

        window.Clear();
        Assert.Equal(0, entry.RefCount);
    }

    [Fact]
    public void AfterABreakBack_ASecondaryFromTheLeftTimelineIsReleasedRatherThanHeldOnTheLead()
    {
        // A fork-rejoin: the secondary branch lags the trunk, so its items from before the break
        // are still on the edge when the primary's next timeline starts.
        var window = new SecondaryWindow<RefBox<int>>();
        Assert.Null(Admit(window, RefBox.Of(1000), Lead));
        Assert.Equal(1000, Match(window, 1000, TimelineStep.Untracked));

        var held = RefBox.Of(1100);
        var room = Admit(window, held, Lead);
        Assert.NotNull(room);

        Assert.Null(Match(window, 0, TimelineStep.NewTimeline));

        Assert.True(room.IsCompleted);
        Assert.Null(Admit(window, held, Lead));
        Assert.Equal(0, held.RefCount);

        var inFlight = RefBox.Of(1020);
        Assert.Null(Admit(window, inFlight, Lead));
        Assert.Equal(0, inFlight.RefCount);

        var fresh = RefBox.Of(5);
        Assert.Null(Admit(window, fresh, Lead));
        Assert.Equal(5, Match(window, 10, TimelineStep.Continues));

        window.Clear();
        Assert.Equal(0, fresh.RefCount);
    }

    [Fact]
    public void ReleaseEntries_ReleasesWhatTheWindowHolds_AndKeepsThePrimarysPlace()
    {
        var window = new SecondaryWindow<RefBox<int>>();
        var old = RefBox.Of(1000);
        Assert.Null(Admit(window, old));
        Assert.Equal(1000, Match(window, 1000, TimelineStep.Untracked));

        window.ReleaseEntries();
        Assert.Equal(0, old.RefCount);

        // The primary is still at 1000, so one more than the window behind it is late.
        var late = RefBox.Of(850);
        Assert.Null(Admit(window, late));
        Assert.Equal(0, late.RefCount);

        var fresh = RefBox.Of(1010);
        Assert.Null(Admit(window, fresh));
        Assert.Equal(1010, Match(window, 1010, TimelineStep.Untracked));

        window.Clear();
        Assert.Equal(0, fresh.RefCount);
    }

    // ── Where the join reads a timeline ─────────────────────────────────

    [Fact]
    public void ByDefault_TheJoinReadsTheTimelineOfAFrameOrABuffer()
    {
        var timeline = TimelineId.Next();
        var keys = new SyncJoinKeys<IRefCounted, IRefCounted>(_ => default, _ => default);

        Assert.Equal(timeline, keys.TimelineOfPrimary(new StampedFrame(timeline)));
        Assert.Equal(timeline, keys.TimelineOfSecondary(new StampedBuffer(timeline)));
        Assert.Equal(TimelineId.None, keys.TimelineOfPrimary(RefBox.Of(timeline)));
    }

    [Fact]
    public void ASelectorNamesTheTimelineOfAnItemThatCarriesItItsOwnWay()
    {
        var frameTimeline = TimelineId.Next();
        var named = TimelineId.Next();
        var keys = new SyncJoinKeys<IRefCounted, IRefCounted>(_ => default, _ => default)
        {
            PrimaryTimeline = _ => named,
            SecondaryTimeline = item => ((RefBox<TimelineId>)item).Value,
        };

        Assert.Equal(named, keys.TimelineOfPrimary(new StampedFrame(frameTimeline)));
        Assert.Equal(named, keys.TimelineOfSecondary(RefBox.Of(named)));
    }

    // ── The pump ────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePump_ReleasesTheWindowWhenThePrimaryStartsANewTimeline()
    {
        var a = TimelineId.Next();
        var b = TimelineId.Next();
        var join = Join();
        var span = RefBox.Of(new Span(Ms(0), Ms(1000), "old", TimelineId.None));
        var ticks = new[] { RefBox.Of(new Tick(Ms(100), a)), RefBox.Of(new Tick(Ms(200), b)) };

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", span)).ToSecondary(join, EdgeOptions.Buffered(8));
        graph.Pipeline(EmitAfterRetained(join, 1, ticks)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        // 200 ms is inside the span, and forward of 100 ms: only the timeline says it broke.
        Assert.Equal(["100=old", "200=none"], got);
        Assert.Equal(0, span.RefCount);
        Assert.All(ticks, t => Assert.Equal(0, t.RefCount));
    }

    [Fact]
    public async Task ThePump_ReleasesWhatTheSecondaryLeftWhenItStartsANewTimeline()
    {
        var x = TimelineId.Next();
        var y = TimelineId.Next();
        var join = Join();
        var spans = new[]
        {
            RefBox.Of(new Span(Ms(0), Ms(1000), "old", x)),
            RefBox.Of(new Span(Ms(2000), Ms(2100), "new", y)),
            RefBox.Of(new Span(Ms(2100), Ms(2200), "newer", y)),
        };
        var tick = RefBox.Of(new Tick(Ms(50), TimelineId.None));

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(Emit("secondary", spans)).ToSecondary(join, EdgeOptions.Buffered(8));

        // Two retained means the third span is in: the first was released when the second came,
        // or, without that, the window holds the first two and the primary can match the first.
        graph.Pipeline(EmitAfterRetained(join, 2, tick)).ToPrimary(join);
        graph.Pipeline(join.Output).To(CollectInto(got));

        await graph.RunAsync();

        Assert.Equal(["50=none"], got);
        Assert.All(spans, s => Assert.Equal(0, s.RefCount));
        Assert.Equal(0, tick.RefCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private sealed record Tick(TimeSpan At, TimelineId Timeline);

    private sealed record Span(TimeSpan From, TimeSpan To, string Tag, TimelineId Timeline);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static Task? Admit(SecondaryWindow<RefBox<int>> window, RefBox<int> point, TimeSpan? maxLead = null) =>
        window.TryAdmit(
            point,
            Ms(point.Value),
            Ms(point.Value),
            Window,
            maxLead,
            maxRetained: null,
            SyncMatch.MostRecentAtOrBefore,
            TimeSpan.MaxValue
        );

    private static int? Match(SecondaryWindow<RefBox<int>> window, int ms, TimelineStep step)
    {
        using var match = window.AdvanceAndMatch(
            Ms(ms),
            SyncMatch.MostRecentAtOrBefore,
            Window,
            TimeSpan.MaxValue,
            step
        );
        return match?.Value;
    }

    private static SyncJoinNode<RefBox<Tick>, RefBox<Span>, RefBox<string>> Join() =>
        new(
            "join",
            (primary, secondary, _) =>
                ValueTask.FromResult<RefBox<string>?>(
                    RefBox.Of($"{primary.Value.At.TotalMilliseconds:0}={secondary?.Value.Tag ?? "none"}")
                ),
            new SyncJoinKeys<RefBox<Tick>, RefBox<Span>>(p => p.Value.At, s => (s.Value.From, s.Value.To))
            {
                PrimaryTimeline = p => p.Value.Timeline,
                SecondaryTimeline = s => s.Value.Timeline,
            },
            SyncMatch.Within,
            window: Ms(10_000)
        );

    private static SourceNode<T> Emit<T>(string id, params T[] items)
        where T : class, IRefCounted
    {
        int i = 0;
        return new SourceNode<T>(id, _ => ValueTask.FromResult<T?>(i < items.Length ? items[i++] : null));
    }

    /// <summary>
    /// A primary source that waits, once, for the join's window to hold
    /// <paramref name="awaitRetained"/> secondaries, then emits: the pattern
    /// <see cref="SyncJoinTests"/> uses to order the secondaries before the primaries.
    /// </summary>
    /// <remarks>
    /// A condition wait on state the join exposes, yielding between checks, with no duration
    /// involved (ADR-0072).
    /// </remarks>
    private static SourceNode<RefBox<Tick>> EmitAfterRetained(
        SyncJoinNode<RefBox<Tick>, RefBox<Span>, RefBox<string>> join,
        int awaitRetained,
        params RefBox<Tick>[] items
    )
    {
        int i = 0;
        return new SourceNode<RefBox<Tick>>(
            "primary",
            async ct =>
            {
                if (i == 0)
                {
                    while (join.RetainedCount < awaitRetained)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                }
                return i < items.Length ? items[i++] : null;
            }
        );
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

    private sealed class StampedFrame(TimelineId timeline) : IFrame, IRefCounted
    {
        public int Width => 1;

        public int Height => 1;

        public TimeSpan Timestamp => TimeSpan.Zero;

        public TimelineId Timeline => timeline;

        public IRefCounted AddRef() => this;

        public void Dispose() { }
    }

    private sealed class StampedBuffer(TimelineId timeline) : IAudioBuffer
    {
        public TimeSpan Timestamp => TimeSpan.Zero;

        public TimeSpan Duration => TimeSpan.Zero;

        public int SampleRate => 48_000;

        public int ChannelCount => 1;

        public int FrameCount => 0;

        public AudioSampleFormat SampleFormat => AudioSampleFormat.Float32;

        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public TimelineId Timeline => timeline;

        public IAudioBuffer AddRef() => this;

        public void Dispose() { }
    }
}
