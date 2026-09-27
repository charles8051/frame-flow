// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace FrameFlow.Inference.WinML;

/// <summary>
/// Loads the ONNX Runtime Windows ML ships before anything else can load one. Windows carries an
/// older <c>onnxruntime.dll</c> in System32, and an app built without a runtime identifier can load
/// that one by name, which the managed runtime then refuses by crashing the process.
/// </summary>
internal static class WinMLRuntime
{
    private static readonly Lazy<string> Loaded = new(Load);

    /// <summary>The path of the runtime loaded.</summary>
    /// <exception cref="PlatformNotSupportedException">The process is neither x64 nor Arm64.</exception>
    /// <exception cref="DllNotFoundException">No runtime but the one Windows carries could be found.</exception>
    public static string EnsureLoaded() => Loaded.Value;

    /// <summary>
    /// Where the package's runtime sits: beside the app when it was built for a runtime identifier,
    /// under <c>runtimes/&lt;rid&gt;/native</c> when it was not. Empty for an architecture the package
    /// ships no runtime for. Pure.
    /// </summary>
    internal static IReadOnlyList<string> Candidates(string baseDirectory, Architecture architecture)
    {
        string? rid = architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => null,
        };
        return rid is null
            ? []
            : [Path.Combine(baseDirectory, "onnxruntime.dll"), Path.Combine(baseDirectory, "runtimes", rid, "native", "onnxruntime.dll")];
    }

    /// <summary>True when <paramref name="path"/> is in <paramref name="systemDirectory"/>, where Windows keeps its own copy. Pure.</summary>
    internal static bool IsSystemCopy(string path, string systemDirectory) =>
        string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(systemDirectory).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string Load()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var candidates = Candidates(AppContext.BaseDirectory, architecture);
        if (candidates.Count == 0)
        {
            throw new PlatformNotSupportedException(
                $"Windows ML ships ONNX Runtime for x64 and Arm64 processes; this one is {architecture}.");
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                // Loaded by path, the module is the one a later load by name finds.
                NativeLibrary.Load(candidate);
                return candidate;
            }
        }

        // Elsewhere, as ONNX Runtime's own import would find it: a single-file app extracts it, for
        // one. Only Windows' copy is refused.
        if (NativeLibrary.TryLoad("onnxruntime", typeof(OrtEnv).Assembly, searchPath: null, out nint handle))
        {
            string path = ModulePath(handle);
            if (!IsSystemCopy(path, Environment.SystemDirectory))
                return path;

            throw new DllNotFoundException(
                $"Windows ML's onnxruntime.dll was not found, and the only one that loads is Windows' own ({path}), "
                    + "which this package's managed runtime cannot use. Build the app for a Windows runtime identifier, "
                    + "or publish the package's runtimes folder beside it.");
        }

        throw new DllNotFoundException(
            $"Windows ML's onnxruntime.dll was not found beside the app or under {candidates[^1]}. Build the app for a "
                + "Windows runtime identifier, or publish the package's runtimes folder beside it.");
    }

    private static string ModulePath(nint module)
    {
        var buffer = new char[32768];
        int length = GetModuleFileNameW(module, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "(unknown path)";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetModuleFileNameW(nint module, [Out] char[] fileName, int size);
}
