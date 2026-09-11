// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Native;

/// <summary>
/// Production <see cref="IFfmpegLibraryLoader"/> that uses <see cref="NativeLibrary"/> to locate
/// and load FFmpeg shared libraries, then registers a <see cref="DllImportResolver"/> so that
/// source-generated P/Invoke calls are routed to the loaded handles.
/// </summary>
/// <remarks>
/// <para>
/// Loading and resolver registration are performed exactly once per process.
/// Subsequent calls are no-ops that return the cached probe result.
/// </para>
/// <para>
/// The resolver is registered from a module initializer rather than from
/// <see cref="TryLoad"/>, so it is in place before the first P/Invoke regardless of whether
/// anything called <see cref="FrameFlowBootstrapper.Initialize"/>. See ADR-0070.
/// </para>
/// </remarks>
internal sealed class FfmpegNativeLibraryLoader : IFfmpegLibraryLoader
{
    private readonly ILogger<FfmpegNativeLibraryLoader> _logger;

    // Handles kept alive for the process lifetime (never freed — bootstrap owns the loading context).
    // Static so that even if multiple loader instances are created, handles are shared.
    private static readonly Dictionary<string, nint> LoadedHandles = [];
    private static readonly object LoadLock = new();

    // Tracks whether the DllImportResolver has been registered for the assembly.
    // SetDllImportResolver can only be called once per assembly per process lifetime.
    private static bool _resolverRegistered;

    // Set when something else got to that one slot first, so FrameFlow's resolver is not
    // installed and the names in FFmpegLibraryResolver are not mapped.
    private static bool _foreignResolverInstalled;

    private const string ForeignResolverMessage =
        "A DllImportResolver is already registered for the FrameFlow.Native assembly, so "
        + "FrameFlow's own resolver could not be installed and the FFmpeg library names are "
        + "not mapped to the packaged files. NativeLibrary.SetDllImportResolver permits one "
        + "resolver per assembly and they cannot be chained. Remove the other resolver, or "
        + "resolve the FFmpeg libraries through it.";

    // Search directory the registered resolver consults when it has to resolve a library on
    // demand. The resolver cannot capture it at registration time: registration now happens in
    // a module initializer, before any options exist. TryLoad updates it on every attempt, so a
    // bootstrap that falls back from bundled to system resolution moves the resolver with it.
    // Read and written under LoadLock.
    private static string? _resolverSearchPath;

    // Guards the implicit bootstrap. Always taken outside LoadLock — the bootstrap it runs takes
    // LoadLock itself, so the order is ImplicitBootstrapLock -> LoadLock and never the reverse.
    private static readonly object ImplicitBootstrapLock = new();
    private static bool _implicitBootstrapAttempted;
    private static FrameFlowBootstrapResult? _implicitBootstrapResult;

    // Set while this thread is inside the implicit bootstrap. The bootstrap's own version probe
    // is a P/Invoke, which re-enters the resolver. Without this flag that call would recurse.
    [ThreadStatic]
    private static bool _inImplicitBootstrap;

    // Cached result for when TryLoad is called a second time (e.g., from a second bootstrapper
    // in the same process — rare in production but can occur in tests with multiple instances).
    private static FfmpegLoadResult? _cachedProbeResult;

    public FfmpegNativeLibraryLoader(ILogger<FfmpegNativeLibraryLoader> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers the <see cref="DllImportResolver"/> as the module loads, before any P/Invoke
    /// in this assembly can run.
    /// </summary>
    /// <remarks>
    /// Registration used to happen inside <see cref="TryLoad"/>, which made every public entry
    /// point below <c>FrameFlow.Player</c> throw <see cref="DllNotFoundException"/> unless the
    /// process had separately run the bootstrap. See ADR-0070.
    /// </remarks>
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Registering a DllImportResolver is the case the rule's own guidance "
            + "allows: it must be in place before the first P/Invoke in this assembly, and no "
            + "consumer call site can guarantee that. It registers a callback and loads nothing."
    )]
    internal static void InstallResolver()
    {
        // A module initializer that throws makes every later use of this assembly fail with a
        // TypeInitializationException raised from wherever it was first touched. Swallow the
        // conflict here and let TryLoad raise it, which is where it surfaced before
        // registration moved into this method.
        try
        {
            EnsureResolverRegistered();
        }
        catch (InvalidOperationException)
        {
            // Recorded in _foreignResolverInstalled. TryLoad reports it.
        }
    }

    private static void EnsureResolverRegistered()
    {
        lock (LoadLock)
        {
            if (_foreignResolverInstalled)
                throw new InvalidOperationException(ForeignResolverMessage);

            if (_resolverRegistered)
                return;

            try
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(FFAvUtil).Assembly,
                    static (libraryName, assembly, searchPathDirs) => ResolveDllImport(libraryName)
                );
            }
            catch (InvalidOperationException)
            {
                // Something else owns this assembly's single resolver slot. FrameFlow's name
                // mapping is not active and cannot be made active — resolvers cannot be
                // chained, and retrying throws again. Claiming registration succeeded would
                // disable packaged avformat/avcodec resolution silently, so say so instead.
                _foreignResolverInstalled = true;
                throw new InvalidOperationException(ForeignResolverMessage);
            }

            _resolverRegistered = true;
        }
    }

    /// <inheritdoc />
    public FfmpegLoadResult TryLoad(string? searchPath, FfmpegBinarySource source)
    {
        lock (LoadLock)
        {
            // If we've already probed successfully, return the cached result.
            if (_cachedProbeResult.HasValue)
            {
                // Unless this caller asked for binaries other than the ones already loaded.
                // FFmpeg loads once per process, so that request cannot be honoured — and
                // reporting success would tell the caller it got binaries it did not get.
                // A warning is not enough on its own: a consumer with no logger configured
                // would run on an unintended build with nothing to show for it.
                if (
                    !string.IsNullOrEmpty(searchPath)
                    && !string.Equals(_resolverSearchPath, searchPath, StringComparison.Ordinal)
                )
                {
                    var loadedPath = _resolverSearchPath ?? "(OS loader default)";

                    _logger.LogWarning(
                        "FFmpeg libraries were already loaded from '{LoadedPath}'; the requested "
                            + "search path '{RequestedPath}' cannot be used.",
                        loadedPath,
                        searchPath
                    );

                    return FfmpegLoadResult.Failure(
                        $"FFmpeg libraries are already loaded from '{loadedPath}', so the "
                            + $"requested path '{searchPath}' cannot be used. Libraries load "
                            + "once per process: bootstrap before the first decode call to "
                            + "control which binaries load. The already-loaded libraries stay "
                            + "usable."
                    );
                }

                _logger.LogDebug("FFmpeg libraries already loaded; returning cached probe result.");
                return _cachedProbeResult.Value;
            }

            // Point the resolver at this attempt's directory before anything can call through it.
            _resolverSearchPath = searchPath;

            // Normally a no-op: the module initializer registered the resolver when this
            // assembly loaded. Kept for the case where that initializer has not run.
            EnsureResolverRegistered();

            // Load each required library in dependency order.
            foreach (var lib in FFmpegLibraryResolver.RequiredLibraries)
            {
                if (LoadedHandles.ContainsKey(lib))
                    continue; // Already loaded in a prior call.

                if (!TryLoadLibrary(lib, searchPath, out var handle))
                {
                    var candidates = string.Join(
                        ", ",
                        FFmpegLibraryResolver.CandidatePaths(lib, searchPath)
                    );

                    var failure = FfmpegLoadResult.Failure(
                        $"Failed to load FFmpeg library '{lib}'. "
                            + $"Searched: [{candidates}]. "
                            + $"Ensure FFmpeg {GetExpectedVersionHint()} is installed "
                            + $"or configure FrameFlowNativeOptions.CustomFfmpegPath."
                    );

                    return failure; // Do not cache failure — allow retry with different path.
                }

                LoadedHandles[lib] = handle;

                _logger.LogDebug(
                    "Loaded FFmpeg library {Library} handle=0x{Handle:X} source={Source}",
                    lib,
                    handle,
                    source
                );
            }
        }

        // Probe outside the lock: call avutil_version() to confirm bindings work.
        try
        {
            var version = FFAvUtil.avutil_version();
            var major = FFAvUtil.AvVersionMajor(version);
            var minor = FFAvUtil.AvVersionMinor(version);
            var micro = FFAvUtil.AvVersionMicro(version);

            _logger.LogInformation(
                "FFmpeg avutil version {Major}.{Minor}.{Micro} confirmed via version probe",
                major,
                minor,
                micro
            );

            var success = FfmpegLoadResult.Success(version);

            lock (LoadLock)
            {
                _cachedProbeResult = success;
            }

            return success;
        }
        catch (Exception ex)
        {
            return FfmpegLoadResult.Failure(
                $"FFmpeg libraries loaded but version probe failed: {ex.Message}"
            );
        }
    }

    private bool TryLoadLibrary(string libraryName, string? searchPath, out nint handle)
    {
        foreach (var candidate in FFmpegLibraryResolver.CandidatePaths(libraryName, searchPath))
        {
            if (NativeLibrary.TryLoad(candidate, out handle))
            {
                _logger.LogDebug("Resolved {Library} via '{Candidate}'", libraryName, candidate);
                return true;
            }

            _logger.LogDebug("Candidate not found: '{Candidate}'", candidate);
        }

        _logger.LogWarning(
            "Failed to load FFmpeg library '{Library}'. SearchPath='{SearchPath}'. All candidates exhausted.",
            libraryName,
            searchPath ?? "(none)"
        );
        handle = 0;
        return false;
    }

    private static nint ResolveDllImport(string libraryName)
    {
        // If we have a cached handle from a prior TryLoadLibrary call, return it immediately.
        if (TryGetLoadedHandle(libraryName, out var cached))
            return cached;

        // Nothing has loaded FFmpeg yet. Do it now, so that a consumer of FrameFlow.Decoding
        // can open a file without first going through the player layer or the DI host.
        // A load that already succeeded means this miss is a library outside the required set
        // rather than a missing bootstrap, so leave that case to on-demand probing below.
        FrameFlowBootstrapResult? bootstrap = null;
        if (!HasSuccessfulLoad())
        {
            bootstrap = TryImplicitBootstrap();

            if (TryGetLoadedHandle(libraryName, out cached))
                return cached;
        }

        string? searchPath;
        lock (LoadLock)
        {
            searchPath = _resolverSearchPath;
        }

        // On-demand resolution for any library that wasn't pre-loaded — avfilter and avdevice
        // are outside FFmpegLibraryResolver.RequiredLibraries and arrive here.
        foreach (var candidate in FFmpegLibraryResolver.CandidatePaths(libraryName, searchPath))
        {
            if (NativeLibrary.TryLoad(candidate, out var h))
            {
                lock (LoadLock)
                {
                    LoadedHandles[libraryName] = h;
                }
                return h;
            }
        }

        // The implicit bootstrap ran and failed. Returning 0 here produces "Unable to load DLL
        // 'avformat'", which sends the reader after the runtime package rather than the
        // environment. Carry the bootstrap's diagnostic instead.
        if (bootstrap is { IsSuccess: false })
        {
            throw new DllNotFoundException(
                $"FrameFlow could not load the FFmpeg library '{libraryName}'. "
                    + $"The implicit native bootstrap failed: {bootstrap.Message} "
                    + "Call FrameFlowBootstrapper.Initialize() with FrameFlowNativeOptions "
                    + "configured for this environment before decoding."
            );
        }

        return 0;
    }

    private static bool HasSuccessfulLoad()
    {
        lock (LoadLock)
        {
            return _cachedProbeResult.HasValue;
        }
    }

    private static bool TryGetLoadedHandle(string libraryName, out nint handle)
    {
        lock (LoadLock)
        {
            if (LoadedHandles.TryGetValue(libraryName, out handle) && handle != 0)
                return true;
        }

        handle = 0;
        return false;
    }

    /// <summary>
    /// Runs a default bootstrap the first time a P/Invoke needs a library that no explicit
    /// bootstrap has loaded.
    /// </summary>
    /// <returns>
    /// The bootstrap result, or <see langword="null"/> when no bootstrap ran because this call
    /// is the running bootstrap's own version probe re-entering the resolver.
    /// </returns>
    private static FrameFlowBootstrapResult? TryImplicitBootstrap()
    {
        if (_inImplicitBootstrap)
            return null;

        lock (ImplicitBootstrapLock)
        {
            if (_implicitBootstrapAttempted)
                return _implicitBootstrapResult;

            _inImplicitBootstrap = true;
            try
            {
                // The hardware-decode probe is skipped. Its result is reachable only through a
                // FrameFlowBootstrapResult, which an implicit bootstrap never hands to anyone,
                // and av_hwdevice_ctx_create is not something to run from inside a resolver
                // callback. Hardware-decode capability discovery still requires an explicit
                // Initialize().
                var options = new FrameFlowNativeOptions { SkipHardwareProbe = true };
                _implicitBootstrapResult = new FrameFlowBootstrapper(options).Initialize();
            }
            catch (Exception)
            {
                // The resolver has no logger and no caller that could handle this. Leave the
                // result null; the caller falls through to on-demand candidate probing.
                _implicitBootstrapResult = null;
            }
            finally
            {
                _inImplicitBootstrap = false;
                _implicitBootstrapAttempted = true;
            }

            return _implicitBootstrapResult;
        }
    }

    private static string GetExpectedVersionHint() => "7.x";
}
