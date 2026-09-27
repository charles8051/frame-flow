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
    /// <exception cref="DllNotFoundException">
    /// No runtime could be found, the one that loaded is older than Windows ML's (Windows' own copy,
    /// or another inference package's), or another onnxruntime.dll was loaded first.
    /// </exception>
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

    /// <summary>The oldest ONNX Runtime this package runs on: what Windows ML 2.3 ships.</summary>
    internal const int MinimumMinor = 27;

    /// <summary>
    /// Why the runtime at <paramref name="path"/>, reporting <paramref name="version"/>, cannot be
    /// used, or null when it can. Pure.
    /// </summary>
    internal static string? Refusal(string version, string path, string systemDirectory)
    {
        string[] parts = version.Split('.');
        if (parts.Length >= 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minor)
            && (major > 1 || (major == 1 && minor >= MinimumMinor)))
        {
            return null;
        }

        string why = IsSystemCopy(path, systemDirectory)
            ? "That is Windows' own copy: build the app for a Windows runtime identifier, or publish the package's runtimes folder beside it."
            : "Another inference package's runtime likely replaced Windows ML's: an app references one of FrameFlow.Inference.Dml, .Cuda and .WinML.";
        return $"The onnxruntime.dll that loaded ({path}) is ONNX Runtime {version}, and Windows ML needs 1.{MinimumMinor} or later. {why}";
    }

    private static string Load()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var candidates = Candidates(AppContext.BaseDirectory, architecture);
        if (candidates.Count == 0)
        {
            throw new PlatformNotSupportedException(
                $"Windows ML ships ONNX Runtime for x64 and Arm64 processes; this one is {architecture}.");
        }

        // Beside the app first; loaded by path, the module is the one a later load by name finds.
        // Elsewhere, as ONNX Runtime's own import would find it: a single-file app extracts it, for one.
        string? path = candidates.FirstOrDefault(File.Exists);
        nint handle;
        if (path is not null)
        {
            handle = NativeLibrary.Load(path);
        }
        else if (NativeLibrary.TryLoad("onnxruntime", typeof(OrtEnv).Assembly, searchPath: null, out handle))
        {
            path = ModulePath(handle);
        }
        else
        {
            throw new DllNotFoundException(
                $"Windows ML's onnxruntime.dll was not found beside the app or under {candidates[^1]}. Build the app for a "
                    + "Windows runtime identifier, or publish the package's runtimes folder beside it.");
        }

        if (Refusal(NativeVersion(handle), path, Environment.SystemDirectory) is { } refusal)
            throw new DllNotFoundException(refusal);

        // ONNX Runtime's imports resolve by name, and Windows answers a name with the module of that
        // name loaded first. If that is not this one, another onnxruntime.dll is already in the process.
        nint byName = GetModuleHandleW("onnxruntime.dll");
        if (byName != 0 && byName != handle)
        {
            throw new DllNotFoundException(
                $"Another onnxruntime.dll ({ModulePath(byName)}) was loaded in this process before Windows ML's ({path}), "
                    + "and ONNX Runtime's calls would reach it. An app references one of FrameFlow.Inference.Dml, .Cuda "
                    + "and .WinML, and loads no other ONNX Runtime.");
        }

        return path;
    }

    /// <summary>The version the runtime reports: <c>OrtGetApiBase()->GetVersionString()</c>.</summary>
    private static unsafe string NativeVersion(nint module)
    {
        var getApiBase = (delegate* unmanaged<nint*>)NativeLibrary.GetExport(module, "OrtGetApiBase");
        var getVersion = (delegate* unmanaged<nint>)getApiBase()[1];
        return Marshal.PtrToStringUTF8(getVersion()) ?? "";
    }

    private static string ModulePath(nint module)
    {
        var buffer = new char[32768];
        int length = GetModuleFileNameW(module, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "(unknown path)";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetModuleFileNameW(nint module, [Out] char[] fileName, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string moduleName);
}
