// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Periphery;
using Periphery.Camera;

namespace FrameFlow.MotionClip;

/// <summary>
/// The motion-clip recording engine: owns the <see cref="RecordingGate"/>,
/// the <see cref="ClipEncoderSink"/>, and the bootstrapping + final-drain
/// lifecycle they share. Headless (<see cref="Program"/>) and windowed
/// (<see cref="MainWindow"/>) entry points wrap the same engine instead of
/// each constructing their own pair — a fix for the drift this codebase
/// kept seeing where new options (sensitivity, warmup, max-clip cap) had
/// to be added in two places.
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction.</b> Use <see cref="Create"/>, not <c>new</c>. The
/// factory runs the FFmpeg native bootstrap (which can fail) and returns
/// <see langword="null"/> on failure so the caller surfaces the failure
/// in its own shell-appropriate way (exit code 1 for the console, error
/// chip for the window). Constructing succeeded means the engine is ready
/// to be driven.
/// </para>
/// <para>
/// <b>Driving the engine.</b> The recorder doesn't own the graph or the
/// camera host's lifetime — only their inputs (gate + encoder). The
/// caller picks the source shape:
/// </para>
/// <list type="bullet">
/// <item><b>Synthetic / one-shot source:</b> call <see cref="BuildGraph"/>
/// and drive the returned <see cref="Graph"/> directly. Suits the
/// synthetic-scene and any fixed-source test rigs.</item>
/// <item><b>Camera (resilient, multi-session):</b> call
/// <see cref="StartCameraAsync"/>, which wraps Periphery's
/// <see cref="DeviceSessionHost{TSession}"/> with the recorder's gate +
/// encoder. The caller disposes the returned host to stop tracking.</item>
/// </list>
/// <para>
/// <b>Shutdown.</b> Always <c>await using</c> or explicitly
/// <see cref="DisposeAsync"/> the recorder. Disposal flushes any
/// in-progress clip into the encoder queue, awaits the encoder worker
/// drain so every queued segment lands on disk, and logs the final
/// clip count.
/// </para>
/// </remarks>
public sealed class MotionClipRecorder : IAsyncDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly RecordingGate _gate;
    private readonly ClipEncoderSink _encoder;
    private readonly IVideoSink? _preview;
    private readonly int _cameraBuffers;
    private int _disposed;

    private MotionClipRecorder(
        RecordingGate gate,
        ClipEncoderSink encoder,
        IVideoSink? preview,
        int cameraBuffers,
        ILogger logger,
        ILoggerFactory loggers
    )
    {
        _gate = gate;
        _encoder = encoder;
        _preview = preview;
        _cameraBuffers = cameraBuffers;
        Logger = logger;
        _loggers = loggers;
    }

    /// <summary>
    /// The recording gate. Exposed so shells can read status
    /// (<see cref="RecordingGate.IsBuilding"/>,
    /// <see cref="RecordingGate.LastMotionRatio"/>) for chips / stats lines.
    /// </summary>
    public RecordingGate Gate => _gate;

    /// <summary>
    /// The encoder sink. Exposed so shells can read
    /// <see cref="ClipEncoderSink.ClipsSaved"/> for the status display.
    /// </summary>
    public ClipEncoderSink Encoder => _encoder;

    /// <summary>
    /// A general-purpose logger named "MotionClip". Shells use it for
    /// the startup banner and lifecycle messages so banner formatting
    /// stays consistent across console and window.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Factory: bootstrap FFmpeg, then construct the gate + encoder using
    /// the supplied arguments and preview sink. Returns <see langword="null"/>
    /// if the FFmpeg native bootstrap failed — the caller handles surfacing.
    /// </summary>
    /// <param name="args">Parsed CLI options. The recorder reads frame
    /// rate, sensitivity, max-clip seconds, output directory, etc. and
    /// derives pre-roll / post-roll / warmup frame counts from them.</param>
    /// <param name="preview">Optional preview sink. <see langword="null"/>
    /// for headless. When non-<see langword="null"/>, the preview is wired
    /// as a graph-native sibling consumer of the display-resolution stream
    /// inside <see cref="RecorderPipeline.BuildGraph"/> — slow UI rendering
    /// can't back-pressure motion detection (the branches have independent
    /// pumps and the preview edge is <see cref="EdgeOptions.LatestWins(int)"/>).
    /// The preview sink remains caller-owned: the graph never disposes it.</param>
    /// <param name="loggers">Logger factory shared with the rest of the
    /// process. Disposed by the caller.</param>
    public static MotionClipRecorder? Create(
        ClipRecorderArgs args,
        IVideoSink? preview,
        ILoggerFactory loggers
    )
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(loggers);

        ILogger logger = loggers.CreateLogger("MotionClip");
        if (!NativeBootstrap.InitializeFfmpeg(loggers, logger))
            return null;

        int preRollFrames = Math.Max(1, (int)Math.Round(2.0 * args.FrameRate));
        int postRollFrames = Math.Max(1, (int)Math.Round(3.0 * args.FrameRate));
        int maxFramesPerClip = Math.Max(postRollFrames + 1, args.MaxClipSeconds * args.FrameRate);

        var gate = new RecordingGate(
            new RecordingGateOptions
            {
                PreRollFrames = preRollFrames,
                PostRollFrames = postRollFrames,
                MaxFramesPerClip = maxFramesPerClip,
                MotionThreshold = args.MotionThreshold,
                // Frame-rate-scaled warmup window so the wall-clock "ignore
                // sensor settling" interval stays ~1 s regardless of fps.
                WarmupFrames = args.FrameRate,
                // Numpad-grid mask. null = all 9 sectors armed (historic
                // behaviour); a subset restricts motion-trigger evaluation
                // to those cells without affecting preview or recording.
                MotionSectors = args.MotionSectors,
            },
            loggers.CreateLogger<RecordingGate>()
        );

        var encoder = new ClipEncoderSink(
            new ClipEncoderOptions
            {
                OutputDirectory = args.OutputDirectory,
                FrameRate = args.FrameRate,
                BitRate = 2_000_000,
            },
            loggers.CreateLogger<ClipEncoderSink>()
        );

        return new MotionClipRecorder(gate, encoder, preview, args.CameraBuffers, logger, loggers);
    }

    /// <summary>
    /// Builds a one-shot graph wiring <paramref name="source"/> through
    /// <see cref="RecorderPipeline"/> to this recorder's gate and encoder.
    /// The caller drives the graph (<see cref="Graph.RunAsync"/>) — directly
    /// for headless, wrapped in <see cref="Task.Run(Func{Task})"/> for the
    /// windowed shell so the UI thread isn't blocked.
    /// </summary>
    public FrameFlow.Graph.Graph BuildGraph(SourceNode<VideoFrameRef> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return RecorderPipeline.BuildGraph(source, _gate, _encoder, _preview);
    }

    /// <summary>
    /// Starts a resilient camera-tracking host that opens a session when
    /// the matching device appears, runs the recorder pipeline for that
    /// session's lifetime, and reconnects on unplug. The caller disposes
    /// the returned host to stop tracking; the gate and encoder remain
    /// alive across reconnects so the clip counter accumulates.
    /// </summary>
    public Task<DeviceSessionHost<CameraSession>> StartCameraAsync(
        DeviceProfile profile,
        Action? onDisconnected,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(profile);
        return CameraTracking.StartTrackedHostAsync(
            profile,
            _gate,
            _encoder,
            _preview,
            _cameraBuffers,
            _loggers,
            Logger,
            onDisconnected,
            ct
        );
    }

    /// <summary>
    /// Flushes any in-progress clip into the encoder queue, awaits the
    /// encoder worker draining (so every queued segment finishes encoding
    /// before the process exits), disposes the gate, and logs the final
    /// clip count. Idempotent.
    /// </summary>
    /// <remarks>
    /// Does NOT dispose the <see cref="ILoggerFactory"/> passed to
    /// <see cref="Create"/> — that's the caller's lifetime to manage. The
    /// "Stopped. Clips saved: N" line writes through the recorder's logger
    /// before disposal so it lands in the file sink before the factory closes.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The shell owns disposal of the flushed segment: it is enqueued (which AddRefs a
        // worker ref), then this method's ref is released in the finally — unconditionally,
        // on both the success and enqueue-threw paths. CA2000 can't see the disposal across
        // the await, but ownership here is explicit and total.
#pragma warning disable CA2000 // pending is disposed in the finally below
        if (_gate.Flush() is { } pending)
        {
            try
            {
                await _encoder
                    .EnqueueAsync(pending, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Final-flush enqueue threw.");
            }
            finally
            {
                pending.Dispose();
            }
        }
#pragma warning restore CA2000

        _gate.Dispose();

        try
        {
            await _encoder.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Encoder worker shutdown threw.");
        }

        Logger.LogInformation("Stopped. Clips saved: {Count}", _encoder.ClipsSaved);
    }
}
