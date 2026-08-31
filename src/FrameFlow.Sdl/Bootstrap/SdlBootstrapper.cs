// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
// Silk.NET.SDL.Sdl is the underlying SDL2 wrapper. The FrameFlow.SDL namespace
// uses the ALL-CAPS form per .NET acronym conventions so it does not collide.
using SdlApi = Silk.NET.SDL.Sdl;

namespace FrameFlow.SDL.Bootstrap;

/// <summary>
/// Resolves and loads the SDL2 native library for all publish modes, including
/// self-contained single-file publish (ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// Consuming code should obtain <see cref="Sdl"/> API instances via
/// <see cref="CreateSdlApi"/> rather than calling <c>Sdl.GetApi()</c> directly.
/// Instances produced by <see cref="CreateSdlApi"/> are backed by a pre-loaded handle
/// that works in all publish modes without PATH manipulation or OS module cache side-effects.
/// </para>
/// <para>
/// Resolution order (first match wins):
/// <list type="number">
///   <item><see cref="SdlNativeOptions.CustomSdlLibraryPath"/> — explicit path, highest priority.</item>
///   <item>App-relative NuGet runtime layout — <c>{AppBase}/runtimes/{rid}/native/{sdlFile}</c>.</item>
///   <item>App-relative root — <c>{AppBase}/{sdlFile}</c> (regular self-contained publish).</item>
///   <item>Bundle extraction probe — hash directories under <c>{extractBase}/{appName}/*/</c> (single-file publish).</item>
///   <item>System library — bare library name via OS loader, final fallback.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class SdlBootstrapper : ISdlBootstrapper
{
    private readonly SdlNativeOptions _options;
    private readonly ILogger<SdlBootstrapper> _logger;

    // 0 = not initialized, 1 = initialized.
    private int _initState;

    // Volatile so the CreateSdlApi fast-path guard is visible across threads.
    private volatile nint _sdlHandle;

    private SdlBootstrapResult? _cachedResult;

    /// <summary>
    /// Initializes a new instance with default options and no logging.
    /// </summary>
    public SdlBootstrapper()
        : this(new SdlNativeOptions(), NullLoggerFactory.Instance) { }

    /// <summary>
    /// Initializes a new instance with the specified options and logger factory.
    /// </summary>
    /// <param name="options">Options controlling SDL2 library resolution.</param>
    /// <param name="loggerFactory">
    /// Logger factory used to create a typed logger. Pass
    /// <see cref="NullLoggerFactory.Instance"/> to suppress all output.
    /// </param>
    public SdlBootstrapper(SdlNativeOptions options, ILoggerFactory loggerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<SdlBootstrapper>();
    }

    /// <inheritdoc />
    public bool IsInitialized => Volatile.Read(ref _initState) == 1;

    /// <inheritdoc />
    public SdlBootstrapResult Initialize()
    {
        // Fast path: already initialized.
        if (Volatile.Read(ref _initState) == 1)
        {
            _logger.LogDebug("SDL bootstrapper is already initialized; skipping.");
            return BuildAlreadyInitializedResult();
        }

        // Race to be the initializing thread.
        if (Interlocked.CompareExchange(ref _initState, 1, 0) != 0)
        {
            _logger.LogDebug(
                "SDL bootstrapper initialization lost the race; already initialized by another thread."
            );
            return BuildAlreadyInitializedResult();
        }

        var sdlFileName = GetSdlFileName();
        _logger.LogDebug(
            "SDL2 library filename for current platform: '{SdlFileName}'",
            sdlFileName
        );

        SdlBootstrapResult result;
        nint handle = 0;

        if (TryResolveLibrary(sdlFileName, out var resolvedPath, out handle))
        {
            _sdlHandle = handle;
            result = new SdlBootstrapResult(
                IsSuccess: true,
                ResolvedLibraryPath: resolvedPath,
                Message: resolvedPath is not null
                    ? $"SDL2 loaded from '{resolvedPath}'."
                    : "SDL2 loaded via system library."
            );

            _logger.LogInformation(
                "SDL bootstrap completed successfully. ResolvedLibraryPath='{Path}'",
                resolvedPath ?? "(system)"
            );
        }
        else
        {
            result = new SdlBootstrapResult(
                IsSuccess: false,
                ResolvedLibraryPath: null,
                Message: $"SDL2 library '{sdlFileName}' could not be found or loaded. "
                    + "Check that SDL2 is installed or bundled with the application."
            );

            _logger.LogError(
                "SDL bootstrap failed. Could not load '{SdlFileName}' from any candidate path.",
                sdlFileName
            );
        }

        Volatile.Write(ref _cachedResult, result);
        return result;
    }

    /// <inheritdoc />
    public SdlApi CreateSdlApi()
    {
        if (!IsInitialized || _sdlHandle == 0)
            throw new InvalidOperationException(
                "SdlBootstrapper must be successfully initialized before creating API instances. "
                    + "Call Initialize() and verify IsSuccess before calling CreateSdlApi()."
            );

#pragma warning disable CA2000 // Ownership of the native context is intentionally transferred to the returned SDL API wrapper.
        return new SdlApi(new SdlNativeContext(_sdlHandle));
#pragma warning restore CA2000
    }

    // ── Resolution helpers ────────────────────────────────────────────────

    private bool TryResolveLibrary(string sdlFileName, out string? resolvedPath, out nint handle)
    {
        var rid = GetCurrentRid();
        var appBase = AppContext.BaseDirectory;

        _logger.LogDebug("Resolving SDL2 library. RID='{Rid}', AppBase='{AppBase}'", rid, appBase);

        // 1. Custom explicit path.
        if (!string.IsNullOrWhiteSpace(_options.CustomSdlLibraryPath))
        {
            _logger.LogDebug(
                "Trying custom SDL library path: '{Path}'",
                _options.CustomSdlLibraryPath
            );
            if (NativeLibrary.TryLoad(_options.CustomSdlLibraryPath, out handle))
            {
                resolvedPath = _options.CustomSdlLibraryPath;
                _logger.LogDebug("SDL2 loaded from custom path: '{Path}'", resolvedPath);
                return true;
            }
            _logger.LogWarning(
                "Custom SDL library path '{Path}' could not be loaded.",
                _options.CustomSdlLibraryPath
            );
        }

        if (_options.UseBundledLibrary)
        {
            // 2. App-relative NuGet runtime layout (development / regular publish).
            var runtimeCandidate = Path.Combine(appBase, "runtimes", rid, "native", sdlFileName);
            _logger.LogDebug("Probing NuGet runtime layout: '{Candidate}'", runtimeCandidate);
            if (NativeLibrary.TryLoad(runtimeCandidate, out handle))
            {
                resolvedPath = runtimeCandidate;
                _logger.LogDebug("SDL2 loaded from NuGet runtime layout: '{Path}'", resolvedPath);
                return true;
            }

            // 3. App-relative root (regular self-contained publish without single-file).
            var rootCandidate = Path.Combine(appBase, sdlFileName);
            _logger.LogDebug("Probing app-relative root: '{Candidate}'", rootCandidate);
            if (NativeLibrary.TryLoad(rootCandidate, out handle))
            {
                resolvedPath = rootCandidate;
                _logger.LogDebug("SDL2 loaded from app-relative root: '{Path}'", resolvedPath);
                return true;
            }

            // 4. Bundle extraction probe (single-file publish).
            _logger.LogDebug(
                "Probing bundle extraction hash directories for '{SdlFileName}'.",
                sdlFileName
            );
            foreach (var hashDir in BundleExtractionHelper.EnumerateHashDirectories())
            {
                var extractCandidate = Path.Combine(hashDir, sdlFileName);
                _logger.LogDebug(
                    "Checking bundle extraction path: '{Candidate}'",
                    extractCandidate
                );
                if (NativeLibrary.TryLoad(extractCandidate, out handle))
                {
                    resolvedPath = extractCandidate;
                    _logger.LogDebug(
                        "SDL2 loaded from bundle extraction directory: '{Path}'",
                        resolvedPath
                    );
                    return true;
                }
            }

            _logger.LogDebug("No bundled SDL2 library found.");
        }

        // 5. System library fallback.
        if (_options.ProbeSystemLibrary)
        {
            // On macOS, SDL2 installed via Homebrew is keg-only and not linked into the
            // system library search path. Probe the well-known keg location first.
            if (OperatingSystem.IsMacOS())
            {
                var homebrewPrefix =
                    RuntimeInformation.OSArchitecture == Architecture.Arm64
                        ? "/opt/homebrew"
                        : "/usr/local";
                var kegCandidate = Path.Combine(homebrewPrefix, "opt", "sdl2", "lib", sdlFileName);
                _logger.LogDebug("Probing macOS Homebrew keg path: '{Candidate}'", kegCandidate);
                if (NativeLibrary.TryLoad(kegCandidate, out handle))
                {
                    resolvedPath = kegCandidate;
                    _logger.LogDebug("SDL2 loaded from Homebrew keg: '{Path}'", resolvedPath);
                    return true;
                }
            }

            _logger.LogDebug("Trying system library fallback for '{SdlFileName}'.", sdlFileName);
            if (NativeLibrary.TryLoad(sdlFileName, out handle))
            {
                resolvedPath = null; // system-resolved, no specific path
                _logger.LogDebug("SDL2 loaded via system library loader.");
                return true;
            }

            // On Linux, try the versioned name as well.
            if (OperatingSystem.IsLinux())
            {
                const string linuxVersioned = "libSDL2-2.0.so.0";
                _logger.LogDebug("Trying Linux versioned fallback: '{Name}'.", linuxVersioned);
                if (NativeLibrary.TryLoad(linuxVersioned, out handle))
                {
                    resolvedPath = null;
                    _logger.LogDebug("SDL2 loaded via Linux versioned system name.");
                    return true;
                }
            }
        }

        resolvedPath = null;
        handle = 0;
        return false;
    }

    private static string GetSdlFileName()
    {
        if (OperatingSystem.IsWindows())
            return "SDL2.dll";
        if (OperatingSystem.IsMacOS())
            return "libSDL2.dylib";
        return "libSDL2.so"; // Linux primary name
    }

    private static string GetCurrentRid()
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

    private SdlBootstrapResult BuildAlreadyInitializedResult()
    {
        var cached = Volatile.Read(ref _cachedResult);
        if (cached is not null)
            return cached with { Message = "SDL bootstrapper is already initialized." };

        var spin = new SpinWait();
        while (Volatile.Read(ref _cachedResult) is null)
            spin.SpinOnce();

        return Volatile.Read(ref _cachedResult)! with
        {
            Message = "SDL bootstrapper is already initialized.",
        };
    }
}
