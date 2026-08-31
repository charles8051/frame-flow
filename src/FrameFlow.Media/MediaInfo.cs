// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

public sealed record MediaInfo(
    string ContainerName,
    TimeSpan Duration,
    IReadOnlyList<VideoStreamInfo> VideoStreams,
    IReadOnlyList<AudioStreamInfo> AudioStreams
);

public sealed record VideoStreamInfo(
    int StreamIndex,
    string CodecName,
    int Width,
    int Height,
    double FrameRate
);

public sealed record AudioStreamInfo(
    int StreamIndex,
    string CodecName,
    int SampleRate,
    int Channels
);
