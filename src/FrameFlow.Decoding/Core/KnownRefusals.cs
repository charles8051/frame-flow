// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Decoding.Core;

/// <summary>
/// A hardware backend that gives wrong output, or none, for a codec, with the issue that says so.
/// </summary>
/// <param name="Backend">The backend not to bind.</param>
/// <param name="CodecId">The FFmpeg <c>AVCodecID</c> it is wrong for.</param>
/// <param name="Reason">Why, for a log line. Cites the issue.</param>
internal readonly record struct KnownRefusal(
    HardwareDecodeBackendKind Backend,
    int CodecId,
    string Reason);

/// <summary>
/// The backends <see cref="HardwareDecodeMode.Auto"/> does not bind for a codec because they are
/// known to decode it wrongly (#574). Pure.
/// </summary>
/// <remarks>
/// A row records a backend that gives wrong output for a codec, and never a judgement about cost
/// (ADR-0033 rejected an automatic "too cheap to accelerate" rule). It leaves with the issue.
/// <c>PreferredBackends</c> does not override a row: it is a ranking, and a player fills it from the
/// video sink's preference as well as the caller's. <c>Required</c> and a borrowed device name a
/// backend, so they keep the candidate.
/// </remarks>
internal static class KnownRefusals
{
    /// <summary><c>AV_CODEC_ID_MJPEG</c>.</summary>
    public const int MjpegCodecId = 7;

    /// <summary>The rows this build ships with.</summary>
    public static IReadOnlyList<KnownRefusal> Builtin { get; } =
    [
        new(
            HardwareDecodeBackendKind.Cuda,
            MjpegCodecId,
            "its MJPEG decoder expands full-range samples as if they were limited range, so shadows "
                + "and highlights clip (#574)"),
    ];

    /// <summary>
    /// Whether the refusals govern a decode: under <see cref="HardwareDecodeMode.Auto"/> with no
    /// borrowed device.
    /// </summary>
    public static bool Governs(HardwareDecodeMode mode, bool borrowedDevice) =>
        mode == HardwareDecodeMode.Auto && !borrowedDevice;

    /// <summary>The row for <paramref name="backend"/> and <paramref name="codecId"/>, or null.</summary>
    public static KnownRefusal? Find(
        IReadOnlyList<KnownRefusal> refusals,
        HardwareDecodeBackendKind backend,
        int codecId)
    {
        ArgumentNullException.ThrowIfNull(refusals);

        foreach (var refusal in refusals)
        {
            if (refusal.Backend == backend && refusal.CodecId == codecId)
                return refusal;
        }

        return null;
    }
}
