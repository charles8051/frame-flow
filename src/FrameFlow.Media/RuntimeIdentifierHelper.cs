// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Media;

/// <summary>
/// Provides the current .NET runtime identifier (RID) string.
/// </summary>
/// <remarks>
/// <para>
/// The RID locates platform-specific native binaries in the
/// <c>runtimes/{rid}/native/</c> layout defined by ADR-0014.
/// </para>
/// <para>
/// Lives here, beside <see cref="BundleExtractionHelper"/>, because both native bootstrappers
/// probe that layout: <c>FrameFlowBootstrapper</c> for FFmpeg and <c>SdlBootstrapper</c> for
/// SDL2. ADR-0019 keeps <c>FrameFlow.Sdl</c> from referencing <c>FrameFlow.Native</c>, so a
/// helper both need belongs in the assembly both already reference. It sat in
/// <c>FrameFlow.Native</c> until then, and <c>SdlBootstrapper</c> carried a verbatim copy.
/// </para>
/// </remarks>
internal static class RuntimeIdentifierHelper
{
    private static readonly Lazy<string> CachedRid = new(DetectRid);

    /// <summary>Gets the RID for the current operating system and CPU architecture.</summary>
    public static string Current => CachedRid.Value;

    private static string DetectRid()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "unknown",
        };

        if (OperatingSystem.IsWindows())
            return $"win-{arch}";
        if (OperatingSystem.IsMacOS())
            return $"osx-{arch}";
        if (OperatingSystem.IsLinux())
            return $"linux-{arch}";

        return $"unknown-{arch}";
    }
}
