// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback;

namespace FrameFlow.ColdStart.Tests;

/// <summary>
/// Asserts that the public surfaces below <c>FrameFlow.Player</c> work in a process that has
/// never called <c>FrameFlowBootstrapper.Initialize()</c>. Issues #124 and #55, ADR-0070.
/// </summary>
/// <remarks>
/// <para>
/// This is why the project exists. Every other test project bootstraps FFmpeg from a collection
/// fixture, so by the time any test in it runs, the resolver is installed and the libraries are
/// loaded — a cold-start regression is invisible there no matter where the assertion is placed.
/// Here nothing bootstraps, so the only thing that can make these pass is the module initializer
/// in <c>FrameFlow.Native</c>.
/// </para>
/// <para>
/// Keep this project free of a bootstrap call, and of a reference to any project that makes one.
/// </para>
/// </remarks>
public sealed class ColdStartTests
{
    private const string CorpusFile = "test-av-h264-aac.mp4";

    /// <summary>
    /// The resolver is registered as <c>FrameFlow.Native</c> loads, not when something
    /// bootstraps. <see cref="NativeLibrary.SetDllImportResolver"/> permits one resolver per
    /// assembly and throws on the second, so a throw here is the observation that one is
    /// already in place. Nothing is registered when it throws.
    /// </summary>
    [Fact]
    public void Resolver_IsRegistered_WithoutAnyBootstrapCall()
    {
        // Calling a method in FrameFlow.Native runs that module's initializer. A constructor
        // is used rather than a typeof(): the guarantee attaches to invoking a method or
        // reading a static field, not to obtaining a type handle.
        var nativeAssembly = new FrameFlowNativeOptions().GetType().Assembly;
        Assert.Equal("FrameFlow.Native", nativeAssembly.GetName().Name);

        Assert.Throws<InvalidOperationException>(
            () => NativeLibrary.SetDllImportResolver(nativeAssembly, (_, _, _) => nint.Zero)
        );
    }

    /// <summary>
    /// Issue #124: the repro from the report, which threw <see cref="DllNotFoundException"/>
    /// on <c>avformat_open_input</c> before the resolver moved to a module initializer.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task DemuxSessionFactory_OpensFile_WithoutAnyBootstrapCall()
    {
        var path = ColdStartEnvironment.GetCorpusFile(CorpusFile);
        Assert.NotNull(path);

        var factory = new DemuxSessionFactory();
        await using var demux = await factory.OpenAsync(MediaSource.FromFile(path));

        Assert.NotEmpty(demux.MediaInfo.VideoStreams);
    }

    /// <summary>
    /// Issue #55: the same defect one layer up. <c>PlaybackController.Create</c> never
    /// bootstraps, unlike <c>MediaPlayer.CreateAsync</c>.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task PlaybackController_LoadsFile_WithoutAnyBootstrapCall()
    {
        var path = ColdStartEnvironment.GetCorpusFile(CorpusFile);
        Assert.NotNull(path);

        await using var controller = PlaybackController.Create();
        var result = await controller.LoadAsync(MediaSource.FromFile(path));

        Assert.True(result.IsSuccess, result.Error?.Message);
    }
}
