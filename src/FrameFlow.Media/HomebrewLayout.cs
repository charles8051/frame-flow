// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Media;

/// <summary>
/// Well-known Homebrew install paths on macOS, for the native bootstrappers that probe them.
/// </summary>
/// <remarks>
/// <para>
/// Both bootstrappers look for a keg-only formula in the same place:
/// <c>FrameFlowBootstrapper</c> for <c>ffmpeg@7</c> and <c>SdlBootstrapper</c> for
/// <c>sdl2</c>. Each derived the prefix and assembled the keg path itself, in two different
/// expressions of the same rule. Sharing it here follows <see cref="BundleExtractionHelper"/>
/// and <see cref="RuntimeIdentifierHelper"/>, which are in this assembly for the same reason:
/// ADR-0019 keeps <c>FrameFlow.Sdl</c> from referencing <c>FrameFlow.Native</c>.
/// </para>
/// <para>
/// <b>macOS only.</b> These paths mean nothing on Windows or Linux. Both callers already
/// guard with <see cref="OperatingSystem.IsMacOS"/>; this type does not re-check, so it
/// answers for the current CPU architecture whatever OS it is asked on.
/// </para>
/// </remarks>
internal static class HomebrewLayout
{
    /// <summary>
    /// The Homebrew install prefix: <c>/opt/homebrew</c> on Apple Silicon,
    /// <c>/usr/local</c> on Intel.
    /// </summary>
    public static string Prefix =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "/opt/homebrew" : "/usr/local";

    /// <summary>
    /// The lib directory of a keg-only formula — <c>{Prefix}/opt/{formula}/lib</c>. Keg-only
    /// formulae are not symlinked into <see cref="LinkedLibDirectory"/>, which is why both
    /// bootstrappers probe here first.
    /// </summary>
    /// <param name="formula">The Homebrew formula name, e.g. <c>ffmpeg@7</c> or <c>sdl2</c>.</param>
    public static string KegLibDirectory(string formula)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formula);
        return Path.Combine(Prefix, "opt", formula, "lib");
    }

    /// <summary>
    /// The main prefix lib directory — <c>{Prefix}/lib</c>. Where a formula lands once
    /// someone has run <c>brew link</c> on it.
    /// </summary>
    public static string LinkedLibDirectory => Path.Combine(Prefix, "lib");
}
