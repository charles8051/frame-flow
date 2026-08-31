using FrameFlow;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using Microsoft.Extensions.DependencyInjection;
using FrameFlow.Graph;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Tests for <see cref="FrameFlowOpenAlServiceCollectionExtensions"/>.
/// </summary>
/// <remarks>
/// Per ADR-0044, the playback layer consumes <see cref="IAudioSink"/>
/// directly as a DI singleton; the prior <see cref="Func{IAudioSink}"/>
/// factory pattern is retired. These tests assert the new registration
/// shape.
/// </remarks>
public sealed class AddFrameFlowOpenAlTests : IClassFixture<FfmpegBootstrapFixture>
{
    // -----------------------------------------------------------------------
    // AddFrameFlowOpenAlAudio — guard clauses
    // -----------------------------------------------------------------------

    [Fact]
    public void AddFrameFlowOpenAlAudio_NullBuilder_Throws()
    {
        IFrameFlowBuilder? builder = null;
        Assert.Throws<ArgumentNullException>(() => builder!.AddFrameFlowOpenAlAudio());
    }

    // -----------------------------------------------------------------------
    // AddFrameFlowOpenAlAudio — service registration
    // -----------------------------------------------------------------------

    [Fact]
    public void AddFrameFlowOpenAlAudio_RegistersAudioSinkSingleton()
    {
        var services = new ServiceCollection();
        services.AddFrameFlow().AddFrameFlowOpenAlAudio();

        var provider = services.BuildServiceProvider();
        var sink = provider.GetService<IAudioSink>();

        Assert.NotNull(sink);
        Assert.IsType<OpenAlAudioSink>(sink);
    }

    [Fact]
    public void AddFrameFlowOpenAlAudio_SingletonReturnsSameInstance()
    {
        // The DI provider owns the sink's lifetime — resolving it more than
        // once returns the same instance (per ADR-0044 single-owner model).
        var services = new ServiceCollection();
        services.AddFrameFlow().AddFrameFlowOpenAlAudio();

        var provider = services.BuildServiceProvider();
        var sink1 = provider.GetRequiredService<IAudioSink>();
        var sink2 = provider.GetRequiredService<IAudioSink>();

        Assert.Same(sink1, sink2);
    }

    [Fact]
    public void AddFrameFlowOpenAlAudio_ReturnsBuilder_ForContinuedChaining()
    {
        var services = new ServiceCollection();
        var builder = services.AddFrameFlow();
        var result = builder.AddFrameFlowOpenAlAudio();

        Assert.Same(builder, result);
    }

    [Fact]
    public void AddFrameFlowOpenAlAudio_CalledTwice_RegistrationIsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddFrameFlow().AddFrameFlowOpenAlAudio().AddFrameFlowOpenAlAudio();

        var provider = services.BuildServiceProvider();
        // Should not throw — TryAddSingleton prevents duplicate registration.
        var sink = provider.GetService<IAudioSink>();
        Assert.NotNull(sink);
    }

    [Fact]
    public void AddFrameFlowOpenAlAudio_ConsumerRegisteredSinkFirst_IsNotOverridden()
    {
        // Consumer's registration should take precedence over FrameFlow's TryAdd.
        var services = new ServiceCollection();
        var customSink = new CustomFakeAudioSink();
        services.AddSingleton<IAudioSink>(customSink);

        services.AddFrameFlow().AddFrameFlowOpenAlAudio();

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IAudioSink>();

        Assert.Same(customSink, resolved);
    }

    // -----------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------

    private sealed class CustomFakeAudioSink : IAudioSink
    {

        public bool Muted { get; set; }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask PresentAsync(
            IAudioBuffer frame,
            CancellationToken cancellationToken = default
        )
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public TimeSpan GetPlaybackTime() => TimeSpan.Zero;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
