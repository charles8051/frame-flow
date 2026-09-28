using System.Collections.Concurrent;
using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using FrameFlow.Player;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>Runs alone: its assertions read the process-wide copy counts exactly.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FrameCopyCountsCollection
{
    public const string Name = "Frame copy counts (#435)";
}

/// <summary>
/// A player decides where hardware frames go when it is not told (#294): on the GPU for a sink
/// that takes them, downloaded for one that has not said it does. Its own gate and pacer sit on
/// the path, so this is the player's decision and not only the pass's. The copy counts show which
/// path the frames took (#435).
/// </summary>
[Collection(FrameCopyCountsCollection.Name)]
public sealed class PlayerHardwareFramesTests
{
    private const string Clip = "test-video-h264-yuv420p.mp4";

    // Bounds a run that never delivers, and nothing else: each test waits on its sink's signal.
    private static readonly TimeSpan Failure = TimeSpan.FromSeconds(30);

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task APlayerUnasked_KeepsFramesOnTheGpu_ForASinkThatTakesThem()
    {
        var sink = new FirstFramesSink(FrameMemoryDomains.Any);
        var before = FrameCopyMetrics.Snapshot();
        await PlayAsync(sink);

        Assert.All(sink.Domains, domain => Assert.Equal(FrameMemoryDomain.Gpu, domain));
        var made = FrameCopyMetrics.Snapshot().Since(before);
        Assert.Equal(0, made[FrameCopySite.DecoderDownload]);
        Assert.Equal(0, made[FrameCopySite.DecoderConvert]);
    }

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task APlayerUnasked_DownloadsFrames_ForASinkThatSaysNothing()
    {
        var sink = new FirstFramesSink(accepts: null);
        var before = FrameCopyMetrics.Snapshot();
        await PlayAsync(sink);

        Assert.All(sink.Domains, domain => Assert.Equal(FrameMemoryDomain.Cpu, domain));
        var made = FrameCopyMetrics.Snapshot().Since(before);
        Assert.True(made[FrameCopySite.DecoderDownload] >= sink.Domains.Count, made.ToString());
        Assert.True(made[FrameCopySite.DecoderConvert] >= sink.Domains.Count, made.ToString());
    }

    /// <summary>Plays the clip on a D3D12VA device, with no <c>WithHardwareFrames</c>, until the sink has three frames.</summary>
    private static async Task PlayAsync(FirstFramesSink sink)
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        await using var player = await FrameFlowPlayer
            .Create()
            .WithMedia(TestEnvironment.CorpusFile(Clip)!)
            .WithHardwareDevice(device)
            .WithVideoSink(sink)
            .BuildPlayerAsync();

        var played = await player.PlayAsync();
        Assert.True(played.IsSuccess, played.IsSuccess ? null : played.Error.Message);
        await sink.Received.WaitAsync(Failure);
    }

    /// <summary>
    /// Records the memory domain of the first three frames and then signals. Declares
    /// <paramref name="accepts"/>, or nothing when it is null.
    /// </summary>
    private sealed class FirstFramesSink(FrameMemoryDomains? accepts) : IVideoSink
    {
        private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<FrameMemoryDomain> Domains { get; } = new();

        public Task Received => _received.Task;

        public int? MaxHeldFrames => 0;

        public FrameMemoryDomains AcceptedDomains => accepts ?? ((IVideoSink)new Undeclared()).AcceptedDomains;

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            if (Domains.Count < 3)
                Domains.Enqueue(frame.MemoryDomain);
            if (Domains.Count >= 3)
                _received.TrySetResult();
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A sink that declares nothing, for the interface's default.</summary>
    private sealed class Undeclared : IVideoSink
    {
        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
