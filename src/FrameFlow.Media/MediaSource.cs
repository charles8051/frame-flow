// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO;

namespace FrameFlow.Media;

public sealed record MediaSource(
    string DisplayName,
    Uri? Uri = null,
    string? FilePath = null,
    bool IsSeekable = true
) : IMediaSource
{
    public static MediaSource FromFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new MediaSource(
            DisplayName: Path.GetFileName(fullPath),
            Uri: new Uri(fullPath),
            FilePath: fullPath,
            IsSeekable: true
        );
    }

    public static MediaSource FromUri(Uri uri)
    {
        return new MediaSource(
            DisplayName: uri.ToString(),
            Uri: uri,
            FilePath: uri.IsFile ? uri.LocalPath : null,
            IsSeekable: uri.IsFile
        );
    }
}
