// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Inference.WinML;

/// <summary>
/// Loads the ONNX Runtime Windows ML ships before anything else can load one. Windows carries an
/// older <c>onnxruntime.dll</c> in System32, and an app built without a runtime identifier can load
/// that one by name, which the managed runtime then refuses by crashing the process.
/// </summary>
internal static class WinMLRuntime
{
    private static readonly Lazy<string?> Loaded = new(Load);

    /// <summary>The path loaded, or null when the package's runtime was not found beside the app.</summary>
    public static string? EnsureLoaded() => Loaded.Value;

    /// <summary>
    /// Where the package's runtime sits: beside the app when it was built for a runtime identifier,
    /// under <c>runtimes/&lt;rid&gt;/native</c> when it was not. Pure.
    /// </summary>
    internal static IReadOnlyList<string> Candidates(string baseDirectory, Architecture architecture)
    {
        string rid = architecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        return
        [
            Path.Combine(baseDirectory, "onnxruntime.dll"),
            Path.Combine(baseDirectory, "runtimes", rid, "native", "onnxruntime.dll"),
        ];
    }

    private static string? Load()
    {
        foreach (string candidate in Candidates(AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture))
        {
            if (File.Exists(candidate))
            {
                // Loaded by path, the module is the one a later load by name finds.
                NativeLibrary.Load(candidate);
                return candidate;
            }
        }

        return null;
    }
}
