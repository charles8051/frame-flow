using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Proves <see cref="PlaybackInvariants.VideoFramePixelsMatchReference"/>
/// fails on the corruption it claims to detect, and keeps that detection
/// separate from the frame loss a pipeline under load produces on purpose.
/// </summary>
/// <remarks>
/// <para>
/// The invariant went from written to called without anyone watching it go
/// red. A gate nobody has seen fail is indistinguishable from a gate that
/// cannot fail, and this one could not: it compared frame counts first, so on
/// every run where the runtime shed anything it stopped before reading a
/// pixel. The clip from #134 delivers 525 frames against a 997-frame
/// reference, so the count check is exactly what stood between the assertion
/// and the bug it was written for.
/// </para>
/// <para>
/// These are pure data assertions: no FFmpeg, no corpus, no playback. They
/// state the ways a capture can diverge from a reference decode and assert
/// each is caught, including the case that matters most — corruption found
/// while loss is inside its budget.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class PlaybackInvariantsVideoTests
{
    private const int Width = 4;
    private const int Height = 4;
    private const int BytesPerFrame = Width * Height;

    [Fact]
    public void AnIdenticalSequenceMatches()
    {
        PlaybackInvariants.VideoFramePixelsMatchReference(Sequence(5), Sequence(5));
    }

    [Fact]
    public void ASingleAlteredPixelByteIsCaught()
    {
        var reference = Sequence(5);
        var capture = Sequence(5);

        // One byte, in the middle frame, off by one. The smallest possible
        // version of what #134 produced at macroblock scale.
        capture[2].Pixels[7] ^= 0x01;

        var failure = Fails(capture, reference);

        Assert.Contains("pixel bytes diverge", failure.Message, StringComparison.Ordinal);
        Assert.Contains("first at byte 7", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case the count check used to hide. A run that legitimately shed
    /// half its frames still has to deliver correct pictures for the half it
    /// kept.
    /// </summary>
    [Fact]
    public void CorruptionIsCaughtWhileLossIsInsideItsBudget()
    {
        var reference = Sequence(10);
        var capture = EveryOtherFrameOf(reference);
        capture[3].Pixels[2] ^= 0xFF;

        var failure = Fails(capture, reference, maxFrameLossRatio: 0.6);

        Assert.Contains("pixel bytes diverge", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LossInsideItsBudgetIsNotAFailure()
    {
        var reference = Sequence(10);
        var capture = EveryOtherFrameOf(reference);

        PlaybackInvariants.VideoFramePixelsMatchReference(
            capture,
            reference,
            maxFrameLossRatio: 0.6
        );
    }

    [Fact]
    public void LossBeyondItsBudgetIsAFailure()
    {
        var reference = Sequence(10);
        var capture = EveryOtherFrameOf(reference);

        var failure = Fails(capture, reference, maxFrameLossRatio: 0.4);

        Assert.Contains("frame loss", failure.Message, StringComparison.Ordinal);
        Assert.Contains("5 missing", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADroppedFrameIsCaughtAgainstTheDefaultZeroBudget()
    {
        var reference = Sequence(5);
        var capture = Sequence(5);
        capture.RemoveAt(3);

        var failure = Fails(capture, reference);

        Assert.Contains("frame loss", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReorderedPairIsCaughtOnDeliveryOrder()
    {
        var reference = Sequence(5);
        var capture = Sequence(5);
        (capture[1], capture[2]) = (capture[2], capture[1]);

        var failure = Fails(capture, reference);

        Assert.Contains("arrived out of order", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedFrameIsCaught()
    {
        var reference = Sequence(5);
        var capture = Sequence(5);
        capture[3] = capture[2];

        var failure = Fails(capture, reference);

        Assert.Contains("repeats PTS", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrameAtAPtsTheReferenceNeverProducedIsCaught()
    {
        var reference = Sequence(5);
        var capture = Sequence(5);
        capture[4] = capture[4] with { Pts = TimeSpan.FromMilliseconds(999) };

        var failure = Fails(capture, reference);

        Assert.Contains(
            "which the reference decode never produced",
            failure.Message,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// Matching by PTS and counting loss by subtraction both assume the
    /// reference has no repeated timestamp. If one slipped through, a duplicate
    /// would vanish from the lookup while still counting toward the reference
    /// total, and the comparison would quietly certify or reject the wrong
    /// thing.
    /// </summary>
    [Fact]
    public void ADuplicateReferencePtsIsRejectedRatherThanOverwritten()
    {
        var reference = Sequence(5);
        reference[3] = reference[3] with { Pts = reference[2].Pts };
        var capture = Sequence(5);

        var failure = Fails(capture, reference);

        Assert.Contains("more than once", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void ABudgetOutsideZeroToOneIsRejected(double budget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaybackInvariants.VideoFramePixelsMatchReference(
                Sequence(2),
                Sequence(2),
                maxFrameLossRatio: budget
            )
        );
    }

    private static Xunit.Sdk.XunitException Fails(
        List<VideoCapture> capture,
        List<VideoCapture> reference,
        double maxFrameLossRatio = 0.0
    ) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            PlaybackInvariants.VideoFramePixelsMatchReference(
                capture,
                reference,
                maxFrameLossRatio
            )
        );

    /// <summary>
    /// Half the frames, evenly spaced — the shape a shed episode leaves
    /// behind, without needing a decoder to produce it.
    /// </summary>
    private static List<VideoCapture> EveryOtherFrameOf(List<VideoCapture> reference)
    {
        var kept = new List<VideoCapture>();
        for (int i = 0; i < reference.Count; i += 2)
        {
            var r = reference[i];
            kept.Add(r with { Pixels = (byte[])r.Pixels.Clone() });
        }
        return kept;
    }

    /// <summary>
    /// A deterministic frame sequence where each frame's pixels are unique to
    /// its index, so a swap or a drop is detectable by content as well as by
    /// position.
    /// </summary>
    private static List<VideoCapture> Sequence(int frames)
    {
        var list = new List<VideoCapture>(frames);
        for (int i = 0; i < frames; i++)
        {
            var pixels = new byte[BytesPerFrame];
            for (int b = 0; b < BytesPerFrame; b++)
                pixels[b] = (byte)((i * 31) + b);

            list.Add(
                new VideoCapture(
                    Pts: TimeSpan.FromMilliseconds(i * 40),
                    Duration: TimeSpan.FromMilliseconds(40),
                    Width: Width,
                    Height: Height,
                    Format: PixelFormat.Bgra32,
                    Pixels: pixels
                )
            );
        }
        return list;
    }
}
