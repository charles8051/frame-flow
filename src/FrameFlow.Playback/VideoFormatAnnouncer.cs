// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Tells a video sink what shape the frames it is about to receive are, and does it once per
/// distinct shape rather than once per item.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IVideoSink.OnFormatChangedAsync"/> is documented as the call the playback
/// pipeline makes when the stream format changes, and until this type nothing made it. A sink
/// that sizes its surface from the announcement therefore kept whatever geometry it had
/// guessed, and an item of a different size from the one before it showed the previous item's
/// picture — for a clip only until its next frame arrived, and for an item with one frame
/// for as long as that item lasted (#287).
/// </para>
/// <para>
/// <b>Why the frame and not the metadata.</b> The announcement describes what the sink will be
/// handed, and that is not what the stream says: the decode path converts to
/// <see cref="PixelFormat.Bgra32"/> on the CPU and yields NV12 when hardware frames are passed
/// through. Reading it off the frame about to be presented cannot disagree with the frame, and
/// covers a mid-stream change without a separate mechanism.
/// </para>
/// <para>
/// <b>Why it outlives an item.</b> One instance is shared by every item played through the
/// same sink, because the question it answers — has this sink been told this shape — is about
/// the sink, which is warm across the queue, and not about the item. A per-item instance would
/// re-announce an unchanged format at every boundary and make a sink that rebuilds a surface
/// on announcement rebuild it for each item in a queue of identical clips.
/// </para>
/// </remarks>
internal sealed class VideoFormatAnnouncer
{
    // Serializes announcement-with-presentation for one sink. A sink is a single consumer,
    // and during a queue boundary two sessions are live at once — the next item is created
    // and warmed while the current one still plays — so two pacers can reach the same sink.
    // Without this, pacer B could announce its format and present before pacer A presented
    // the frame A had already announced, and the sink would be handed a frame that does not
    // match the last thing it was told.
    private readonly SemaphoreSlim _serial = new(1, 1);

    // Read and written only under _serial, except by Announced, which is for tests.
    private VideoFormatInfo? _announced;

    /// <summary>
    /// The format most recently announced and accepted, or <see langword="null"/> before the
    /// first one.
    /// </summary>
    internal VideoFormatInfo? Announced => Volatile.Read(ref _announced);

    /// <summary>
    /// Whether <paramref name="next"/> has to be announced given what was announced last.
    /// </summary>
    /// <remarks>
    /// <see cref="VideoFormatInfo"/> is a record, so this is structural: the same width,
    /// height and pixel format is the same announcement whatever item it came from.
    /// </remarks>
    internal static bool ShouldAnnounce(VideoFormatInfo? announced, VideoFormatInfo next) =>
        announced != next;

    /// <summary>
    /// Describes the frame in the terms <see cref="IVideoSink.OnFormatChangedAsync"/> takes.
    /// </summary>
    internal static VideoFormatInfo FormatOf(IVideoFrame frame) =>
        new(frame.Width, frame.Height, frame.Format);

    /// <summary>
    /// Announces <paramref name="frame"/>'s format to <paramref name="sink"/> when it differs
    /// from the last announced one, then presents the frame. The pair is one critical section,
    /// so the sink cannot be handed a frame whose shape is not the last shape it was told.
    /// </summary>
    /// <remarks>
    /// The new format becomes the baseline only after the sink's own call returns. A callback
    /// that throws or is cancelled leaves the previous baseline in place, so the next frame of
    /// that format announces again rather than being presented to a sink that was never
    /// successfully told — which is what recording the baseline first would have done.
    /// </remarks>
    internal async ValueTask PresentAsync(
        IVideoSink sink,
        IVideoFrame frame,
        CancellationToken cancellationToken
    )
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = FormatOf(frame);
            if (ShouldAnnounce(Volatile.Read(ref _announced), next))
            {
                await sink.OnFormatChangedAsync(next, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _announced, next);
            }

            await sink.PresentAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// Forgets what was announced, so the next frame announces whatever it is. For a sink that
    /// has been torn down and rebuilt behind this announcer.
    /// </summary>
    internal void Reset() => Volatile.Write(ref _announced, null);
}
