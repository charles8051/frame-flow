// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
using FrameFlow.Media;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins ADR-0085 decision 6 on the player and pass builders against real media: each claims every
/// claimable sink it is given, video and audio, before it builds anything; a second build against a
/// held sink fails naming the sink and its holder; a build that fails or is cancelled releases its
/// claims; and a pass holds its claim until it is disposed, not until its run ends.
/// </summary>
public sealed class SinkClaimTests
{
    private const string VideoFile = "test-video-h264-yuv420p.mp4";
    private const string AudioFile = "test-audio-aac.m4a";

    private static string Corpus(string name) =>
        TestEnvironment.GetCorpusFile(name) ?? throw new InvalidOperationException($"{name} is missing.");

    private static Task<IMediaPlayer> BuildPlayerAsync(
        string file,
        IVideoSink? video = null,
        IAudioSink? audio = null,
        CancellationToken ct = default
    )
    {
        var builder = FrameFlowPlayer.Create().WithMedia(file).WithHardwareDecode(HardwareDecodeMode.Disabled);
        if (video is not null)
            builder = builder.WithVideoSink(video);
        if (audio is not null)
            builder = builder.WithAudioSink(audio);
        return builder.BuildPlayerAsync(ct);
    }

    private static Task<MediaPass> BuildPassAsync(
        string file,
        IVideoSink? video = null,
        IAudioSink? audio = null,
        CancellationToken ct = default
    )
    {
        var builder = FrameFlowPass.Create(file).WithHardwareDecode(HardwareDecodeMode.Disabled);
        if (video is not null)
            builder = builder.WithVideoSink(video);
        if (audio is not null)
            builder = builder.WithAudioSink(audio);
        return builder.BuildAsync(ct);
    }

    private static string? Holder(object sink) => ((IClaimableSink)sink).Claimant;

    // ─── The player ─────────────────────────────────────────────────

    [RequiresFfmpegAndCorpusFact]
    public async Task ASecondPlayerOverAHeldVideoSink_Fails_NamingTheSinkAndItsHolder()
    {
        await using var sink = new HeadlessVideoSink();
        await using var first = await BuildPlayerAsync(Corpus(VideoFile), video: sink);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildPlayerAsync(Corpus(VideoFile), video: sink)
        );

        Assert.Contains("video sink HeadlessVideoSink", error.Message, StringComparison.Ordinal);
        Assert.Contains($"held by a player built for '{VideoFile}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("await DisposeAsync", error.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlayerBuiltAfterTheFirstsAwaitedDisposal_ClaimsTheSink()
    {
        await using var sink = new HeadlessVideoSink();
        var first = await BuildPlayerAsync(Corpus(VideoFile), video: sink);

        await first.DisposeAsync();
        Assert.Null(Holder(sink));

        await using var second = await BuildPlayerAsync(Corpus(VideoFile), video: sink);
        Assert.Equal($"a player built for '{VideoFile}'", Holder(sink));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APlayerBuildThatFailsToLoad_ReleasesItsClaims()
    {
        await using var video = new HeadlessVideoSink();
        await using var audio = new ClaimableAudioSink();
        var missing = Path.Combine(TestEnvironment.CorpusDir, "no-such-file.mp4");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildPlayerAsync(missing, video: video, audio: audio)
        );

        Assert.Null(Holder(video));
        Assert.Null(Holder(audio));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ACancelledPlayerBuild_ReleasesItsClaims()
    {
        await using var video = new HeadlessVideoSink();
        await using var audio = new ClaimableAudioSink();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildPlayerAsync(Corpus(VideoFile), video: video, audio: audio, ct: cancelled.Token)
        );

        Assert.Null(Holder(video));
        Assert.Null(Holder(audio));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASecondPlayerOverAHeldAudioSink_Fails_NamingTheSinkAndItsHolder()
    {
        await using var audio = new ClaimableAudioSink();
        await using var first = await BuildPlayerAsync(Corpus(AudioFile), audio: audio);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildPlayerAsync(Corpus(AudioFile), audio: audio)
        );

        Assert.Contains("audio sink ClaimableAudioSink", error.Message, StringComparison.Ordinal);
        Assert.Contains($"held by a player built for '{AudioFile}'", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, audio.Activations); // the second build activated nothing
    }

    // ─── The pass ───────────────────────────────────────────────────

    [RequiresFfmpegAndCorpusFact]
    public async Task APassOverASinkAPlayerHolds_Fails_NamingThePlayer()
    {
        await using var sink = new HeadlessVideoSink();
        await using var player = await BuildPlayerAsync(Corpus(VideoFile), video: sink);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildPassAsync(Corpus(VideoFile), video: sink)
        );

        Assert.Contains($"held by a player built for '{VideoFile}'", error.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APassHoldsItsClaimAfterItsRunEnds_UntilItIsDisposed()
    {
        await using var sink = new HeadlessVideoSink();
        var pass = await BuildPassAsync(Corpus(VideoFile), video: sink);
        await pass.RunToCompletionAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildPassAsync(Corpus(VideoFile), video: sink)
        );
        Assert.Contains($"held by a pass over '{VideoFile}'", error.Message, StringComparison.Ordinal);

        await pass.DisposeAsync();

        Assert.Null(Holder(sink));
        await using var next = await BuildPassAsync(Corpus(VideoFile), video: sink);
        Assert.Equal($"a pass over '{VideoFile}'", Holder(sink));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASecondPassOverAHeldAudioSink_Fails()
    {
        await using var audio = new ClaimableAudioSink();
        await using var first = await BuildPassAsync(Corpus(AudioFile), audio: audio);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildPassAsync(Corpus(AudioFile), audio: audio)
        );

        Assert.Contains("audio sink ClaimableAudioSink", error.Message, StringComparison.Ordinal);
        Assert.Contains($"held by a pass over '{AudioFile}'", error.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APassBuildThatFails_ReleasesItsClaims()
    {
        await using var video = new HeadlessVideoSink();
        await using var audio = new ClaimableAudioSink();
        var missing = Path.Combine(TestEnvironment.CorpusDir, "no-such-file.mp4");

        await Assert.ThrowsAnyAsync<Exception>(() => BuildPassAsync(missing, video: video, audio: audio));

        Assert.Null(Holder(video));
        Assert.Null(Holder(audio));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APassDisposedDuringItsRun_KeepsItsClaimUntilTheRunEnds()
    {
        // The pass's dispose does not wait for a run in flight, and what that run still sends would
        // reach a sink the next pass had claimed. The run is held inside the audio sink's
        // activation, which comes before the graph starts, so the dispose frees nothing the run
        // is using; the activation then fails, which ends the run without starting the graph.
        await using var audio = new ClaimableAudioSink { HoldActivation = true };
        var pass = await BuildPassAsync(Corpus(AudioFile), audio: audio);
        var run = pass.RunToCompletionAsync();
        await audio.ActivationEntered.Task;

        await pass.DisposeAsync();

        Assert.Equal($"a pass over '{AudioFile}'", Holder(audio));
        audio.ActivationGate.SetException(new InvalidOperationException("the device went away"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Null(Holder(audio));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ACancelledPassBuild_ReleasesItsClaims()
    {
        await using var video = new HeadlessVideoSink();
        await using var audio = new ClaimableAudioSink();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildPassAsync(Corpus(VideoFile), video: video, audio: audio, ct: cancelled.Token)
        );

        Assert.Null(Holder(video));
        Assert.Null(Holder(audio));
    }

    // ─── Doubles ────────────────────────────────────────────────────

    /// <summary>An audio sink that claims through a <see cref="SinkClaimGate"/>, as the in-box ones do.</summary>
    private sealed class ClaimableAudioSink : IAudioSink, IClaimableSink
    {
        private readonly SinkClaimGate _claims = new(typeof(ClaimableAudioSink));
        private int _activations;

        public int Activations => Volatile.Read(ref _activations);

        /// <summary>
        /// When set, <see cref="ActivateAsync"/> completes <see cref="ActivationEntered"/> and then
        /// waits for <see cref="ActivationGate"/>.
        /// </summary>
        public bool HoldActivation { get; init; }

        public TaskCompletionSource ActivationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ActivationGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? TryClaim(string claimant) => _claims.TryClaim(claimant);

        public string? Claimant => _claims.Claimant;

        public long ClaimOrder => _claims.ClaimOrder;

        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _activations);
            if (!HoldActivation)
                return ValueTask.CompletedTask;
            ActivationEntered.TrySetResult();
            return new ValueTask(ActivationGate.Task);
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _claims.Close();
            return ValueTask.CompletedTask;
        }
    }
}
