// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Native;

/// <summary>
/// Initializes the FFmpeg native environment by resolving binary paths and loading bindings.
/// </summary>
/// <remarks>
/// <para>
/// This class owns the native loading context for the lifetime of the application.
/// It should be registered as a singleton and initialized exactly once via
/// <see cref="Initialize"/>, typically from the hosted service wrapper.
/// </para>
/// <para>
/// All platform-specific path resolution logic is encapsulated here and must not
/// leak into higher-layer projects such as <c>FrameFlow.Decoding</c>.
/// </para>
/// </remarks>
public sealed class FrameFlowBootstrapper : IFrameFlowBootstrapper
{
    private readonly FrameFlowNativeOptions _options;
    private readonly ILogger<FrameFlowBootstrapper> _logger;
    private readonly IFfmpegLibraryLoader _loader;

    // 0 = not initialized, 1 = initialized. Used with Interlocked for thread safety.
    private int _initState;

    // Cached result for "already initialized" fast path — volatile to ensure visibility.
    private FrameFlowBootstrapResult? _cachedResult;

    /// <summary>
    /// Initializes a new instance of <see cref="FrameFlowBootstrapper"/> with the production
    /// <see cref="FfmpegNativeLibraryLoader"/>. No logging is emitted; use the
    /// <see cref="FrameFlowBootstrapper(FrameFlowNativeOptions, ILoggerFactory)"/> overload
    /// when logging is available.
    /// </summary>
    /// <param name="options">Native options controlling binary resolution strategy.</param>
    public FrameFlowBootstrapper(FrameFlowNativeOptions options)
        : this(options, NullLoggerFactory.Instance) { }

    /// <summary>
    /// Initializes a new instance of <see cref="FrameFlowBootstrapper"/> with a logger factory
    /// and the production <see cref="FfmpegNativeLibraryLoader"/>.
    /// </summary>
    /// <param name="options">Native options controlling binary resolution strategy.</param>
    /// <param name="loggerFactory">
    /// Logger factory used to create typed loggers for the bootstrapper and the native library
    /// loader. Pass <see cref="NullLoggerFactory.Instance"/> to suppress all output.
    /// </param>
    public FrameFlowBootstrapper(FrameFlowNativeOptions options, ILoggerFactory loggerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _logger = loggerFactory.CreateLogger<FrameFlowBootstrapper>();
        _loader = new FfmpegNativeLibraryLoader(
            loggerFactory.CreateLogger<FfmpegNativeLibraryLoader>()
        );
    }

    /// <summary>
    /// Initializes a new instance of <see cref="FrameFlowBootstrapper"/> with explicit control
    /// over the library loading strategy. Intended for use in tests that inject a stub loader.
    /// </summary>
    /// <param name="options">Native options controlling binary resolution strategy.</param>
    /// <param name="logger">Logger for bootstrap diagnostics events.</param>
    /// <param name="loader">
    /// The library loader to use. Pass a stub implementation to avoid requiring real FFmpeg
    /// binaries in unit tests.
    /// </param>
    internal FrameFlowBootstrapper(
        FrameFlowNativeOptions options,
        ILogger<FrameFlowBootstrapper> logger,
        IFfmpegLibraryLoader loader
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    /// <inheritdoc />
    public bool IsInitialized => Volatile.Read(ref _initState) == 1;

    /// <inheritdoc />
    /// <remarks>
    /// This method is thread-safe. Concurrent callers will each receive a result, but
    /// initialization logic executes exactly once. Subsequent callers receive a cached
    /// "already initialized" result immediately.
    /// </remarks>
    public FrameFlowBootstrapResult Initialize()
    {
        // Fast path: already initialized — return the cached result.
        if (Volatile.Read(ref _initState) == 1)
        {
            _logger.LogDebug("FrameFlow bootstrapper is already initialized; skipping.");
            return BuildAlreadyInitializedResult();
        }

        // Race to be the one thread that initializes.
        if (Interlocked.CompareExchange(ref _initState, 1, 0) != 0)
        {
            // Another thread won the race.
            _logger.LogDebug(
                "FrameFlow bootstrapper initialization lost the race; already initialized by another thread."
            );
            return BuildAlreadyInitializedResult();
        }

        // This thread won — perform initialization.
        var binarySource = ResolveBinarySource();
        var searchPath = ResolvePathForSource(binarySource);

        _logger.LogInformation(
            "FrameFlow native bootstrap starting. BinarySource={BinarySource}, SearchPath={SearchPath}",
            binarySource,
            searchPath ?? "(none)"
        );

        FrameFlowBootstrapResult result;

        if (binarySource == FfmpegBinarySource.Unknown)
        {
            // No resolution strategy is enabled — report failure immediately.
            _logger.LogError(
                "FrameFlow native bootstrap cannot proceed: no binary resolution strategy is configured. "
                    + "Enable UseBundledBinaries, ProbeSystemLibraries, or set CustomFfmpegPath."
            );

            result = new FrameFlowBootstrapResult(
                IsSuccess: false,
                ResolvedPath: null,
                BinarySource: FfmpegBinarySource.Unknown,
                Message: "No FFmpeg binary resolution strategy is configured. "
                    + "Set CustomFfmpegPath, enable UseBundledBinaries, or enable ProbeSystemLibraries."
            );
        }
        else
        {
            var loadResult = _loader.TryLoad(searchPath, binarySource);

            // When bundled loading fails and the system probe is also enabled, fall through
            // to the system source rather than failing immediately. This is the expected
            // behavior when both UseBundledBinaries and ProbeSystemLibraries are true.
            if (
                !loadResult.IsSuccess
                && binarySource == FfmpegBinarySource.Bundled
                && _options.ProbeSystemLibraries
            )
            {
                _logger.LogWarning(
                    "Bundled FFmpeg loading failed; falling back to system library probe. "
                        + "Bundled error: {Error}",
                    loadResult.ErrorMessage
                );

                binarySource = FfmpegBinarySource.System;
                searchPath = ResolveSystemPath();

                _logger.LogInformation(
                    "Retrying with BinarySource={BinarySource}, SearchPath={SearchPath}",
                    binarySource,
                    searchPath ?? "(OS loader default)"
                );

                loadResult = _loader.TryLoad(searchPath, binarySource);
            }

            if (loadResult.IsSuccess)
            {
                var major = Interop.FFAvUtil.AvVersionMajor(loadResult.AvutilVersion);
                var minor = Interop.FFAvUtil.AvVersionMinor(loadResult.AvutilVersion);
                var micro = Interop.FFAvUtil.AvVersionMicro(loadResult.AvutilVersion);

                _logger.LogInformation(
                    "FrameFlow native bootstrap completed successfully. "
                        + "BinarySource={BinarySource}, AvutilVersion={Major}.{Minor}.{Micro}",
                    binarySource,
                    major,
                    minor,
                    micro
                );

                // ADR-0033: probe hardware-decode capabilities now that libavutil
                // is callable. Probing is opt-out via FrameFlowNativeOptions to
                // accommodate container environments where the GPU-init
                // diagnostics would be noisy.
                HardwareDecodeCapabilities capabilities;
                if (_options.SkipHardwareProbe)
                {
                    _logger.LogDebug(
                        "Hardware decode probe skipped (FrameFlowNativeOptions.SkipHardwareProbe=true)."
                    );
                    capabilities = HardwareDecodeCapabilities.Empty;
                }
                else
                {
                    capabilities = HardwareDecodeProbe.Run(_logger);
                    var initializedCount = 0;
                    foreach (var b in capabilities.Available)
                    {
                        if (b.Initialized)
                            initializedCount++;
                    }
                    _logger.LogInformation(
                        "Hardware decode probe complete: {Initialized}/{Total} backends usable.",
                        initializedCount,
                        capabilities.Available.Count
                    );
                }

                result = new FrameFlowBootstrapResult(
                    IsSuccess: true,
                    ResolvedPath: searchPath,
                    BinarySource: binarySource,
                    Message: $"FFmpeg avutil {major}.{minor}.{micro} loaded from {binarySource}.",
                    Capabilities: capabilities
                );
            }
            else
            {
                _logger.LogError(
                    "FrameFlow native bootstrap failed. BinarySource={BinarySource}, Error={Error}",
                    binarySource,
                    loadResult.ErrorMessage
                );

                result = new FrameFlowBootstrapResult(
                    IsSuccess: false,
                    ResolvedPath: searchPath,
                    BinarySource: binarySource,
                    Message: loadResult.ErrorMessage
                        ?? "FFmpeg library load failed with no additional detail."
                );
            }
        }

        // Cache the result so the "already initialized" fast path can serve it.
        Volatile.Write(ref _cachedResult, result);
        return result;
    }

    private FrameFlowBootstrapResult BuildAlreadyInitializedResult()
    {
        // If we have a cached result from the winning initialization thread, return it.
        var cached = Volatile.Read(ref _cachedResult);
        if (cached is not null)
        {
            return cached with { Message = "FrameFlow bootstrapper is already initialized." };
        }

        // Fallback for the very narrow window where _initState is 1 but _cachedResult
        // has not yet been written (winning thread is still in progress). Spin briefly.
        var spin = new SpinWait();
        while (Volatile.Read(ref _cachedResult) is null)
            spin.SpinOnce();

        return Volatile.Read(ref _cachedResult)! with
        {
            Message = "FrameFlow bootstrapper is already initialized.",
        };
    }

    private FfmpegBinarySource ResolveBinarySource()
    {
        if (!string.IsNullOrWhiteSpace(_options.CustomFfmpegPath))
        {
            _logger.LogDebug(
                "Binary source resolved to CustomPath. CustomFfmpegPath='{Path}'",
                _options.CustomFfmpegPath
            );
            return FfmpegBinarySource.CustomPath;
        }

        if (_options.UseBundledBinaries)
        {
            _logger.LogDebug("Binary source resolved to Bundled (UseBundledBinaries=true).");
            return FfmpegBinarySource.Bundled;
        }

        if (_options.ProbeSystemLibraries)
        {
            _logger.LogDebug("Binary source resolved to System (ProbeSystemLibraries=true).");
            return FfmpegBinarySource.System;
        }

        _logger.LogDebug(
            "Binary source resolved to Unknown — no resolution strategy is enabled. "
                + "Set CustomFfmpegPath, UseBundledBinaries, or ProbeSystemLibraries."
        );
        return FfmpegBinarySource.Unknown;
    }

    private string? ResolvePathForSource(FfmpegBinarySource source) =>
        source switch
        {
            FfmpegBinarySource.CustomPath => _options.CustomFfmpegPath,
            FfmpegBinarySource.Bundled => ResolveBundledPath(),
            // System and Unknown: no explicit path — OS loader handles it
            _ => null,
        };

    /// <summary>
    /// Resolves the bundled binary path using the NuGet runtime layout convention:
    /// <c>{appBaseDir}/runtimes/{rid}/native/</c> (ADR-0014).
    /// </summary>
    /// <remarks>
    /// For single-file publish with <c>IncludeNativeLibrariesForSelfExtract=true</c>,
    /// the .NET runtime extracts native libraries to a temp directory before managed
    /// code starts. <c>AppContext.BaseDirectory</c> points to the exe folder in .NET 6+,
    /// not the extraction dir, so we also probe the bundle extraction base path.
    /// </remarks>
    private string? ResolveBundledPath()
    {
        var rid = RuntimeIdentifierHelper.Current;
        var appBase = AppContext.BaseDirectory;

        _logger.LogDebug(
            "Resolving bundled FFmpeg path. RID='{Rid}', AppBase='{AppBase}'",
            rid,
            appBase
        );

        // Primary: standard NuGet runtime layout (development build or regular publish).
        var candidate = Path.Combine(appBase, "runtimes", rid, "native");
        _logger.LogDebug("Probing primary bundled path: '{Candidate}'", candidate);

        if (Directory.Exists(candidate))
        {
            _logger.LogDebug("Primary bundled path found: '{Candidate}'", candidate);
            return candidate;
        }

        _logger.LogDebug(
            "Primary bundled path not found. Probing single-file bundle extraction directory."
        );

        // Single-file publish with IncludeNativeLibrariesForSelfExtract=true: .NET extracts
        // native libraries to {extractBase}/{appName}/{hash}/runtimes/{rid}/native/ before
        // starting managed code. AppContext.BaseDirectory is the exe dir in .NET 6+, so we
        // probe the bundle extraction directory explicitly.
        var extractedCandidate = ProbeBundleExtractionDirectory(rid);
        if (extractedCandidate is not null)
        {
            _logger.LogDebug(
                "Found native layout in bundle extraction directory: '{Path}'",
                extractedCandidate
            );
            return extractedCandidate;
        }

        // Fallback: some publish scenarios flatten the layout and place DLLs next to the assembly.
        _logger.LogDebug(
            "Bundle extraction directory probe failed. Falling back to AppBase='{AppBase}'",
            appBase
        );
        return appBase;
    }

    /// <summary>
    /// Searches the .NET single-file bundle extraction directory for the native library layout.
    /// </summary>
    /// <remarks>
    /// The extraction path is <c>{DOTNET_BUNDLE_EXTRACT_BASE_DIR}/{appName}/{hash}/</c>.
    /// The hash subdirectory is not knowable at compile time, so we enumerate and pick the
    /// most recently written directory that contains the expected native layout.
    /// </remarks>
    private string? ProbeBundleExtractionDirectory(string rid)
    {
        _logger.LogDebug("Probing bundle extraction directory for RID='{Rid}'.", rid);

        foreach (var hashDir in BundleExtractionHelper.EnumerateHashDirectories())
        {
            var candidate = Path.Combine(hashDir, "runtimes", rid, "native");
            _logger.LogDebug(
                "Checking extraction hash dir for native layout: '{Candidate}'",
                candidate
            );
            if (Directory.Exists(candidate))
                return candidate;
        }

        _logger.LogDebug("No valid native layout found in bundle extraction directories.");
        return null;
    }

    /// <summary>
    /// Resolves a platform-specific search path for system-installed FFmpeg libraries,
    /// or <see langword="null"/> to let the OS loader apply its own default search rules.
    /// </summary>
    private static string? ResolveSystemPath()
    {
        if (OperatingSystem.IsMacOS())
            return ResolveMacOsHomebrewPath();

        // Linux and Windows: let the OS loader resolve via system library paths.
        return null;
    }

    /// <summary>
    /// Probes well-known Homebrew paths for an installed <c>ffmpeg@7</c> formula.
    /// Returns the lib directory path if found, or <see langword="null"/> to fall through
    /// to bare-name OS loader resolution.
    /// </summary>
    private static string? ResolveMacOsHomebrewPath()
    {
        // Apple Silicon uses /opt/homebrew; Intel Macs use /usr/local.
        var prefix =
            RuntimeIdentifierHelper.Current == "osx-arm64" ? "/opt/homebrew" : "/usr/local";

        // ffmpeg@7 is keg-only so it is not symlinked into the main prefix lib.
        var kegPath = Path.Combine(prefix, "opt", "ffmpeg@7", "lib");
        if (Directory.Exists(kegPath))
            return kegPath;

        // Also check the general Homebrew lib path for users who ran `brew link ffmpeg`.
        var libPath = Path.Combine(prefix, "lib");
        if (Directory.Exists(libPath))
            return libPath;

        return null;
    }
}
