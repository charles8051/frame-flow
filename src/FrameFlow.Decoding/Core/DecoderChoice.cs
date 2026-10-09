// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Decoding.Core;

/// <summary>
/// A decoder paired with a hardware backend that can run it, from one entry in the decoder's
/// <c>AVCodecHWConfig</c> table.
/// </summary>
/// <param name="Decoder">The decoder's name, as FFmpeg registers it.</param>
/// <param name="Kind">The backend.</param>
/// <param name="AvHwDeviceType">The backend's <c>AVHWDeviceType</c>.</param>
/// <param name="HwPixelFormat">The pixel format the decoder produces on the backend.</param>
internal sealed record HwAccelCandidate(
    string Decoder,
    HardwareDecodeBackendKind Kind,
    int AvHwDeviceType,
    int HwPixelFormat);

/// <summary>The operating system family whose default backend order applies.</summary>
internal enum OsFamily
{
    Other,
    Windows,
    MacOs,
    Linux,
}

/// <summary>What <see cref="DecoderChoice.Decide"/> chose, and why, for a log line.</summary>
/// <param name="Hardware">The hardware candidates to try, in order. Empty when none applies.</param>
/// <param name="SoftwareDecoder">The software decoder, which is what <c>Auto</c> falls back to.</param>
/// <param name="Refused">The known refusals that dropped a candidate.</param>
/// <param name="Reason">Why, in a line.</param>
/// <param name="Excluded">
/// True when the codec is in <c>ExcludedCodecs</c>, which is why <paramref name="Hardware"/> is empty.
/// </param>
internal readonly record struct DecoderChoiceDecision(
    IReadOnlyList<HwAccelCandidate> Hardware,
    string SoftwareDecoder,
    IReadOnlyList<KnownRefusal> Refused,
    string Reason,
    bool Excluded = false);

/// <summary>
/// Decides which hardware candidates a video decoder tries, and in what order (ADR-0033,
/// ADR-0083). Pure: the
/// decoder's hardware configs, the mode and the backend orders in, a decision out.
/// </summary>
/// <remarks>
/// A codec in <c>ExcludedCodecs</c> has no hardware candidate, whatever else is asked. A borrowed
/// device fixes the backend, and it opened already, so the probe's view of the host
/// and the order do not apply and nothing is refused. Otherwise only backends whose device
/// initialised are candidates, ordered by the caller's preference, then the platform default, then
/// the rest in the order the decoder lists them. Under <see cref="HardwareDecodeMode.Auto"/> a
/// backend in <see cref="KnownRefusals"/> for the codec is dropped.
/// </remarks>
internal static class DecoderChoice
{
    /// <summary>
    /// The backends tried after the caller's preference, first to last, on <paramref name="os"/>.
    /// A backend not listed is tried last.
    /// </summary>
    public static IReadOnlyList<HardwareDecodeBackendKind> PlatformDefault(OsFamily os) =>
        os switch
        {
            OsFamily.Windows =>
            [
                HardwareDecodeBackendKind.D3D11Va,
                HardwareDecodeBackendKind.D3D12Va,
                HardwareDecodeBackendKind.Dxva2,
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.Qsv,
            ],
            OsFamily.MacOs => [HardwareDecodeBackendKind.VideoToolbox],
            OsFamily.Linux =>
            [
                HardwareDecodeBackendKind.VaApi,
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.Vdpau,
                HardwareDecodeBackendKind.Qsv,
                HardwareDecodeBackendKind.Vulkan,
                HardwareDecodeBackendKind.Drm,
            ],
            _ => [],
        };

    /// <param name="codecId">The FFmpeg <c>AVCodecID</c>, which keys <paramref name="refusals"/>.</param>
    /// <param name="codecName">The codec's name, which <paramref name="excludedCodecs"/> names.</param>
    /// <param name="softwareDecoder">The software decoder's name.</param>
    /// <param name="configs">
    /// Every device-context hardware config of the decoder, whether or not its device initialised.
    /// </param>
    /// <param name="mode">The mode. <see cref="HardwareDecodeMode.Disabled"/> has no candidate.</param>
    /// <param name="preferred">The caller's preferred backends, first to last.</param>
    /// <param name="platformDefault">The platform's default order, from <see cref="PlatformDefault"/>.</param>
    /// <param name="initialised">The backends whose device initialised on this host.</param>
    /// <param name="borrowedDeviceType">
    /// The <c>AVHWDeviceType</c> of a borrowed device, or <see langword="null"/> for none.
    /// </param>
    /// <param name="refusals">The backends not to bind for a codec under <c>Auto</c>.</param>
    /// <param name="excludedCodecs">Codec names that decode in software, compared without regard to case.</param>
    public static DecoderChoiceDecision Decide(
        int codecId,
        string codecName,
        string softwareDecoder,
        IReadOnlyList<HwAccelCandidate> configs,
        HardwareDecodeMode mode,
        IReadOnlyList<HardwareDecodeBackendKind> preferred,
        IReadOnlyList<HardwareDecodeBackendKind> platformDefault,
        IReadOnlyCollection<HardwareDecodeBackendKind> initialised,
        int? borrowedDeviceType,
        IReadOnlyList<KnownRefusal> refusals,
        IReadOnlyList<string> excludedCodecs)
    {
        ArgumentNullException.ThrowIfNull(codecName);
        ArgumentNullException.ThrowIfNull(softwareDecoder);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(platformDefault);
        ArgumentNullException.ThrowIfNull(initialised);
        ArgumentNullException.ThrowIfNull(refusals);
        ArgumentNullException.ThrowIfNull(excludedCodecs);

        if (mode == HardwareDecodeMode.Disabled)
            return new(Hardware: [], softwareDecoder, Refused: [], "hardware decode is disabled");

        if (excludedCodecs.Contains(codecName, StringComparer.OrdinalIgnoreCase))
        {
            return new(
                Hardware: [],
                softwareDecoder,
                Refused: [],
                $"'{codecName}' is in ExcludedCodecs",
                Excluded: true);
        }

        if (borrowedDeviceType is { } deviceType)
        {
            var onDevice = configs.Where(c => c.AvHwDeviceType == deviceType).ToList();
            return new(
                onDevice,
                softwareDecoder,
                Refused: [],
                onDevice.Count == 0
                    ? "the decoder has no hardware configuration for the borrowed device"
                    : "the borrowed device fixes the backend");
        }

        var usable = configs.Where(c => initialised.Contains(c.Kind)).ToList();

        var refused = new List<KnownRefusal>();
        if (KnownRefusals.Governs(mode, borrowedDevice: false))
        {
            usable.RemoveAll(candidate =>
            {
                if (KnownRefusals.Find(refusals, candidate.Kind, codecId) is not { } refusal)
                    return false;

                refused.Add(refusal);
                return true;
            });
        }

        // OrderBy is stable, so backends of one rank keep the order the decoder lists them in.
        var ordered = usable.OrderBy(c => Rank(c.Kind, preferred, platformDefault)).ToList();

        return new(
            ordered,
            softwareDecoder,
            refused,
            Describe(ordered, softwareDecoder, preferred, refused));
    }

    // Preferred backends rank below 1000 in the order given, then the platform default from 1000,
    // and everything else (including Other) last.
    private static int Rank(
        HardwareDecodeBackendKind kind,
        IReadOnlyList<HardwareDecodeBackendKind> preferred,
        IReadOnlyList<HardwareDecodeBackendKind> platformDefault)
    {
        for (int i = 0; i < preferred.Count; i++)
        {
            if (preferred[i] == kind)
                return i;
        }

        for (int i = 0; i < platformDefault.Count; i++)
        {
            if (platformDefault[i] == kind)
                return 1000 + i;
        }

        return 10_000;
    }

    private static string Describe(
        IReadOnlyList<HwAccelCandidate> ordered,
        string softwareDecoder,
        IReadOnlyList<HardwareDecodeBackendKind> preferred,
        IReadOnlyList<KnownRefusal> refused)
    {
        string order =
            ordered.Count == 0
                ? "no hardware backend with a device and a config"
                : string.Join(
                    ", ",
                    ordered.Select(c =>
                        c.Decoder == softwareDecoder ? $"{c.Kind}" : $"{c.Kind} ({c.Decoder})"))
                    + (preferred.Count > 0 ? ", preference first" : ", platform default");

        return refused.Count == 0
            ? order
            : $"{order}; skipped {string.Join(", ", refused.Select(r => r.Backend))} as known to "
                + "decode the codec wrongly";
    }
}
