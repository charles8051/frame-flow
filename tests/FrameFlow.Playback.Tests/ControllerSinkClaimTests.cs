// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Playback.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Pins ADR-0085 decision 6 on the controller factories: <see cref="PlaybackController.Create"/>
/// and <see cref="PlaybackController.CreatePlaylist"/> claim every claimable sink they are given,
/// video and audio, before they build anything; a second build against a held sink fails naming the
/// sink and its holder; and the controller releases its claims only once its dispose has disposed
/// the session. No FFmpeg: nothing is loaded except through a fake session.
/// </summary>
public sealed class ControllerSinkClaimTests
{
    [Fact]
    public async Task ASecondControllerOverAHeldVideoSink_Fails_NamingTheSinkAndItsHolder()
    {
        await using var sink = new HeadlessVideoSink();
        await using var first = PlaybackController.Create(videoSink: sink);

        var error = Assert.Throws<InvalidOperationException>(() =>
            PlaybackController.Create(videoSink: sink)
        );

        Assert.Contains("video sink HeadlessVideoSink", error.Message, StringComparison.Ordinal);
        Assert.Contains("held by a controller from PlaybackController.Create", error.Message, StringComparison.Ordinal);
        Assert.Contains("await DisposeAsync", error.Message, StringComparison.Ordinal);
        Assert.Contains("Stopping it is not enough", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AControllerBuiltAfterTheFirstIsDisposed_ClaimsTheSink()
    {
        await using var sink = new HeadlessVideoSink();
        var first = PlaybackController.Create(videoSink: sink);

        await first.DisposeAsync();

        await using var second = PlaybackController.Create(videoSink: sink);
        Assert.Equal("a controller from PlaybackController.Create", ((IClaimableSink)sink).Claimant);
    }

    [Fact]
    public async Task ASecondControllerOverAHeldAudioSink_Fails_AndLeavesItsVideoSinkUnclaimed()
    {
        // All or none: the free video sink the second build claimed is released when its held
        // audio sink fails the build. The video sink is made first, so it comes first in the claim
        // order and is taken before the audio sink is found held.
        await using var secondVideo = new HeadlessVideoSink();
        await using var audio = new ClaimableAudioSink();
        await using var firstVideo = new HeadlessVideoSink();
        await using var first = PlaybackController.Create(videoSink: firstVideo, audioSink: audio);

        var error = Assert.Throws<InvalidOperationException>(() =>
            PlaybackController.Create(videoSink: secondVideo, audioSink: audio)
        );

        Assert.Contains("audio sink ClaimableAudioSink", error.Message, StringComparison.Ordinal);
        Assert.Null(((IClaimableSink)secondVideo).Claimant);
    }

    [Fact]
    public async Task APlaylistController_NamesItsClaimant()
    {
        await using var sink = new HeadlessVideoSink();
        await using var first = PlaybackController.CreatePlaylist(
            new PlaylistCoordinator(RepeatMode.Off),
            videoSink: sink,
            claimant: "a player built for 'clip.mp4'"
        );

        var error = Assert.Throws<InvalidOperationException>(() =>
            PlaybackController.CreatePlaylist(new PlaylistCoordinator(RepeatMode.Off), videoSink: sink)
        );

        Assert.Contains("held by a player built for 'clip.mp4'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildThatThrowsAfterClaiming_ReleasesItsClaims()
    {
        // The session factory refuses inverted hysteresis after the sinks are claimed.
        await using var video = new HeadlessVideoSink();
        await using var audio = new ClaimableAudioSink();
        var inverted = new LatenessRecoveryOptions
        {
            EscalateAbove = TimeSpan.FromMilliseconds(100),
            RelaxBelow = TimeSpan.FromMilliseconds(400),
        };

        Assert.Throws<ArgumentException>(() =>
            PlaybackController.CreatePlaylist(
                new PlaylistCoordinator(RepeatMode.Off),
                videoSink: video,
                audioSink: audio,
                latenessRecovery: inverted
            )
        );

        Assert.Null(((IClaimableSink)video).Claimant);
        Assert.Null(((IClaimableSink)audio).Claimant);
    }

    [Fact]
    public async Task ADisposedSink_FailsTheBuild_WithObjectDisposed()
    {
        var sink = new HeadlessVideoSink();
        await sink.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => PlaybackController.Create(videoSink: sink));
    }

    [Fact]
    public async Task TheControllerReleasesItsClaims_OnlyAfterItHasDisposedTheSession()
    {
        // The session's dispose awaits the graph and the pacer's delivery loop, so a claim released
        // after it means nothing the controller sent can reach the sink after a next build claims it.
        var log = new List<string>();
        var session = new LoggingSession(log);
        var claims = new LoggingClaims(log);
        var controller = NewController(session, claims);
        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);

        await controller.DisposeAsync();

        Assert.Equal(["session disposed", "claims released"], log);
    }

    [Fact]
    public async Task AControllerWhoseSessionDisposeThrows_StillReleasesItsClaims()
    {
        var log = new List<string>();
        var session = new LoggingSession(log) { DisposeThrows = new InvalidOperationException("teardown") };
        var claims = new LoggingClaims(log);
        var controller = NewController(session, claims);
        Assert.True((await controller.LoadAsync(new FakeSource())).IsSuccess);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await controller.DisposeAsync());

        Assert.Equal(["session disposed", "claims released"], log);
    }

    [Fact]
    public void HeldMessage_SaysWhoHoldsTheSink_OrThatAnotherPlayerOrPassDid()
    {
        var sink = new HeadlessVideoSink();

        Assert.StartsWith(
            "The video sink HeadlessVideoSink is held by a pass over 'a.mp4'.",
            SinkClaims.HeldMessage(sink, "a pass over 'a.mp4'"),
            StringComparison.Ordinal
        );
        Assert.StartsWith(
            "The video sink HeadlessVideoSink is held by another player or pass.",
            SinkClaims.HeldMessage(sink, holder: null),
            StringComparison.Ordinal
        );
    }

    private static PlaybackControllerCore NewController(LoggingSession session, LoggingClaims claims) =>
        new(
            NullLogger<PlaybackControllerCore>.Instance,
            new OneSessionFactory(session),
            new PlaybackClock(new FakeTimeProvider()),
            sinkClaims: claims
        );

    // ─── Doubles ────────────────────────────────────────────────────

    /// <summary>An audio sink that claims through a <see cref="SinkClaimGate"/>, as the in-box ones do.</summary>
    private sealed class ClaimableAudioSink : IAudioSink, IClaimableSink
    {
        private readonly SinkClaimGate _claims = new(typeof(ClaimableAudioSink));

        public IDisposable? TryClaim(string claimant) => _claims.TryClaim(claimant);

        public string? Claimant => _claims.Claimant;

        public long ClaimOrder => _claims.Order;

        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _claims.Close();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Claims that log their release.</summary>
    private sealed class LoggingClaims(List<string> log) : IDisposable
    {
        public void Dispose() => log.Add("claims released");
    }

    private sealed class OneSessionFactory(IPlaybackSession session) : IPlaybackSessionFactory
    {
        public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks) => session;
    }

    /// <summary>A session that loads anything and logs its dispose.</summary>
    private sealed class LoggingSession(List<string> log) : IPlaybackSession
    {
        public Exception? DisposeThrows { get; init; }

        public MediaInfo? MediaInfo { get; } = new("fake", TimeSpan.FromSeconds(1), [], []);

        public TimeSpan Duration => TimeSpan.FromSeconds(1);

        public ValueTask InitializeAsync(IMediaSource source, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PlayAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            log.Add("session disposed");
            if (DisposeThrows is { } ex)
                throw ex;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSource : IMediaSource
    {
        public string DisplayName => "fake";
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }
}
