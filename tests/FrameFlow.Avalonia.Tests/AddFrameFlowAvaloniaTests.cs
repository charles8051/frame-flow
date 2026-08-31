using FrameFlow;
using FrameFlow.Avalonia;
using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Tests for <see cref="FrameFlowAvaloniaServiceCollectionExtensions"/>.
/// </summary>
public sealed class AddFrameFlowAvaloniaTests
{
    /// <summary>Registers a null logger factory so DI can resolve ILogger{T}.</summary>
    private static void AddNullLogging(IServiceCollection services)
    {
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    // -----------------------------------------------------------------------
    // AddFrameFlowAvaloniaVideoSink — guard clauses
    // -----------------------------------------------------------------------

    [Fact]
    public void AddFrameFlowAvaloniaVideoSink_NullBuilder_Throws()
    {
        IFrameFlowBuilder? builder = null;
        Assert.Throws<ArgumentNullException>(() => builder!.AddFrameFlowAvaloniaVideoSink());
    }

    // -----------------------------------------------------------------------
    // AddFrameFlowAvaloniaVideoSink — service registration
    // -----------------------------------------------------------------------

    [Fact]
    public void AddFrameFlowAvaloniaVideoSink_RegistersIVideoSink()
    {
        var services = new ServiceCollection();
        AddNullLogging(services);
        services.AddFrameFlow().AddFrameFlowAvaloniaVideoSink();

        var provider = services.BuildServiceProvider();
        var sink = provider.GetService<IVideoSink>();

        Assert.NotNull(sink);
    }

    [Fact]
    public void AddFrameFlowAvaloniaVideoSink_RegisteredSink_IsAvaloniaVideoSink()
    {
        var services = new ServiceCollection();
        AddNullLogging(services);
        services.AddFrameFlow().AddFrameFlowAvaloniaVideoSink();

        var provider = services.BuildServiceProvider();
        var sink = provider.GetRequiredService<IVideoSink>();

        Assert.IsType<AvaloniaVideoSink>(sink);
    }

    [Fact]
    public void AddFrameFlowAvaloniaVideoSink_IVideoSink_IsSingleton()
    {
        var services = new ServiceCollection();
        AddNullLogging(services);
        services.AddFrameFlow().AddFrameFlowAvaloniaVideoSink();

        var provider = services.BuildServiceProvider();
        var sink1 = provider.GetRequiredService<IVideoSink>();
        var sink2 = provider.GetRequiredService<IVideoSink>();

        Assert.Same(sink1, sink2);
    }

    [Fact]
    public void AddFrameFlowAvaloniaVideoSink_ReturnsBuilder_ForContinuedChaining()
    {
        var services = new ServiceCollection();
        var builder = services.AddFrameFlow();
        var result = builder.AddFrameFlowAvaloniaVideoSink();

        Assert.Same(builder, result);
    }

    [Fact]
    public void AddFrameFlowAvaloniaVideoSink_RegistersIFramePool()
    {
        var services = new ServiceCollection();
        AddNullLogging(services);
        services.AddFrameFlow().AddFrameFlowAvaloniaVideoSink();

        var provider = services.BuildServiceProvider();
        var pool = provider.GetService<IFramePool>();

        Assert.NotNull(pool);
    }

    [Fact]
    public void AddFrameFlowAvaloniaVideoSink_CalledTwice_RegistrationIsIdempotent()
    {
        // Parity with AddFrameFlowOpenAlTests — TryAddSingleton means a
        // second call is a no-op rather than a double registration.
        var services = new ServiceCollection();
        AddNullLogging(services);
        services.AddFrameFlow().AddFrameFlowAvaloniaVideoSink().AddFrameFlowAvaloniaVideoSink();

        var provider = services.BuildServiceProvider();
        var sink = provider.GetService<IVideoSink>();

        Assert.NotNull(sink);
    }

    [Fact]
    public void AddFrameFlowAvaloniaVideoSink_ConsumerRegisteredSinkFirst_IsNotOverridden()
    {
        // Parity with AddFrameFlowOpenAlTests — a consumer's own IVideoSink
        // registration takes precedence over FrameFlow's TryAdd.
        var services = new ServiceCollection();
        AddNullLogging(services);
        var customSink = new CustomFakeVideoSink();
        services.AddSingleton<IVideoSink>(customSink);

        services.AddFrameFlow().AddFrameFlowAvaloniaVideoSink();

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IVideoSink>();

        Assert.Same(customSink, resolved);
    }

    // -----------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------

    private sealed class CustomFakeVideoSink : IVideoSink
    {
        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public IFramePool FramePool => null!;

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
