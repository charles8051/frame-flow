using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Proves <see cref="PlaybackInvariants.VideoFramePixelsMatchReference"/>
/// fails on the corruption it claims to detect, and keeps that detection
/// separate from the frames a real-time pipeline drops late on purpose.
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
/// each is caught, including the case that matters most — corruption among
/// the frames presented while others were dropped late — and the ways the
/// frame account can fail to add up (#299).
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
        PlaybackInvariants.VideoFramePixelsMatchReference(Sequence(5), Sequence(5), droppedLate: 0);
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
    /// The case the count check used to hide. A run that dropped half its
    /// frames late still has to present correct pictures for the half it kept.
    /// </summary>
    [Fact]
    public void CorruptionIsCaughtWhileFramesWereDroppedLate()
    {
        var reference = Sequence(10);
        var capture = EveryOtherFrameEndingOnTheLast(reference);
        capture[3].Pixels[2] ^= 0xFF;

        var failure = Fails(capture, reference, droppedLate: 5);

        Assert.Contains("pixel bytes diverge", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FramesTheRuntimeCountedAsDroppedLateAreNotAFailure()
    {
        var reference = Sequence(10);
        var capture = EveryOtherFrameEndingOnTheLast(reference);

        PlaybackInvariants.VideoFramePixelsMatchReference(capture, reference, droppedLate: 5);
    }

    /// <summary>
    /// A frame lost on the way to the sink, among frames the pacer did drop
    /// late. The late drops do not excuse it, whatever their number.
    /// </summary>
    [Fact]
    public void AFrameMissingBeyondTheLateDropsIsAFailure()
    {
        var reference = Sequence(10);
        var capture = EveryOtherFrameEndingOnTheLast(reference);

        var failure = Fails(capture, reference, droppedLate: 4);

        Assert.Contains("went missing with no count", failure.Message, StringComparison.Ordinal);
        Assert.Contains("1 of 10", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrameMissingWithNoLateDropIsCaught()
    {
        var reference = Sequence(5);
        var capture = Sequence(5);
        capture.RemoveAt(3);

        var failure = Fails(capture, reference);

        Assert.Contains("went missing with no count", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction: a count with no missing frame behind it. Either a
    /// counted frame also reached the sink, or the counter is wrong, and an
    /// account that only checked for uncounted loss would pass both.
    /// </summary>
    [Fact]
    public void MoreLateDropsThanMissingFramesIsAFailure()
    {
        var reference = Sequence(10);
        var capture = EveryOtherFrameEndingOnTheLast(reference);

        var failure = Fails(capture, reference, droppedLate: 6);

        Assert.Contains("count and the capture disagree", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A late drop discards a frame because a newer one was due, so the newest
    /// frame of all cannot be one. The account adds up here; the claim that the
    /// missing frame was dropped late cannot be true.
    /// </summary>
    [Fact]
    public void TheNewestFrameCountedAsDroppedLateIsAFailure()
    {
        var reference = Sequence(5);
        var capture = Sequence(5);
        capture.RemoveAt(4);

        var failure = Fails(capture, reference, droppedLate: 1);

        Assert.Contains("last reference frame", failure.Message, StringComparison.Ordinal);
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

    [Fact]
    public void ANegativeLateDropCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaybackInvariants.VideoFramePixelsMatchReference(
                Sequence(2),
                Sequence(2),
                droppedLate: -1
            )
        );
    }

    private static Xunit.Sdk.XunitException Fails(
        List<VideoCapture> capture,
        List<VideoCapture> reference,
        long droppedLate = 0
    ) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            PlaybackInvariants.VideoFramePixelsMatchReference(capture, reference, droppedLate)
        );

    /// <summary>
    /// Every other frame, ending on the last: what a pacer that keeps finding
    /// two frames due leaves behind. The newest frame is always among those
    /// presented, because a late drop only discards a frame a newer one
    /// superseded.
    /// </summary>
    private static List<VideoCapture> EveryOtherFrameEndingOnTheLast(List<VideoCapture> reference)
    {
        var kept = new List<VideoCapture>();
        for (int i = reference.Count % 2 == 0 ? 1 : 0; i < reference.Count; i += 2)
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
