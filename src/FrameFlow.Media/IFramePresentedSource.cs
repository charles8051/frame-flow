// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// The frame a sink has just put on screen: its presentation timestamp on the media
/// timeline, and the wall-clock instant it was presented.
/// </summary>
/// <param name="PresentationTime">
/// The frame's <see cref="IVideoFrame.Pts"/>. This is the value an overlay keys off: it
/// names the picture the viewer is looking at.
/// </param>
/// <param name="PresentedAtUtc">
/// When the present happened, for measuring the gap between a frame reaching the screen and
/// whatever the consumer does about it.
/// </param>
public readonly record struct FramePresentedInfo(TimeSpan PresentationTime, DateTime PresentedAtUtc);

/// <summary>
/// A sink that reports which frame it has presented, so a consumer can act on the picture
/// that is on screen rather than on the frame the graph is currently processing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not the same as the graph's position.</b> Work attached to the pipeline
/// runs where the graph runs, which is ahead of the display by however much the pacer
/// buffers. An overlay that posts its result there draws over a picture that has not been
/// shown yet. Keying that post off this event puts it back on the frame it describes.
/// </para>
/// <para>
/// <b>Optional.</b> Sinks implement this only when they know the moment a frame reaches the
/// screen. <c>AvaloniaVideoSink</c> raises it at the buffer swap, and the composition-interop
/// sink once the compositor hand-off has completed; a headless or recording sink need not
/// implement it at all. Consumers test for it: <c>if (sink is IFramePresentedSource presented)</c>.
/// </para>
/// <para>
/// <b>Threading. Do not assume the UI thread.</b> Each sink raises this synchronously on
/// whichever thread finished the present, and that thread differs by path. The Avalonia view's
/// swap runs on the UI thread, but a host driving <c>AvaloniaVideoSink.RenderPendingFrame</c>
/// itself gets the event on its own thread, and the composition-interop sink raises from the
/// continuation of the compositor hand-off, which is a thread-pool thread. A handler that
/// touches UI state marshals for itself.
/// </para>
/// <para>
/// <b>It runs in the present path, deliberately.</b> A handler that blocks holds up the next
/// frame. The event exists to put a consumer as close to the present as the presenter itself
/// is, so queueing delivery would reintroduce exactly the lag it removes. Handlers do the
/// least possible work: read the PTS, hand it on, return. One handler throwing does not
/// prevent the others from running, and never fails the present.
/// </para>
/// </remarks>
public interface IFramePresentedSource
{
    /// <summary>
    /// Raised once per frame that reaches the screen, in presentation order. Not raised for
    /// a frame that was superseded or dropped before it drew.
    /// </summary>
    event EventHandler<FramePresentedInfo>? FramePresented;
}
