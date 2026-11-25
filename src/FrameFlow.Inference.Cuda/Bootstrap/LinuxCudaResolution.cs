// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Inference.Cuda;

/// <summary>
/// The outcome of asking the native loader for one library: whether it
/// loaded and, when it did not, the loader's message.
/// </summary>
/// <param name="Loaded">Whether the library is now loaded in the process.</param>
/// <param name="Error">The loader's message when <paramref name="Loaded"/> is false.</param>
internal readonly record struct NativeLoadAttempt(bool Loaded, string? Error)
{
    /// <summary>A successful load.</summary>
    public static NativeLoadAttempt Success { get; } = new(true, null);

    /// <summary>A failed load carrying the loader's message.</summary>
    public static NativeLoadAttempt Failed(string? error) => new(false, error);
}

/// <summary>
/// One shared library the ONNX Runtime CUDA execution provider needs on
/// Linux, named by its soname.
/// </summary>
/// <param name="Soname">The soname, e.g. <c>libcudart.so.12</c>.</param>
/// <param name="Component">The layer a missing copy is reported as.</param>
/// <param name="Required">
/// Whether a missing copy makes the layer missing. A library that is not
/// required is loaded from the app-local directory when it is there, so
/// that a library loaded from that directory finds it, and is otherwise
/// left to the loader.
/// </param>
/// <param name="AppLocalAllowed">
/// Whether an app-local copy may be loaded. False only for the driver,
/// whose user-space library has to match the kernel module and is never
/// redistributed.
/// </param>
internal sealed record CudaNativeLibrary(
    string Soname,
    MissingNativeComponent Component,
    bool Required,
    bool AppLocalAllowed = true
);

/// <summary>Where a library that loaded came from.</summary>
internal enum CudaLibrarySource
{
    /// <summary>Loaded by absolute path from the app-local <c>runtimes/{rid}/native/</c> directory.</summary>
    AppLocal,

    /// <summary>Loaded by soname through the system loader's search path.</summary>
    LoaderSearchPath,
}

/// <summary>A library that loaded, and from where.</summary>
/// <param name="Library">The library.</param>
/// <param name="LoadedFrom">The absolute path or soname that was passed to the loader.</param>
/// <param name="Source">Whether it came from the app-local directory or the loader's search path.</param>
internal readonly record struct CudaLibraryLoad(
    CudaNativeLibrary Library,
    string LoadedFrom,
    CudaLibrarySource Source
);

/// <summary>A library that did not load, with the loader's last message.</summary>
/// <param name="Library">The library.</param>
/// <param name="Error">The loader's message from the last attempt, when it gave one.</param>
internal readonly record struct CudaLibraryFailure(CudaNativeLibrary Library, string? Error);

/// <summary>
/// The verdict of <see cref="LinuxCudaResolution.Resolve"/>: the layers
/// that are missing, what loaded and from where, and what did not.
/// </summary>
/// <param name="Missing">The layers with at least one required library that did not load.</param>
/// <param name="Loaded">The libraries that loaded, in load order.</param>
/// <param name="Failed">
/// The required libraries that did not load, and the optional ones whose
/// app-local copy did not load.
/// </param>
internal sealed record LinuxCudaVerdict(
    MissingNativeComponent Missing,
    IReadOnlyList<CudaLibraryLoad> Loaded,
    IReadOnlyList<CudaLibraryFailure> Failed
)
{
    /// <summary>The load of <paramref name="soname"/>, or <see langword="null"/> when it did not load.</summary>
    public CudaLibraryLoad? Find(string soname)
    {
        foreach (var load in Loaded)
        {
            if (string.Equals(load.Library.Soname, soname, StringComparison.Ordinal))
                return load;
        }
        return null;
    }

    /// <summary>The loader's message for the first required library of <paramref name="component"/> that did not load.</summary>
    public string? FirstErrorFor(MissingNativeComponent component)
    {
        foreach (var failure in Failed)
        {
            if (failure.Library.Required && failure.Library.Component == component)
                return failure.Error ?? $"{failure.Library.Soname} could not be loaded.";
        }
        return null;
    }
}

/// <summary>How the bootstrapper treats the platform it runs on.</summary>
internal enum CudaBootstrapPlatform
{
    /// <summary>Windows: directory discovery and a PATH prepend (<see cref="CudaPathResolution"/>).</summary>
    Windows,

    /// <summary>Linux x64: soname loads and app-local preloads (<see cref="LinuxCudaResolution"/>).</summary>
    LinuxX64,

    /// <summary>Anything else. ONNX Runtime ships no CUDA build for it.</summary>
    Unsupported,
}

/// <summary>
/// Pure decisions for the Linux side of the CUDA bootstrap: which shared
/// libraries the ONNX Runtime CUDA execution provider needs, in which
/// order to load them, whether an app-local copy or the loader's search
/// path supplies each one, and which layers are missing as a result.
/// </summary>
/// <remarks>
/// <para>
/// <b>Functional core.</b> Nothing here touches the loader or the
/// filesystem. The shell (<see cref="CudaDllResolver"/>) lists the
/// app-local directory and passes <c>NativeLibrary.Load</c> in as the
/// loader; the tests pass a fake loader over a synthetic table of
/// loadable libraries, so the verdict is reproducible on every OS with
/// no CUDA install and no GPU.
/// </para>
/// <para>
/// <b>Why loading is the probe.</b> On Linux the loader reads
/// <c>LD_LIBRARY_PATH</c> once, at process start, so changing it in
/// process does nothing. What does work is loading a library by absolute
/// path: once a library with a given soname is loaded, a later load of
/// that soname, including the one ONNX Runtime's
/// <c>libonnxruntime_providers_cuda.so</c> makes for its dependencies,
/// resolves to the copy already in the process. A successful load is
/// therefore both the evidence that a layer is present and the activation
/// that makes it usable.
/// </para>
/// </remarks>
internal static class LinuxCudaResolution
{
    /// <summary>The CUDA driver's user-space library, installed with the NVIDIA driver.</summary>
    internal const string DriverSoname = "libcuda.so.1";

    /// <summary>The CUDA 12 runtime, the library whose location is reported as the toolkit directory.</summary>
    internal const string CudaRuntimeSoname = "libcudart.so.12";

    /// <summary>cuDNN 9, the library whose location is reported as the cuDNN directory.</summary>
    internal const string CudnnSoname = "libcudnn.so.9";

    /// <summary>
    /// The libraries the ONNX Runtime 1.26 CUDA execution provider needs on
    /// Linux, in an order where each one's dependencies come before it.
    /// The required ones are the provider's direct dependencies, plus the
    /// driver. The cuDNN 9 sub-libraries are loaded at run time by
    /// <c>libcudnn.so.9</c> itself; they are preloaded from the app-local
    /// directory when present so that an app-local cuDNN finds them, and
    /// are left to the loader otherwise.
    /// </summary>
    internal static IReadOnlyList<CudaNativeLibrary> Libraries { get; } =
    [
        new(DriverSoname, MissingNativeComponent.GpuDriver, Required: true, AppLocalAllowed: false),
        new(CudaRuntimeSoname, MissingNativeComponent.CudaToolkit, Required: true),
        new("libcublasLt.so.12", MissingNativeComponent.CudaToolkit, Required: true),
        new("libcublas.so.12", MissingNativeComponent.CudaToolkit, Required: true),
        new("libcufft.so.11", MissingNativeComponent.CudaToolkit, Required: true),
        new("libcurand.so.10", MissingNativeComponent.CudaToolkit, Required: true),
        new("libnvrtc.so.12", MissingNativeComponent.CudaToolkit, Required: false),
        new("libcudnn_graph.so.9", MissingNativeComponent.CuDnn, Required: false),
        new("libcudnn_engines_precompiled.so.9", MissingNativeComponent.CuDnn, Required: false),
        new("libcudnn_engines_runtime_compiled.so.9", MissingNativeComponent.CuDnn, Required: false),
        new("libcudnn_heuristic.so.9", MissingNativeComponent.CuDnn, Required: false),
        new("libcudnn_ops.so.9", MissingNativeComponent.CuDnn, Required: false),
        new("libcudnn_cnn.so.9", MissingNativeComponent.CuDnn, Required: false),
        new("libcudnn_adv.so.9", MissingNativeComponent.CuDnn, Required: false),
        new(CudnnSoname, MissingNativeComponent.CuDnn, Required: true),
    ];

    /// <summary>
    /// Classifies the running platform for the bootstrapper. Linux is
    /// supported on x64 only, the one Linux architecture
    /// <c>Microsoft.ML.OnnxRuntime.Gpu</c> ships a CUDA build for.
    /// </summary>
    internal static CudaBootstrapPlatform ClassifyPlatform(
        bool isWindows,
        bool isLinux,
        Architecture processArchitecture
    )
    {
        if (isWindows)
            return CudaBootstrapPlatform.Windows;
        if (isLinux && processArchitecture == Architecture.X64)
            return CudaBootstrapPlatform.LinuxX64;
        return CudaBootstrapPlatform.Unsupported;
    }

    /// <summary>
    /// The runtime identifier of the app-local <c>runtimes/{rid}/native/</c>
    /// directory for a platform, e.g. <c>win-x64</c> or <c>linux-x64</c>,
    /// or <see langword="null"/> for a platform with no such directory.
    /// </summary>
    internal static string? AppLocalRid(bool isWindows, bool isLinux, Architecture processArchitecture)
    {
        var os = isWindows ? "win" : isLinux ? "linux" : null;
        var arch = processArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        return os is null || arch is null ? null : $"{os}-{arch}";
    }

    /// <summary>
    /// Loads every library in <paramref name="libraries"/> it can and
    /// reports which layers are missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// App-local copies go first. Each library whose soname is in
    /// <paramref name="appLocalFiles"/> is loaded by absolute path, in
    /// table order, and the failures are retried while a pass loads
    /// something new, so a copy whose dependency comes later in the table
    /// still loads. A required library that did not load from the app-local
    /// directory is then loaded by soname, which searches
    /// <c>LD_LIBRARY_PATH</c>, the <c>ld.so</c> cache and the default
    /// directories.
    /// </para>
    /// <para>
    /// A layer is missing when any of its required libraries did not load.
    /// </para>
    /// </remarks>
    /// <param name="appLocalNativeDir">
    /// The app-local <c>runtimes/linux-x64/native</c> directory, or
    /// <see langword="null"/> when it does not exist.
    /// </param>
    /// <param name="appLocalFiles">The file names in <paramref name="appLocalNativeDir"/>.</param>
    /// <param name="load">The loader: an absolute path or a soname in, the outcome out.</param>
    /// <param name="libraries">The library table. Defaults to <see cref="Libraries"/>.</param>
    internal static LinuxCudaVerdict Resolve(
        string? appLocalNativeDir,
        IReadOnlySet<string> appLocalFiles,
        Func<string, NativeLoadAttempt> load,
        IReadOnlyList<CudaNativeLibrary>? libraries = null
    )
    {
        ArgumentNullException.ThrowIfNull(appLocalFiles);
        ArgumentNullException.ThrowIfNull(load);
        libraries ??= Libraries;

        var loaded = new Dictionary<CudaNativeLibrary, CudaLibraryLoad>();
        var order = new List<CudaNativeLibrary>();
        var lastError = new Dictionary<CudaNativeLibrary, string?>();

        // 1. App-local copies, retried while a pass makes progress.
        var pending = new List<CudaNativeLibrary>();
        if (appLocalNativeDir is not null)
        {
            foreach (var library in libraries)
            {
                if (library.AppLocalAllowed && appLocalFiles.Contains(library.Soname))
                    pending.Add(library);
            }
        }

        var progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (var library in pending.ToArray())
            {
                var path = JoinLinuxPath(appLocalNativeDir!, library.Soname);
                var attempt = load(path);
                if (attempt.Loaded)
                {
                    loaded[library] = new CudaLibraryLoad(library, path, CudaLibrarySource.AppLocal);
                    order.Add(library);
                    pending.Remove(library);
                    progress = true;
                }
                else
                {
                    lastError[library] = attempt.Error;
                }
            }
        }

        // 2. The loader's search path, for required libraries still missing.
        foreach (var library in libraries)
        {
            if (!library.Required || loaded.ContainsKey(library))
                continue;
            var attempt = load(library.Soname);
            if (attempt.Loaded)
            {
                loaded[library] = new CudaLibraryLoad(
                    library,
                    library.Soname,
                    CudaLibrarySource.LoaderSearchPath
                );
                order.Add(library);
            }
            else
            {
                lastError[library] = attempt.Error;
            }
        }

        var missing = MissingNativeComponent.None;
        var failed = new List<CudaLibraryFailure>();
        foreach (var library in libraries)
        {
            if (loaded.ContainsKey(library))
                continue;
            if (library.Required)
                missing |= library.Component;
            if (lastError.TryGetValue(library, out var error))
                failed.Add(new CudaLibraryFailure(library, error));
        }

        return new LinuxCudaVerdict(missing, order.ConvertAll(l => loaded[l]), failed);
    }

    /// <summary>
    /// The directory a loaded library's file lives in, read from the lines
    /// of <c>/proc/self/maps</c>. A library loaded by soname is mapped from
    /// its real file, e.g. <c>libcudart.so.12.9.79</c>, so a file named
    /// <paramref name="soname"/> or <paramref name="soname"/> followed by a
    /// dot and more version matches. Returns <see langword="null"/> when no
    /// mapping matches.
    /// </summary>
    internal static string? DirectoryOfLoaded(IEnumerable<string> mapsLines, string soname)
    {
        ArgumentNullException.ThrowIfNull(mapsLines);
        ArgumentException.ThrowIfNullOrEmpty(soname);

        foreach (var line in mapsLines)
        {
            // address perms offset dev inode pathname: the path is the
            // first '/'-led field, and may contain spaces.
            var start = line.IndexOf('/', StringComparison.Ordinal);
            if (start < 0)
                continue;
            var path = line[start..].TrimEnd();
            const string deleted = " (deleted)";
            if (path.EndsWith(deleted, StringComparison.Ordinal))
                path = path[..^deleted.Length];

            var slash = path.LastIndexOf('/');
            var name = path[(slash + 1)..];
            if (
                string.Equals(name, soname, StringComparison.Ordinal)
                || name.StartsWith(soname + ".", StringComparison.Ordinal)
            )
            {
                return slash == 0 ? "/" : path[..slash];
            }
        }
        return null;
    }

    /// <summary>
    /// Joins a Linux directory and a file name with <c>/</c>. Not
    /// <see cref="Path.Join(string, string)"/>, which uses the separator of
    /// the host it runs on rather than of the path's own platform.
    /// </summary>
    internal static string JoinLinuxPath(string directory, string fileName) =>
        directory.EndsWith('/') ? directory + fileName : directory + "/" + fileName;
}
