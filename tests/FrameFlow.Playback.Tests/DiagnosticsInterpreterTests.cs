// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using FrameFlow.Playback.Diagnostics;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Tests for <see cref="DiagnosticsInterpreter"/>, the delta-to-observation mapping that keeps
/// every consumer of the ADR-0034 snapshot from rediscovering which counter blames which stage.
/// </summary>
/// <remarks>
/// The load-bearing case is the generation guard. A pair straddling a <c>load</c> must come back
/// as a reset, not as observations — subtracting across it reports a session restart as a drop
/// burst, and reporting only increases silently swallows the new session's real errors until its
/// counters climb past the old high-water mark. Both failures are silent, so they are pinned
/// directly rather than left to the caller.
/// </remarks>
public sealed class DiagnosticsInterpreterTests
{
    // ── Builders ──────────────────────────────────────────────────────────────────────────

    private static PlaybackDiagnosticsSnapshot Snapshot(
        int generation = 1,
        long seeks = 0,
        bool eof = false,
        long videoDecodeErrors = 0,
        long audioDecodeErrors = 0,
        long packetsShed = 0,
        long droppedForSync = 0,
        long sinkDropped = 0,
        long underruns = 0,
        long backpressure = 0,
        bool loopStalled = false,
        TimeSpan? loopOverrun = null
    ) =>
        new(
            State: PlaybackState.Playing,
            SeekingState: SeekState.NotSeeking,
            RepeatMode: RepeatMode.Off,
            Position: TimeSpan.FromSeconds(1),
            Duration: TimeSpan.FromSeconds(10),
            MediaInfo: null,
            Pipeline: new PipelineDiagnosticsSnapshot(
                Stream: new DecodedMediaStreamDiagnosticsSnapshot(
                    Demux: new DemuxSessionDiagnosticsSnapshot(
                        PacketsRead: 0,
                        BytesRead: 0,
                        SeeksPerformed: seeks,
                        EndOfStreamReached: eof
                    ),
                    VideoDecoder: new VideoDecoderDiagnosticsSnapshot(
                        FramesDecoded: 0,
                        DecodeErrors: videoDecodeErrors,
                        HardwareBackend: null,
                        PacketsDroppedForBackpressure: packetsShed
                    ),
                    AudioDecoder: new AudioDecoderDiagnosticsSnapshot(
                        BuffersDecoded: 0,
                        DecodeErrors: audioDecodeErrors,
                        UsedSyntheticPts: false
                    ),
                    VideoChannelDepth: 0,
                    AudioChannelDepth: 0
                ),
                VideoSink: new VideoSinkDiagnosticsSnapshot(
                    FramesPresented: 0,
                    FramesDropped: sinkDropped,
                    LastPresentedPresentationTime: null,
                    LastPresentedAtUtc: null
                ),
                AudioSink: new AudioSinkDiagnosticsSnapshot(
                    PresentationTime: TimeSpan.Zero,
                    ProcessedSamplesPerChannel: 0,
                    SampleRate: 48000,
                    Channels: 2,
                    BlocksWritten: 0,
                    UnderrunCount: underruns,
                    BackpressureEvents: backpressure,
                    IsActive: true
                ),
                VideoFramesDroppedForSync: droppedForSync
            ),
            AvSyncDrift: null,
            LoopStalled: loopStalled,
            LoopOverrun: loopOverrun,
            SessionGeneration: generation
        );

    private static DiagnosticsObservation Single(DiagnosticsDelta delta) =>
        Assert.Single(delta.Observations);

    // ── The generation guard ──────────────────────────────────────────────────────────────

    [Fact]
    public void DifferentGenerations_ReportReset_NotObservations()
    {
        // The load case: demux and decoder counters restarted at zero, sink counters kept
        // climbing. Half the fields are not subtractable.
        var before = Snapshot(generation: 1, videoDecodeErrors: 40, sinkDropped: 100);
        var after = Snapshot(generation: 2, videoDecodeErrors: 0, sinkDropped: 104);

        var delta = DiagnosticsInterpreter.Compare(before, after);

        Assert.True(delta.IsReset);
        Assert.Equal(1, delta.FromGeneration);
        Assert.Equal(2, delta.ToGeneration);
        Assert.Empty(delta.Observations);
        Assert.NotNull(delta.ResetMessage);
    }

    [Fact]
    public void Reset_SuppressesObservationsThatWouldOtherwiseFire()
    {
        // Sink drops really did rise across the pair — the sink is long-lived and its counter
        // never restarted. Reporting it anyway would attribute a drop burst to a load.
        var before = Snapshot(generation: 1, sinkDropped: 10);
        var after = Snapshot(generation: 2, sinkDropped: 99);

        Assert.Empty(DiagnosticsInterpreter.Compare(before, after).Observations);
    }

    [Fact]
    public void Reset_IsDistinguishableFromNothingHappening()
    {
        // Both have empty observation lists. Only IsReset separates "could not measure" from
        // "measured, nothing moved" — which is the whole reason it is a value.
        var quiet = DiagnosticsInterpreter.Compare(Snapshot(), Snapshot());
        var reset = DiagnosticsInterpreter.Compare(Snapshot(generation: 1), Snapshot(generation: 2));

        Assert.Empty(quiet.Observations);
        Assert.Empty(reset.Observations);
        Assert.False(quiet.IsReset);
        Assert.True(reset.IsReset);
        Assert.Null(quiet.ResetMessage);
    }

    [Fact]
    public void SameGeneration_IsComparable_EvenAcrossAGenerationGreaterThanOne()
    {
        var delta = DiagnosticsInterpreter.Compare(
            Snapshot(generation: 7, videoDecodeErrors: 2),
            Snapshot(generation: 7, videoDecodeErrors: 5)
        );

        Assert.False(delta.IsReset);
        Assert.Equal(3, Single(delta).Delta);
    }

    // ── Nothing moved ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IdenticalSnapshots_ProduceNoObservations()
    {
        var delta = DiagnosticsInterpreter.Compare(Snapshot(), Snapshot());

        Assert.False(delta.IsReset);
        Assert.Empty(delta.Observations);
    }

    [Fact]
    public void CounterGoingBackwards_WithinASession_IsIgnored()
    {
        // Cannot happen from a correct producer. If it does, it is a bug in the producer, not an
        // event in the media, so it must not surface as an observation.
        var delta = DiagnosticsInterpreter.Compare(
            Snapshot(sinkDropped: 50),
            Snapshot(sinkDropped: 10)
        );

        Assert.Empty(delta.Observations);
    }

    // ── Individual counters ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DiagnosticsObservationKind.VideoDecodeErrors, DiagnosticsObservationSeverity.Error)]
    [InlineData(DiagnosticsObservationKind.AudioDecodeErrors, DiagnosticsObservationSeverity.Error)]
    [InlineData(DiagnosticsObservationKind.VideoPacketsShed, DiagnosticsObservationSeverity.Warning)]
    [InlineData(DiagnosticsObservationKind.FramesDroppedForSync, DiagnosticsObservationSeverity.Warning)]
    [InlineData(DiagnosticsObservationKind.SinkFramesDropped, DiagnosticsObservationSeverity.Warning)]
    [InlineData(DiagnosticsObservationKind.AudioUnderruns, DiagnosticsObservationSeverity.Warning)]
    [InlineData(DiagnosticsObservationKind.AudioBackpressure, DiagnosticsObservationSeverity.Info)]
    [InlineData(DiagnosticsObservationKind.DemuxSeeks, DiagnosticsObservationSeverity.Info)]
    public void EachCounter_ReportsItsOwnKindAndSeverity(
        DiagnosticsObservationKind kind,
        DiagnosticsObservationSeverity severity
    )
    {
        var after = kind switch
        {
            DiagnosticsObservationKind.VideoDecodeErrors => Snapshot(videoDecodeErrors: 3),
            DiagnosticsObservationKind.AudioDecodeErrors => Snapshot(audioDecodeErrors: 3),
            DiagnosticsObservationKind.VideoPacketsShed => Snapshot(packetsShed: 3),
            DiagnosticsObservationKind.FramesDroppedForSync => Snapshot(droppedForSync: 3),
            DiagnosticsObservationKind.SinkFramesDropped => Snapshot(sinkDropped: 3),
            DiagnosticsObservationKind.AudioUnderruns => Snapshot(underruns: 3),
            DiagnosticsObservationKind.AudioBackpressure => Snapshot(backpressure: 3),
            DiagnosticsObservationKind.DemuxSeeks => Snapshot(seeks: 3),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var observation = Single(DiagnosticsInterpreter.Compare(Snapshot(), after));

        Assert.Equal(kind, observation.Kind);
        Assert.Equal(severity, observation.Severity);
        Assert.Equal(3, observation.Delta);
        Assert.NotEmpty(observation.Message);
    }

    // ── Rising edges ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EndOfStream_ReportsOnTheRisingEdgeOnly()
    {
        var rising = DiagnosticsInterpreter.Compare(Snapshot(eof: false), Snapshot(eof: true));
        Assert.Equal(DiagnosticsObservationKind.EndOfStream, Single(rising).Kind);
        Assert.Equal(1, Single(rising).Delta);

        // Already at EOF for both polls — reporting it every interval would be noise.
        Assert.Empty(
            DiagnosticsInterpreter.Compare(Snapshot(eof: true), Snapshot(eof: true)).Observations
        );
    }

    [Fact]
    public void LoopStalled_ReportsOnTheRisingEdgeOnly()
    {
        var rising = DiagnosticsInterpreter.Compare(
            Snapshot(loopStalled: false),
            Snapshot(loopStalled: true, loopOverrun: TimeSpan.FromSeconds(4.2))
        );

        var observation = Single(rising);
        Assert.Equal(DiagnosticsObservationKind.LoopStalled, observation.Kind);
        Assert.Equal(DiagnosticsObservationSeverity.Error, observation.Severity);
        Assert.Contains("4.2s", observation.Message, StringComparison.Ordinal);

        Assert.Empty(
            DiagnosticsInterpreter
                .Compare(Snapshot(loopStalled: true), Snapshot(loopStalled: true))
                .Observations
        );
    }

    [Fact]
    public void LoopStalled_WithoutAnOverrun_StillReports()
    {
        var observation = Single(
            DiagnosticsInterpreter.Compare(
                Snapshot(loopStalled: false),
                Snapshot(loopStalled: true, loopOverrun: null)
            )
        );

        Assert.Equal(DiagnosticsObservationKind.LoopStalled, observation.Kind);
    }

    // ── Several at once ───────────────────────────────────────────────────────────────────

    [Fact]
    public void MultipleCounters_AllReport_InPipelineOrder()
    {
        var delta = DiagnosticsInterpreter.Compare(
            Snapshot(),
            Snapshot(seeks: 1, videoDecodeErrors: 2, droppedForSync: 3, sinkDropped: 4)
        );

        Assert.Collection(
            delta.Observations,
            o => Assert.Equal(DiagnosticsObservationKind.DemuxSeeks, o.Kind),
            o => Assert.Equal(DiagnosticsObservationKind.VideoDecodeErrors, o.Kind),
            o => Assert.Equal(DiagnosticsObservationKind.FramesDroppedForSync, o.Kind),
            o => Assert.Equal(DiagnosticsObservationKind.SinkFramesDropped, o.Kind)
        );
    }

    // ── Guards ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compare_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(
            () => DiagnosticsInterpreter.Compare(null!, Snapshot())
        );
        Assert.Throws<ArgumentNullException>(
            () => DiagnosticsInterpreter.Compare(Snapshot(), null!)
        );
    }

    [Fact]
    public void EmptySnapshot_IsGenerationZero_SoTheFirstLoadReadsAsAReset()
    {
        // The unloaded seed and the first real session are not comparable, which is correct:
        // there was no interval to measure.
        var delta = DiagnosticsInterpreter.Compare(
            PlaybackDiagnosticsSnapshot.Empty,
            Snapshot(generation: 1)
        );

        Assert.Equal(0, PlaybackDiagnosticsSnapshot.Empty.SessionGeneration);
        Assert.True(delta.IsReset);
    }
}
