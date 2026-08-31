// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Native.Interop;

/// <summary>
/// FFmpeg flag and constant values that are not struct offsets.
/// These are integer flag definitions from the FFmpeg public API.
/// </summary>
internal static class FFmpegConstants
{
    /// <summary>
    /// <c>AV_PKT_FLAG_KEY</c> — set in <c>AVPacket.flags</c> when the packet is a key frame.
    /// Defined in <c>libavcodec/packet.h</c>.
    /// </summary>
    internal const int PktFlagKey = 0x0001;
}
