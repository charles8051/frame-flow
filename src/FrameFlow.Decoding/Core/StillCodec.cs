// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Decoding.Core;

/// <summary>
/// The decoder for a bare image whose format FFmpeg's <c>image2</c> demuxer cannot name from the
/// file's extension (#575). Pure.
/// </summary>
/// <remarks>
/// <c>image2</c> reads one image per file and picks its decoder from the extension. The FFmpeg 9.0
/// build leaves GIF out of that table, so a GIF opened on <c>image2</c> has a stream with no codec
/// and the open fails. Naming the decoder opens it. This reads the file's own first bytes rather
/// than its extension, because <see cref="FrameFlow.Media.MediaSource.FromStill"/> promises to
/// look at neither the extension nor the name, and a content-addressed store has none.
/// </remarks>
internal static class StillCodec
{
    /// <summary>The bytes <see cref="ForHeader"/> needs.</summary>
    public const int HeaderLength = 6;

    /// <summary>
    /// The <c>AVCodecID</c> for a file starting with <paramref name="header"/>, or
    /// <see langword="null"/> when this does not know the format.
    /// </summary>
    public static int? ForHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength)
            return null;

        // "GIF87a" and "GIF89a".
        if (
            header[..3].SequenceEqual("GIF"u8)
            && (header[3..6].SequenceEqual("87a"u8) || header[3..6].SequenceEqual("89a"u8))
        )
        {
            return (int)AVCodecID.AV_CODEC_ID_GIF;
        }

        return null;
    }
}
