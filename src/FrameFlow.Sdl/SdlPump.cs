// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Silk.NET.SDL;

namespace FrameFlow.Sdl;

/// <summary>
/// The SDL calls <see cref="SdlEventLoop"/> makes, so the loop runs against a fake in tests that
/// have no display.
/// </summary>
internal interface ISdlPump
{
    /// <summary>Renders the sink's pending frame, if it has one.</summary>
    void RenderPendingFrame();

    /// <summary>Polls the next SDL event into <paramref name="sdlEvent"/>; false when there is none.</summary>
    bool PollEvent(ref Event sdlEvent);

    /// <summary>Releases what an event the loop drops unhandled owns: a drop's path or text.</summary>
    void Discard(ref Event sdlEvent);
}

/// <summary>The <see cref="ISdlPump"/> over a real SDL instance and its video sink.</summary>
internal sealed unsafe class SdlPump(SdlApi sdl, SdlVideoSink videoSink) : ISdlPump
{
    public void RenderPendingFrame() => videoSink.RenderPendingFrame();

    public bool PollEvent(ref Event sdlEvent) => sdl.PollEvent(ref sdlEvent) != 0;

    public void Discard(ref Event sdlEvent)
    {
        // SDL hands the application the string a drop carries, and the application frees it.
        if ((EventType)sdlEvent.Type is EventType.Dropfile or EventType.Droptext)
            sdl.Free(sdlEvent.Drop.File);
    }
}
