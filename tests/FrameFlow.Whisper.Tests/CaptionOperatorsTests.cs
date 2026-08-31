namespace FrameFlow.Whisper.Tests;

/// <summary>
/// Pins the behavioural-equivalence of the ported Caption operators
/// against the old <c>CaptionPipelineExtensions</c> implementations.
/// The port should produce identical output for identical input —
/// the only thing that changed is the substrate, not the splitting /
/// reveal logic itself.
/// </summary>
public sealed class CaptionOperatorsTests
{
    // ─── SplitOnPunctuation ──────────────────────────────────────────

    [Fact]
    public async Task SplitOnPunctuation_NoInternalPunctuation_PassesThroughUnchanged()
    {
        var input = new Caption(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Hello world");
        var captured = await RunSplit(input);

        Assert.Single(captured);
        Assert.Equal(input.Text, captured[0].Text);
        Assert.Equal(input.From, captured[0].From);
        Assert.Equal(input.To, captured[0].To);
    }

    [Fact]
    public async Task SplitOnPunctuation_TwoPhrases_SplitsAtComma_WithProportionalDuration()
    {
        // "Hello there, friend" — 12 chars + 7 chars (post-trim).
        // Total duration 4s → first piece 12/19 ≈ 2.526s, second 7/19 ≈ 1.473s.
        var input = new Caption(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Hello there, friend");
        var captured = await RunSplit(input);

        Assert.Equal(2, captured.Count);
        Assert.Equal("Hello there,", captured[0].Text);
        Assert.Equal("friend", captured[1].Text);
        Assert.Equal(TimeSpan.Zero, captured[0].From);
        // Last piece absorbs rounding, so end of second piece == input.To exactly.
        Assert.Equal(input.To, captured[^1].To);
        Assert.Equal(captured[0].To, captured[1].From);
    }

    [Fact]
    public async Task SplitOnPunctuation_MultipleSegments_AllEmitted()
    {
        var input = new Caption(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(6),
            "One. Two? Three! Four."
        );
        var captured = await RunSplit(input);

        Assert.Equal(4, captured.Count);
        Assert.Equal("One.", captured[0].Text);
        Assert.Equal("Two?", captured[1].Text);
        Assert.Equal("Three!", captured[2].Text);
        Assert.Equal("Four.", captured[3].Text);
        Assert.Equal(input.To, captured[^1].To);
    }

    // ─── AnimatedReveal ──────────────────────────────────────────────

    [Fact]
    public async Task AnimatedReveal_SingleWord_PassesThroughUnchanged()
    {
        var input = new Caption(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello");
        var captured = await RunReveal(input, wordsPerSecond: 5.0);

        Assert.Single(captured);
        Assert.Equal("Hello", captured[0].Text);
    }

    [Fact]
    public async Task AnimatedReveal_MultiWord_EmitsProgressivePrefixes()
    {
        var input = new Caption(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            "One two three four"
        );
        // 4 words, capped at 1s caption duration → reveal at 4 wps,
        // 0.25s per word. Total reveal: 4 sub-captions.
        var captured = await RunReveal(input, wordsPerSecond: 20.0);

        Assert.Equal(4, captured.Count);
        Assert.Equal("One", captured[0].Text);
        Assert.Equal("One two", captured[1].Text);
        Assert.Equal("One two three", captured[2].Text);
        Assert.Equal("One two three four", captured[3].Text);
        // Last sub-caption stretches to the original To.
        Assert.Equal(input.To, captured[^1].To);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static async Task<List<Caption>> RunSplit(Caption input)
    {
        var captured = new List<Caption>();
        var emitted = false;

        var source = new SourceNode<CaptionRef>(
            "src",
            (ct) =>
            {
                if (emitted)
                    return ValueTask.FromResult<CaptionRef?>(null);
                emitted = true;
                return ValueTask.FromResult<CaptionRef?>(new CaptionRef(input));
            }
        );

        var split = CaptionOperators.SplitOnPunctuation("split");

        var sink = new SinkNode<CaptionRef>(
            "sink",
            (item, ct) =>
            {
                lock (captured)
                    captured.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

        var graph = new Graph.Graph();
        graph.Pipeline(source).Then(split).To(sink);
        await graph.RunAsync();

        return captured;
    }

    private static async Task<List<Caption>> RunReveal(Caption input, double wordsPerSecond)
    {
        var captured = new List<Caption>();
        var emitted = false;

        var source = new SourceNode<CaptionRef>(
            "src",
            (ct) =>
            {
                if (emitted)
                    return ValueTask.FromResult<CaptionRef?>(null);
                emitted = true;
                return ValueTask.FromResult<CaptionRef?>(new CaptionRef(input));
            }
        );

        var reveal = CaptionOperators.AnimatedReveal("reveal", wordsPerSecond);

        var sink = new SinkNode<CaptionRef>(
            "sink",
            (item, ct) =>
            {
                lock (captured)
                    captured.Add(item.Value);
                return ValueTask.CompletedTask;
            }
        );

        var graph = new Graph.Graph();
        graph.Pipeline(source).Then(reveal).To(sink);
        await graph.RunAsync();

        return captured;
    }
}
