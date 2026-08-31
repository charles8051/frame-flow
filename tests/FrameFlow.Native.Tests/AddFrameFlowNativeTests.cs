using FrameFlow;
using FrameFlow.Native;
using FrameFlow.Native.Tests.Doubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Tests for <see cref="FrameFlowNativeServiceCollectionExtensions"/>.
/// </summary>
public sealed class AddFrameFlowNativeTests
{
    /// <summary>
    /// Creates a bootstrapper backed by a stub loader so hosted service tests
    /// do not require real FFmpeg binaries.
    /// </summary>
    private static FrameFlowBootstrapper CreateStubBootstrapper(
        FrameFlowNativeOptions? options = null,
        StubFfmpegLibraryLoader? loader = null
    ) =>
        new(
            // SkipHardwareProbe defaults to true here because the stub
            // loader doesn't actually put avutil on the OS DLL search
            // path — it just reports "FFmpeg loaded fine" to the
            // bootstrapper. Without the skip, Initialize() unconditionally
            // calls HardwareDecodeProbe.Run() which P/Invokes
            // av_hwdevice_iterate_types and throws DllNotFoundException
            // unless an earlier test in the same process happens to have
            // loaded the real DLLs (which under xUnit's parallel test
            // scheduling, isn't guaranteed). This was the source of the
            // intermittent "parallel-load DLL flake" we'd been blaming
            // on environment — actually a test-ordering bug.
            //
            // Tests that DO want to exercise the probe can pass an
            // options object with SkipHardwareProbe = false explicitly.
            options ?? new FrameFlowNativeOptions { SkipHardwareProbe = true },
            NullLogger<FrameFlowBootstrapper>.Instance,
            loader ?? new StubFfmpegLibraryLoader()
        );

    // -----------------------------------------------------------------------
    // AddFrameFlowNative — guard clauses
    // -----------------------------------------------------------------------

    [Fact]
    public void AddFrameFlowNative_NullServices_Throws()
    {
        IServiceCollection? services = null;
        Assert.Throws<ArgumentNullException>(() => services!.AddFrameFlowNative());
    }

    // -----------------------------------------------------------------------
    // AddFrameFlowNative — options registration
    // -----------------------------------------------------------------------

    [Fact]
    public void AddFrameFlowNative_WithoutConfigure_RegistersFrameFlowNativeOptionsWithDefaults()
    {
        var services = new ServiceCollection();
        services.AddFrameFlowNative();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FrameFlowNativeOptions>>().Value;

        Assert.NotNull(options);
    }

    [Fact]
    public void AddFrameFlowNative_WithoutConfigure_UseBundledBinaries_IsTrue()
    {
        var services = new ServiceCollection();
        services.AddFrameFlowNative();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FrameFlowNativeOptions>>().Value;

        Assert.True(options.UseBundledBinaries);
    }

    [Fact]
    public void AddFrameFlowNative_WithoutConfigure_ProbeSystemLibraries_IsTrue()
    {
        var services = new ServiceCollection();
        services.AddFrameFlowNative();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FrameFlowNativeOptions>>().Value;

        Assert.True(options.ProbeSystemLibraries);
    }

    [Fact]
    public void AddFrameFlowNative_WithoutConfigure_CustomFfmpegPath_IsNull()
    {
        var services = new ServiceCollection();
        services.AddFrameFlowNative();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FrameFlowNativeOptions>>().Value;

        Assert.Null(options.CustomFfmpegPath);
    }

    [Fact]
    public void AddFrameFlowNative_WithConfigure_MutationIsApplied()
    {
        var services = new ServiceCollection();
        services.AddFrameFlowNative(o => o.CustomFfmpegPath = "/opt/ffmpeg");

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FrameFlowNativeOptions>>().Value;

        Assert.Equal("/opt/ffmpeg", options.CustomFfmpegPath);
    }

    // -----------------------------------------------------------------------
    // AddFrameFlowNative — IFrameFlowBootstrapper registration
    // -----------------------------------------------------------------------

    [Fact]
    public void AddFrameFlowNative_RegistersIFrameFlowBootstrapper()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFrameFlowNative();

        var provider = services.BuildServiceProvider();
        var bootstrapper = provider.GetService<IFrameFlowBootstrapper>();

        Assert.NotNull(bootstrapper);
    }

    [Fact]
    public void AddFrameFlowNative_IFrameFlowBootstrapper_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFrameFlowNative();

        var provider = services.BuildServiceProvider();
        var b1 = provider.GetRequiredService<IFrameFlowBootstrapper>();
        var b2 = provider.GetRequiredService<IFrameFlowBootstrapper>();

        Assert.Same(b1, b2);
    }

    [Fact]
    public void AddFrameFlowNative_RegisteredBootstrapper_IsFrameFlowBootstrapper()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFrameFlowNative();

        var provider = services.BuildServiceProvider();
        var bootstrapper = provider.GetRequiredService<IFrameFlowBootstrapper>();

        Assert.IsType<FrameFlowBootstrapper>(bootstrapper);
    }

    [Fact]
    public void AddFrameFlowNative_CalledTwice_BootstrapperRegistrationIsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFrameFlowNative();
        services.AddFrameFlowNative();

        var provider = services.BuildServiceProvider();
        // Should not throw — TryAdd semantics prevent duplicate registration.
        var bootstrapper = provider.GetService<IFrameFlowBootstrapper>();
        Assert.NotNull(bootstrapper);
    }

    // -----------------------------------------------------------------------
    // AddHostedBootstrap — guard clauses
    // -----------------------------------------------------------------------

    [Fact]
    public void AddHostedBootstrap_NullBuilder_Throws()
    {
        IFrameFlowBuilder? builder = null;
        Assert.Throws<ArgumentNullException>(() => builder!.AddHostedBootstrap());
    }

    // -----------------------------------------------------------------------
    // AddHostedBootstrap — registration
    // -----------------------------------------------------------------------

    [Fact]
    public void AddHostedBootstrap_AlsoRegistersIFrameFlowBootstrapper()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFrameFlow().AddHostedBootstrap();

        var provider = services.BuildServiceProvider();
        var bootstrapper = provider.GetService<IFrameFlowBootstrapper>();

        Assert.NotNull(bootstrapper);
    }

    [Fact]
    public void AddHostedBootstrap_ReturnsBuilder_ForContinuedChaining()
    {
        var services = new ServiceCollection();
        var builder = services.AddFrameFlow();
        var result = builder.AddHostedBootstrap();

        Assert.Same(builder, result);
    }

    // -----------------------------------------------------------------------
    // FrameFlowHostedService — construction
    // -----------------------------------------------------------------------

    [Fact]
    public void FrameFlowHostedService_NullBootstrapper_Throws()
    {
        var logger = NullLogger<FrameFlowHostedService>.Instance;
        Assert.Throws<ArgumentNullException>(() => new FrameFlowHostedService(null!, logger));
    }

    [Fact]
    public void FrameFlowHostedService_NullLogger_Throws()
    {
        var bootstrapper = CreateStubBootstrapper();
        Assert.Throws<ArgumentNullException>(() => new FrameFlowHostedService(bootstrapper, null!));
    }

    // -----------------------------------------------------------------------
    // FrameFlowHostedService — StartAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_SuccessfulBootstrap_CompletesWithoutException()
    {
        var bootstrapper = CreateStubBootstrapper();
        var logger = NullLogger<FrameFlowHostedService>.Instance;
        var service = new FrameFlowHostedService(bootstrapper, logger);

        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_SetsBootstrapperInitialized()
    {
        var bootstrapper = CreateStubBootstrapper();
        var logger = NullLogger<FrameFlowHostedService>.Instance;
        var service = new FrameFlowHostedService(bootstrapper, logger);

        await service.StartAsync(CancellationToken.None);

        Assert.True(bootstrapper.IsInitialized);
    }

    [Fact]
    public async Task StartAsync_CalledTwice_DoesNotThrow()
    {
        // The bootstrapper returns success on repeated calls (already-initialized path).
        var bootstrapper = CreateStubBootstrapper();
        var logger = NullLogger<FrameFlowHostedService>.Instance;
        var service = new FrameFlowHostedService(bootstrapper, logger);

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenBootstrapFails_ThrowsInvalidOperationException()
    {
        var loader = new StubFfmpegLibraryLoader { SimulateSuccess = false };
        var bootstrapper = CreateStubBootstrapper(loader: loader);
        var logger = NullLogger<FrameFlowHostedService>.Instance;
        var service = new FrameFlowHostedService(bootstrapper, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(CancellationToken.None)
        );
    }

    // -----------------------------------------------------------------------
    // FrameFlowHostedService — StopAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_Always_CompletesWithoutException()
    {
        var bootstrapper = CreateStubBootstrapper();
        var logger = NullLogger<FrameFlowHostedService>.Instance;
        var service = new FrameFlowHostedService(bootstrapper, logger);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_WithoutPriorStart_CompletesWithoutException()
    {
        var bootstrapper = CreateStubBootstrapper();
        var logger = NullLogger<FrameFlowHostedService>.Instance;
        var service = new FrameFlowHostedService(bootstrapper, logger);

        // StopAsync before StartAsync should not throw.
        await service.StopAsync(CancellationToken.None);
    }
}
