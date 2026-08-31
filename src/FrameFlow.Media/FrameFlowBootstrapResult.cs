// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Identifies how FFmpeg native binaries were located during bootstrap.
/// </summary>
public enum FfmpegBinarySource
{
    /// <summary>
    /// The binary source could not be determined. Bootstrap may have failed or was not configured.
    /// </summary>
    Unknown,

    /// <summary>
    /// FFmpeg binaries bundled with the application package were used.
    /// </summary>
    Bundled,

    /// <summary>
    /// A caller-supplied path was used to locate FFmpeg binaries.
    /// </summary>
    CustomPath,

    /// <summary>
    /// System-installed FFmpeg libraries were resolved via the platform's standard library search.
    /// </summary>
    System,
}

/// <summary>
/// Describes the outcome of a <see cref="IFrameFlowBootstrapper.Initialize"/> call.
/// </summary>
/// <param name="IsSuccess">
/// <see langword="true"/> if native initialization succeeded and FFmpeg bindings are ready.
/// </param>
/// <param name="ResolvedPath">
/// <para>The path at which FFmpeg binaries were found, or <see langword="null"/> when no explicit
/// path applies:</para>
/// <list type="bullet">
///   <item>
///     <term><see cref="FfmpegBinarySource.CustomPath"/></term>
///     <description>The caller-supplied path provided via <c>FrameFlowNativeOptions.CustomFfmpegPath</c>.</description>
///   </item>
///   <item>
///     <term><see cref="FfmpegBinarySource.Bundled"/></term>
///     <description>The resolved path to the bundled binary directory within the application layout.</description>
///   </item>
///   <item>
///     <term><see cref="FfmpegBinarySource.System"/></term>
///     <description><see langword="null"/> — system libraries are resolved by the OS loader; no explicit path is tracked.</description>
///   </item>
///   <item>
///     <term><see cref="FfmpegBinarySource.Unknown"/></term>
///     <description><see langword="null"/> — binary source could not be determined.</description>
///   </item>
/// </list>
/// </param>
/// <param name="BinarySource">Identifies which resolution strategy located the binaries.</param>
/// <param name="Message">A human-readable description of the bootstrap outcome, suitable for logging.</param>
/// <param name="Capabilities">
/// Hardware-decode backends discovered during bootstrap (ADR-0033). When the
/// load fails before the probe runs, or when probing is explicitly disabled,
/// this is <see cref="HardwareDecodeCapabilities.Empty"/>.
/// </param>
public sealed record FrameFlowBootstrapResult(
    bool IsSuccess,
    string? ResolvedPath,
    FfmpegBinarySource BinarySource,
    string Message,
    HardwareDecodeCapabilities Capabilities
)
{
    /// <summary>
    /// Backwards-compatible constructor for callers (chiefly tests) that don't
    /// supply capabilities. Defaults to <see cref="HardwareDecodeCapabilities.Empty"/>.
    /// </summary>
    public FrameFlowBootstrapResult(
        bool IsSuccess,
        string? ResolvedPath,
        FfmpegBinarySource BinarySource,
        string Message
    )
        : this(IsSuccess, ResolvedPath, BinarySource, Message, HardwareDecodeCapabilities.Empty) { }
}
