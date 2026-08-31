// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Encoding.Internal;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Encoding;

/// <summary>
/// Factory entry points for container muxers (ADR-0040).
/// </summary>
public static class Muxer
{
    /// <summary>
    /// Creates an MP4 (MPEG-4 Part 14) muxer that writes to
    /// <paramref name="path"/>. The file is created/truncated when the muxer
    /// starts and is not a valid, seekable MP4 until
    /// <see cref="IMuxer.CompleteAsync"/> writes the trailer.
    /// </summary>
    /// <param name="path">Output file path.</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostics (ADR-0010).</param>
    public static IMuxer Mp4(string path, ILoggerFactory? loggerFactory = null) =>
        new Mp4Muxer(path, loggerFactory);
}
