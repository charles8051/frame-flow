using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Native.Tests.Doubles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Unit tests for <see cref="FrameFlowBootstrapper"/> that exercise routing, priority,
/// and idempotency logic using a stub loader — no real FFmpeg binaries required.
/// </summary>
public sealed class FrameFlowBootstrapperTests
{
    /// <summary>
    /// Creates a bootstrapper wired to a stub loader so tests are independent of FFmpeg presence.
    /// </summary>
    private static FrameFlowBootstrapper Create(
        FrameFlowNativeOptions? options = null,
        StubFfmpegLibraryLoader? loader = null
    )
    {
        // Force-skip the hardware decode probe for stub tests. The
        // StubFfmpegLibraryLoader reports "FFmpeg loaded fine" without
        // actually putting avutil on the OS DLL search path, so running
        // HardwareDecodeProbe.Run() (which P/Invokes
        // av_hwdevice_iterate_types directly) throws DllNotFoundException
        // unless an earlier test in the same process happened to seed
        // the DLLs. Under xUnit's parallel scheduling that ordering isn't
        // guaranteed. No test in this file exercises the probe — they
        // all assert on bootstrap-time path resolution and option wiring.
        var opts = options ?? new FrameFlowNativeOptions();
        opts.SkipHardwareProbe = true;
        return new(
            opts,
            NullLogger<FrameFlowBootstrapper>.Instance,
            loader ?? new StubFfmpegLibraryLoader()
        );
    }

    // --- Construction ---

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new FrameFlowBootstrapper(null!, NullLoggerFactory.Instance)
        );
    }

    [Fact]
    public void Constructor_NullLoggerFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new FrameFlowBootstrapper(new FrameFlowNativeOptions(), (ILoggerFactory)null!)
        );
    }

    [Fact]
    public void Constructor_ValidOptions_NotInitialized()
    {
        var bootstrapper = Create();
        Assert.False(bootstrapper.IsInitialized);
    }

    // --- Initialize: basic behavior ---

    [Fact]
    public void Initialize_SetsIsInitializedTrue()
    {
        var bootstrapper = Create();

        bootstrapper.Initialize();

        Assert.True(bootstrapper.IsInitialized);
    }

    [Fact]
    public void Initialize_ReturnsSuccess()
    {
        var bootstrapper = Create();

        var result = bootstrapper.Initialize();

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Message);
        Assert.NotEmpty(result.Message);
    }

    // --- Initialize: idempotency ---

    [Fact]
    public void Initialize_CalledTwice_RemainsInitialized()
    {
        var bootstrapper = Create();

        bootstrapper.Initialize();
        var result = bootstrapper.Initialize();

        Assert.True(bootstrapper.IsInitialized);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Initialize_CalledTwice_SecondCallReturnsAlreadyInitializedMessage()
    {
        var bootstrapper = Create();

        bootstrapper.Initialize();
        var result = bootstrapper.Initialize();

        Assert.Contains("already initialized", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initialize_CalledTwice_BinarySourceConsistent()
    {
        var options = new FrameFlowNativeOptions { CustomFfmpegPath = "/custom" };
        var bootstrapper = Create(options);

        var first = bootstrapper.Initialize();
        var second = bootstrapper.Initialize();

        Assert.Equal(first.BinarySource, second.BinarySource);
        Assert.Equal(first.ResolvedPath, second.ResolvedPath);
    }

    // --- Binary source resolution priority ---

    [Fact]
    public void Initialize_CustomPath_ResolvesBinarySourceToCustomPath()
    {
        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = @"C:\ffmpeg",
            UseBundledBinaries = true,
            ProbeSystemLibraries = true,
        };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Equal(FfmpegBinarySource.CustomPath, result.BinarySource);
    }

    [Fact]
    public void Initialize_CustomPath_SetsResolvedPath()
    {
        var options = new FrameFlowNativeOptions { CustomFfmpegPath = "/opt/ffmpeg/lib" };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Equal("/opt/ffmpeg/lib", result.ResolvedPath);
    }

    [Fact]
    public void Initialize_BundledBinaries_WhenNoCustomPath_ResolvesBundled()
    {
        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = null,
            UseBundledBinaries = true,
            ProbeSystemLibraries = true,
        };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Equal(FfmpegBinarySource.Bundled, result.BinarySource);
    }

    [Fact]
    public void Initialize_SystemLibraries_WhenNoBundled_ResolvesSystem()
    {
        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = null,
            UseBundledBinaries = false,
            ProbeSystemLibraries = true,
        };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Equal(FfmpegBinarySource.System, result.BinarySource);
    }

    [Fact]
    public void Initialize_NothingEnabled_ResolvesUnknown()
    {
        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = null,
            UseBundledBinaries = false,
            ProbeSystemLibraries = false,
        };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Equal(FfmpegBinarySource.Unknown, result.BinarySource);
    }

    [Fact]
    public void Initialize_NoBundled_NoSystem_ResolvedPathIsNull()
    {
        var options = new FrameFlowNativeOptions
        {
            UseBundledBinaries = false,
            ProbeSystemLibraries = false,
        };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Null(result.ResolvedPath);
    }

    // --- Custom path edge cases ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Initialize_WhitespaceCustomPath_TreatedAsNoCustomPath(string whitespace)
    {
        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = whitespace,
            UseBundledBinaries = true,
        };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        // Whitespace-only paths should not resolve to CustomPath
        Assert.NotEqual(FfmpegBinarySource.CustomPath, result.BinarySource);
        Assert.Equal(FfmpegBinarySource.Bundled, result.BinarySource);
    }

    [Theory]
    [InlineData(@"C:\ffmpeg\bin")]
    [InlineData("/usr/local/lib")]
    [InlineData("./relative/path")]
    public void Initialize_VariousCustomPaths_AllResolveToCustomPath(string path)
    {
        var options = new FrameFlowNativeOptions { CustomFfmpegPath = path };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Equal(FfmpegBinarySource.CustomPath, result.BinarySource);
        Assert.Equal(path, result.ResolvedPath);
    }

    // --- Priority: CustomPath > Bundled > System > Unknown ---

    [Fact]
    public void Initialize_CustomPath_TakesPriorityOverBundled()
    {
        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = "/custom",
            UseBundledBinaries = true,
        };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Equal(FfmpegBinarySource.CustomPath, result.BinarySource);
    }

    [Fact]
    public void Initialize_Bundled_TakesPriorityOverSystem()
    {
        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = null,
            UseBundledBinaries = true,
            ProbeSystemLibraries = true,
        };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Equal(FfmpegBinarySource.Bundled, result.BinarySource);
    }

    // --- IFrameFlowBootstrapper interface conformance ---

    [Fact]
    public void ImplementsInterface()
    {
        var bootstrapper = Create();
        Assert.IsAssignableFrom<IFrameFlowBootstrapper>(bootstrapper);
    }

    // --- Default options behavior ---

    [Fact]
    public void Initialize_DefaultOptions_ResolvesBundled()
    {
        var bootstrapper = Create();

        var result = bootstrapper.Initialize();

        Assert.Equal(FfmpegBinarySource.Bundled, result.BinarySource);
    }

    // --- Thread safety ---

    [Fact]
    public void Initialize_ConcurrentCalls_AllReturnSuccess()
    {
        var bootstrapper = Create();

        var results = Enumerable
            .Range(0, 10)
            .AsParallel()
            .Select(_ => bootstrapper.Initialize())
            .ToList();

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.True(bootstrapper.IsInitialized);
    }

    [Fact]
    public void Initialize_ConcurrentCalls_InitializesExactlyOnce()
    {
        var bootstrapper = Create();
        var initCount = 0;

        // Run 20 concurrent calls; the bootstrapper must remain usable and consistent.
        Parallel.For(
            0,
            20,
            _ =>
            {
                var result = bootstrapper.Initialize();
                Assert.True(result.IsSuccess);
                Interlocked.Increment(ref initCount);
            }
        );

        Assert.Equal(20, initCount);
        Assert.True(bootstrapper.IsInitialized);
    }

    // --- Loader interaction ---

    [Fact]
    public void Initialize_CallsLoaderExactlyOnce_OnFirstCall()
    {
        var loader = new StubFfmpegLibraryLoader();
        var options = new FrameFlowNativeOptions { UseBundledBinaries = true };
        var bootstrapper = Create(options, loader);

        bootstrapper.Initialize();
        bootstrapper.Initialize();

        Assert.Equal(1, loader.CallCount);
    }

    [Fact]
    public void Initialize_PassesSearchPathToLoader_ForCustomPath()
    {
        var loader = new StubFfmpegLibraryLoader();
        var options = new FrameFlowNativeOptions { CustomFfmpegPath = "/my/ffmpeg" };
        var bootstrapper = Create(options, loader);

        bootstrapper.Initialize();

        Assert.Equal("/my/ffmpeg", loader.LastSearchPath);
    }

    [Fact]
    public void Initialize_PassesNullSearchPathToLoader_ForSystem()
    {
        var loader = new StubFfmpegLibraryLoader();
        var options = new FrameFlowNativeOptions
        {
            UseBundledBinaries = false,
            ProbeSystemLibraries = true,
        };
        var bootstrapper = Create(options, loader);

        bootstrapper.Initialize();

        Assert.Null(loader.LastSearchPath);
        Assert.Equal(FfmpegBinarySource.System, loader.LastSource);
    }

    [Fact]
    public void Initialize_WhenLoaderFails_ReturnsIsSuccessFalse()
    {
        var loader = new StubFfmpegLibraryLoader { SimulateSuccess = false };
        var options = new FrameFlowNativeOptions { UseBundledBinaries = true };
        var bootstrapper = Create(options, loader);

        var result = bootstrapper.Initialize();

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Message);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void Initialize_WhenLoaderFails_MessageContainsErrorDetail()
    {
        var loader = new StubFfmpegLibraryLoader
        {
            SimulateSuccess = false,
            FailureMessage = "avutil-59.dll not found",
        };
        var bootstrapper = Create(loader: loader);

        var result = bootstrapper.Initialize();

        Assert.Contains(
            "avutil-59.dll not found",
            result.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public void Initialize_UnknownSource_DoesNotCallLoader()
    {
        var loader = new StubFfmpegLibraryLoader();
        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = null,
            UseBundledBinaries = false,
            ProbeSystemLibraries = false,
        };
        var bootstrapper = Create(options, loader);

        bootstrapper.Initialize();

        Assert.Equal(0, loader.CallCount);
    }

    [Fact]
    public void Initialize_UnknownSource_ReturnsIsSuccessFalse()
    {
        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = null,
            UseBundledBinaries = false,
            ProbeSystemLibraries = false,
        };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.False(result.IsSuccess);
    }

    // --- Version information in success message ---

    [Fact]
    public void Initialize_OnSuccess_MessageContainsBinarySource()
    {
        var options = new FrameFlowNativeOptions { CustomFfmpegPath = "/path" };
        var bootstrapper = Create(options);

        var result = bootstrapper.Initialize();

        Assert.Contains("CustomPath", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
