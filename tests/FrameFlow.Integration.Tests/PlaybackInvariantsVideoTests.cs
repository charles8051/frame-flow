using FrameFlow.Integration.Tests.Harness.Capture;
using FrameFlow.Media;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Proves <see cref="PlaybackInvariants.VideoFramePixelsMatchReference"/>
/// actually fails on the corruption it claims to detect.
/// </summary>
/// <remarks>
/// <para>
/// The invariant went from written to called without ever being observed
/// failing, because the playback runtime is correct today and the corpus
/// clips are small enough not to stress it. A gate nobody has seen go red
/// is indistinguishable from a gate that cannot go red.
/// </para>
/// <para>
/// These are pure data assertions: no FFmpeg, no corpus, no playback. They
/// state the three ways a capture can diverge from a reference decode and
/// assert each one is caught.
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
        var reference = Sequence(frames: 5);
        var capture = Sequence(frames: 5);

        PlaybackInvariants.VideoFramePixelsMatchReference(capture, reference);
    }

    [Fact]
    public void ASingleAlteredPixelByteIsCaught()
    {
        var reference = Sequence(frames: 5);
        var capture = Sequence(frames: 5);

        // One byte, in the middle frame, off by one. This is the smallest
        // possible version of what #134 produced at macroblock scale.
        capture[2].Pixels[7] ^= 0x01;

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => PlaybackInvariants.VideoFramePixelsMatchReference(capture, reference)
        );

        Assert.Contains("pixel bytes diverge", failure.Message, StringComparison.Ordinal);
        Assert.Contains("byte 7", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADroppedFrameIsCaughtOnTheCount()
    {
        var reference = Sequence(frames: 5);
        var capture = Sequence(frames: 5);
        capture.RemoveAt(3);

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => PlaybackInvariants.VideoFramePixelsMatchReference(capture, reference)
        );

        Assert.Contains("frame count mismatch", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReorderedFrameIsCaughtOnThePts()
    {
        var reference = Sequence(frames: 5);
        var capture = Sequence(frames: 5);
        (capture[1], capture[2]) = (capture[2], capture[1]);

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => PlaybackInvariants.VideoFramePixelsMatchReference(capture, reference)
        );

        Assert.Contains("PTS mismatch", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deterministic frame sequence where each frame's pixels are unique
    /// to its index, so a swap or a drop is detectable by content as well as
    /// by position.
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
