// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Decoding;

public interface IDemuxSessionFactory
{
    ValueTask<IDemuxSession> OpenAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default
    );
}
