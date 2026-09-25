using FrameFlow.Graph;
using FrameFlow.Media.Tests.Doubles;
using Xunit;
using GraphRunner = FrameFlow.Graph.Graph;

namespace FrameFlow.Media.Tests;

/// <summary>
/// <see cref="CpuVideoFrame"/> is shared by count (ADR-0080), so the graph can hand one frame to
/// more than one holder without a copy: a join can match it for several primaries (#91), and a
/// fan-out can reach two branches without a cloner. Each run releases the frame's buffer once.
/// </summary>
public sealed class CpuVideoFrameSharingTests
{
    [Fact]
    public async Task JoinWithACpuFrameSecondary_MatchesItForMoreThanOnePrimary()
    {
        var pool = new CountingArrayPool<byte>();
        var frame = NewFrame(pool);

        var join = new SyncJoinNode<RefBox<int>, IVideoFrame, RefBox<string>>(
            "join",
            (primary, secondary, _) =>
                ValueTask.FromResult<RefBox<string>?>(
                    RefBox.Of($"{primary.Value}={(secondary is null ? "none" : "frame")}")
                ),
            new SyncJoinKeys<RefBox<int>, IVideoFrame>(
                p => TimeSpan.FromMilliseconds(p.Value),
                s => (s.Timestamp, s.Timestamp)
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: TimeSpan.FromSeconds(10),
            maxLead: TimeSpan.FromSeconds(10)
        );

        int secondaryPulls = 0;
        var secondary = new SourceNode<IVideoFrame>(
            "secondary",
            _ => ValueTask.FromResult<IVideoFrame?>(
                secondaryPulls++ == 0 ? frame : null
            )
        );

        // Both primaries match the one retained secondary, so the window hands it out twice.
        // The first pull waits until the secondary is retained, so neither can miss it.
        var ticks = new[] { RefBox.Of(10), RefBox.Of(20) };
        int next = 0;
        var primary = new SourceNode<RefBox<int>>(
            "primary",
            async ct =>
            {
                if (next == 0)
                    await SpinUntil(() => join.RetainedCount >= 1, ct).ConfigureAwait(false);
                return next < ticks.Length ? ticks[next++] : null;
            }
        );

        var got = new List<string>();
        var graph = new GraphRunner();
        graph.Pipeline(secondary).ToSecondary(join, EdgeOptions.Buffered(4));
        graph.Pipeline(primary).ToPrimary(join);
        graph.Pipeline(join.Output).To(Collect(got));

        await graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(new[] { "10=frame", "20=frame" }, got);
        Assert.Equal(1, pool.Returns);
        Assert.Null(frame.AsCpu());
    }

    [Fact]
    public async Task FanOutWithoutACloner_GivesBothBranchesTheFrame_AndReleasesItOnce()
    {
        var pool = new CountingArrayPool<byte>();
        var frame = NewFrame(pool);

        int pulls = 0;
        var source = new SourceNode<IVideoFrame>(
            "source",
            _ => ValueTask.FromResult<IVideoFrame?>(pulls++ == 0 ? frame : null)
        );

        int trunkReads = 0;
        int branchReads = 0;
        var graph = new GraphRunner();
        var head = graph.Pipeline(source);
        head.Branch(EdgeOptions.Buffered(4)).To(Read("branch", () => branchReads++));
        head.To(Read("trunk", () => trunkReads++));

        await graph.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, trunkReads);
        Assert.Equal(1, branchReads);
        Assert.Equal(1, pool.Returns);
        Assert.Null(frame.AsCpu());
    }

    /// <summary>A sink that counts the frames it could read while it held them.</summary>
    private static SinkNode<IVideoFrame> Read(string id, Action onReadable) =>
        new(
            id,
            (item, _) =>
            {
                if (item.AsCpu() is not null)
                    onReadable();
                return ValueTask.CompletedTask;
            }
        );

    private static SinkNode<RefBox<string>> Collect(List<string> into) =>
        new(
            "collect",
            (item, _) =>
            {
                lock (into)
                    into.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

    /// <summary>
    /// Waits for a state the join exposes. It yields between checks and involves no duration
    /// (ADR-0072); the join raises no event when it retains an item, so its count is what there
    /// is to observe.
    /// </summary>
    private static async Task SpinUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static CpuVideoFrame NewFrame(CountingArrayPool<byte> pool) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            2,
            2,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            static (_, _) => { },
            pool
        );
}
