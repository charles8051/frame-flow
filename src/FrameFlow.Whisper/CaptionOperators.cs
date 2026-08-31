// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using FrameFlow.Graph;

namespace FrameFlow.Whisper;

/// <summary>
/// Port of <c>FrameFlow.Whisper.CaptionPipelineExtensions</c> to the
/// new primitive-set substrate. The point of this port is to demonstrate
/// how <see cref="MultiOperatorNode{TIn, TOut}"/> obviates the
/// Channel-bridge boilerplate the old substrate required for 1→N
/// operators.
/// </summary>
/// <remarks>
/// <para>
/// <b>Diff summary.</b> The old <c>SplitOnPunctuation</c> needed ~55
/// lines of plumbing: an unbounded channel, a driver task running
/// <c>upstream.Observe(...).RunAsync</c>, try/catch for cancellation
/// and exception forwarding to the channel writer, and the
/// downstream <c>await foreach</c> over the reader. The actual
/// splitting logic (~30 lines in <c>SplitCaption</c>) was buried
/// inside.
/// </para>
/// <para>
/// The new <c>SplitOnPunctuation</c> is the splitting logic with
/// <c>yield return</c>. That's it. ~12 lines of factory wrapping +
/// ~30 lines of unchanged splitting helper. The substrate handles
/// cancellation, exception propagation, ref ownership, and channel
/// management automatically because <c>MultiOperator&lt;TIn, TOut&gt;</c>
/// is an <c>IAsyncEnumerable</c>-shaped delegate.
/// </para>
/// </remarks>
public static class CaptionOperators
{
    // ── SplitOnPunctuation ──────────────────────────────────────────

    /// <summary>
    /// Splits each upstream caption at internal punctuation
    /// (<c>. , ! ? ; :</c>) into multiple shorter captions. Each piece
    /// gets a sub-range of the original <c>[From, To]</c> proportional
    /// to its character count. Captions with no internal punctuation
    /// pass through unchanged.
    /// </summary>
    public static MultiOperatorNode<CaptionRef, CaptionRef> SplitOnPunctuation(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new MultiOperatorNode<CaptionRef, CaptionRef>(id, SplitImpl);

        // Local async iterator: just iterates the splitter and yields
        // each piece wrapped in a new CaptionRef. No bridge, no driver
        // task, no try/catch dance — the substrate's pump does all of it.
        static async IAsyncEnumerable<CaptionRef> SplitImpl(
            CaptionRef input,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            await Task.CompletedTask;
            foreach (var piece in SplitCaption(input.Value))
            {
                ct.ThrowIfCancellationRequested();
                yield return new CaptionRef(piece);
            }
        }
    }

    private static IEnumerable<Caption> SplitCaption(Caption original)
    {
        // Unchanged from FrameFlow.Whisper.CaptionPipelineExtensions —
        // domain logic doesn't care about substrate shape.
        var pieces = SplitPattern
            .Split(original.Text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (pieces.Length <= 1)
        {
            yield return original;
            yield break;
        }

        var totalChars = pieces.Sum(p => p.Length);
        if (totalChars == 0)
        {
            yield return original;
            yield break;
        }

        var totalTicks = (original.To - original.From).Ticks;
        var cursor = original.From;
        long allocatedTicks = 0;

        for (int i = 0; i < pieces.Length; i++)
        {
            long pieceTicks =
                i == pieces.Length - 1
                    ? totalTicks - allocatedTicks
                    : totalTicks * pieces[i].Length / totalChars;
            var pieceDuration = TimeSpan.FromTicks(pieceTicks);
            yield return new Caption(cursor, cursor + pieceDuration, pieces[i]);
            cursor += pieceDuration;
            allocatedTicks += pieceTicks;
        }
    }

    private static readonly Regex SplitPattern = new(
        @"(?<=[.,!?;:])\s+",
        RegexOptions.Compiled
    );

    // ── AnimatedReveal ──────────────────────────────────────────────

    /// <summary>
    /// Reveals each upstream caption progressively word-by-word over
    /// wallclock time, emitting a sequence of progressively-longer
    /// sub-captions at <paramref name="wordsPerSecond"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reveal is a visual illusion of liveness, not a latency fix.
    /// Animating over the next reveal-duration seconds turns
    /// "burst → flash → vanish" into "type → settle → vanish", which
    /// reads as streaming even when the underlying ASR is still batch.
    /// </para>
    /// <para>
    /// <b>Pump backpressure.</b> The reveal awaits <c>Task.Delay</c>
    /// between each yielded sub-caption, holding the operator's pump
    /// for the reveal duration. Subsequent input captions queue at the
    /// substrate's edge buffer and are revealed sequentially once the
    /// current reveal completes — same back-pressure characteristic as
    /// the old version, but enforced by the substrate instead of the
    /// bridge-channel writer.
    /// </para>
    /// </remarks>
    public static MultiOperatorNode<CaptionRef, CaptionRef> AnimatedReveal(
        string id,
        double wordsPerSecond = 5.0
    )
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wordsPerSecond);
        return new MultiOperatorNode<CaptionRef, CaptionRef>(id, RevealImpl);

        async IAsyncEnumerable<CaptionRef> RevealImpl(
            CaptionRef input,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            var caption = input.Value;
            var words = caption.Text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            if (words.Length <= 1)
            {
                yield return new CaptionRef(caption);
                yield break;
            }

            var totalDuration = caption.To - caption.From;
            var naturalReveal = TimeSpan.FromSeconds(words.Length / wordsPerSecond);
            var revealDuration = naturalReveal < totalDuration ? naturalReveal : totalDuration;
            var perWord = revealDuration / words.Length;

            var accumulated = new StringBuilder(caption.Text.Length);
            var cursor = caption.From;

            for (int i = 0; i < words.Length; i++)
            {
                if (i > 0)
                    accumulated.Append(' ');
                accumulated.Append(words[i]);

                var subTo = i == words.Length - 1 ? caption.To : cursor + perWord;
                yield return new CaptionRef(new Caption(cursor, subTo, accumulated.ToString()));
                cursor = subTo;

                if (i < words.Length - 1)
                {
                    await Task.Delay(perWord, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
