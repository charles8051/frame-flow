// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO;

namespace FrameFlow.Media;

public sealed record MediaSource : IMediaSource
{
    public required string DisplayName { get; init; }

    /// <inheritdoc />
    public Uri? Uri { get; init; }

    /// <inheritdoc />
    public string? FilePath { get; init; }

    /// <inheritdoc />
    public bool IsSeekable { get; init; } = true;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string>? DemuxerOptions { get; init; }

    /// <inheritdoc />
    public string? InputFormat { get; init; }

    public static MediaSource FromFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new MediaSource
        {
            DisplayName = Path.GetFileName(fullPath),
            Uri = new Uri(fullPath),
            FilePath = fullPath,
            IsSeekable = true,
        };
    }

    public static MediaSource FromUri(Uri uri)
    {
        return new MediaSource
        {
            DisplayName = uri.ToString(),
            Uri = uri,
            FilePath = uri.IsFile ? uri.LocalPath : null,
            IsSeekable = uri.IsFile,
        };
    }
}
