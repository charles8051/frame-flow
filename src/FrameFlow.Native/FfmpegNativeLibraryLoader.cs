// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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
/// Loading and resolver registration are performed exactly once per process.
/// Subsequent calls are no-ops that return the cached probe result.
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

    // Cached result for when TryLoad is called a second time (e.g., from a second bootstrapper
    // in the same process — rare in production but can occur in tests with multiple instances).
    private static FfmpegLoadResult? _cachedProbeResult;

    public FfmpegNativeLibraryLoader(ILogger<FfmpegNativeLibraryLoader> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public FfmpegLoadResult TryLoad(string? searchPath, FfmpegBinarySource source)
    {
        lock (LoadLock)
        {
            // If we've already probed successfully, return the cached result.
            if (_cachedProbeResult.HasValue)
            {
                _logger.LogDebug("FFmpeg libraries already loaded; returning cached probe result.");
                return _cachedProbeResult.Value;
            }

            // Register the DllImportResolver exactly once per process.
            // This must happen before TryLoadLibrary so that the P/Invoke
            // source-generated stubs are routed through our resolver.
            if (!_resolverRegistered)
            {
                _logger.LogDebug("Registering DllImportResolver for FrameFlow.Native assembly.");
                NativeLibrary.SetDllImportResolver(
                    typeof(FFAvUtil).Assembly,
                    (libraryName, assembly, searchPathDirs) =>
                        ResolveDllImport(libraryName, searchPath)
                );
                _resolverRegistered = true;
            }

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

    private static nint ResolveDllImport(string libraryName, string? searchPath)
    {
        // If we have a cached handle from a prior TryLoadLibrary call, return it immediately.
        lock (LoadLock)
        {
            if (LoadedHandles.TryGetValue(libraryName, out var cached) && cached != 0)
                return cached;
        }

        // On-demand resolution for any library that wasn't pre-loaded.
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

        return 0;
    }

    private static string GetExpectedVersionHint() => "7.x";
}
