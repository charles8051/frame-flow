// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// Platform-pluggable source of <see cref="CudaBootstrapInstruction"/>
/// values. Each platform port implements one; the bootstrapper picks
/// the right one via
/// <see cref="CudaInstallInstructionProviders.ForCurrentPlatform"/>.
/// </summary>
/// <remarks>
/// Separating the instructions from the bootstrapper itself keeps the
/// per-platform install-command database isolated. Each platform's
/// provider, with its own package-manager-specific phrasing, is its own
/// file, not a modification to the bootstrapper.
/// </remarks>
public interface ICudaInstallInstructionProvider
{
    /// <summary>
    /// True when this provider's instructions apply to the current OS
    /// platform. A consumer can construct a specific provider directly
    /// (e.g., for testing) but should usually go through
    /// <see cref="CudaInstallInstructionProviders.ForCurrentPlatform"/>.
    /// </summary>
    bool IsSupportedOnCurrentPlatform { get; }

    /// <summary>
    /// Produces the install instruction for a single missing component.
    /// </summary>
    /// <param name="component">
    /// The component flag. Must be a single flag value, not a
    /// combination.
    /// </param>
    /// <returns>An instruction suitable for display to the consumer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="component"/> is <see cref="MissingNativeComponent.None"/>
    /// or an unrecognized flag.
    /// </exception>
    CudaBootstrapInstruction GetInstruction(MissingNativeComponent component);
}

/// <summary>
/// Factory entry point for selecting an
/// <see cref="ICudaInstallInstructionProvider"/> based on the current
/// runtime platform.
/// </summary>
public static class CudaInstallInstructionProviders
{
    /// <summary>
    /// Returns a provider appropriate for the current OS, or
    /// <see langword="null"/> if no provider is implemented for it yet.
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="WindowsCudaInstallInstructionProvider"/> on
    /// Windows, a <see cref="LinuxCudaInstallInstructionProvider"/> on
    /// Linux, and <see langword="null"/> elsewhere: ONNX Runtime ships no
    /// CUDA build for macOS.
    /// </remarks>
    public static ICudaInstallInstructionProvider? ForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsCudaInstallInstructionProvider();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxCudaInstallInstructionProvider();
        return null;
    }
}
