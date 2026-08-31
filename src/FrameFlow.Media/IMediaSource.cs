// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

public interface IMediaSource
{
    string DisplayName { get; }

    /// <summary>
    /// The URI identifying the media resource, or <see langword="null"/> for sources
    /// that are not URI-addressable (e.g. in-memory streams).
    /// </summary>
    Uri? Uri { get; }

    /// <summary>
    /// The local file path when the source is a file, or <see langword="null"/> for non-file sources.
    /// </summary>
    string? FilePath { get; }

    bool IsSeekable { get; }
}
