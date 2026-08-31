// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Triggers for the primary playback state machine region.
/// Each trigger drives exactly one transition in the Stateless configuration.
/// </summary>
internal enum PlaybackTrigger
{
    /// <summary>Begin loading a media source.</summary>
    Load,

    /// <summary>Container headers have been read; stream info is available.</summary>
    HeadersReceived,

    /// <summary>Full metadata parsing and decoder creation completed.</summary>
    MetadataParsed,

    /// <summary>Initial buffer fill reached the ready threshold.</summary>
    BufferReady,

    /// <summary>Resume or start forward playback.</summary>
    Play,

    /// <summary>Pause playback at the current position.</summary>
    Pause,

    /// <summary>Initiate a seek to a new position.</summary>
    Seek,

    /// <summary>Unload the media source and tear down the pipeline.</summary>
    Unload,

    /// <summary>Reset from Unloaded/Error/Ended back to Idle.</summary>
    Reset,

    /// <summary>Release all resources (terminal).</summary>
    Release,

    /// <summary>Buffer level dropped below the playback threshold.</summary>
    BufferUnderrun,

    /// <summary>The final frame of the media has been rendered.</summary>
    LastFrameRendered,

    /// <summary>An unrecoverable error occurred in the pipeline.</summary>
    FatalError,
}
