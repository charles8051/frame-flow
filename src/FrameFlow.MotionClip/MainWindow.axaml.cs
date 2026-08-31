// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FrameFlow.Avalonia;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery;
using Periphery.Camera;

namespace FrameFlow.MotionClip;

/// <summary>
/// Windowed host for the motion clip recorder (ADR-0052 §2). Wraps the
/// same <see cref="MotionClipRecorder"/> engine the headless path uses,
/// adding a live <see cref="FrameFlowVideoView"/> preview branch and a
/// status bar (recording chip + clip count). The camera is tracked by a
/// <see cref="DeviceSessionHost{TSession}"/> tied to the window's lifetime —
/// it runs whether or not a camera is plugged in and (re)connects as the
/// device comes and goes. Closing the window drains and saves any
/// in-progress clip before exit. <c>--synthetic</c> swaps the camera host
/// for a directly-run generated scene.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly IBrush RecordingBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x3A, 0x3A));

    private readonly CancellationTokenSource _windowCts = new();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private MotionClipRecorder? _recorder;

    // Camera mode tracks via the host; synthetic mode runs the graph directly.
    private DeviceSessionHost<CameraSession>? _host;
    private Task? _graphTask;
    private string _sourceLabel = "(starting)";

    private DispatcherTimer? _statsTimer;
    private int _lastRendered;
    private DateTime _lastStatsAt;
    private bool _cleanupDone;

    /// <summary>Parsed CLI options, supplied by <see cref="App"/>.</summary>
    public ClipRecorderArgs Args { get; set; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _loggerFactory = RecorderLogging.Create(Args.LogFile, Args.LogDirectory, Args.LogLevel);

        // The view materialised its sink on attach; EnsureSink returns it.
        IVideoSink preview = VideoView.EnsureSink();

        _recorder = MotionClipRecorder.Create(Args, preview, _loggerFactory);
        if (_recorder is null)
        {
            StatusChip.Text = "● Error";
            StatusChip.Foreground = RecordingBrush;
            StatsText.Text = "FFmpeg bootstrap failed — see log. Cannot encode clips.";
            return;
        }

        _recorder.Logger.LogInformation(
            "MotionClip window loaded (fps={Fps}, sensitivity={Sensitivity:0.##} → "
                + "ratio={Threshold:0.###}, output={Out}, logFile={LogFile}).",
            Args.FrameRate,
            Args.Sensitivity,
            Args.MotionThreshold,
            Path.GetFullPath(Args.OutputDirectory),
            Args.LogFile ?? "(none)"
        );

        if (Args.Synthetic)
        {
            _sourceLabel = "synthetic scene";
            FrameFlow.Graph.Graph graph = _recorder.BuildGraph(
                SyntheticSceneSource.Create(
                    RecorderPipeline.CaptureWidth,
                    RecorderPipeline.CaptureHeight,
                    Args.FrameRate
                )
            );
            // Task.Run so the UI thread isn't blocked by graph.RunAsync,
            // which awaits all sink pumps for the lifetime of the source.
            _graphTask = Task.Run(async () =>
            {
                try
                {
                    await graph.RunAsync(_windowCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on window close / --exit-after.
                }
                catch (Exception ex)
                {
                    _recorder.Logger.LogError(ex, "Recorder graph faulted.");
                }
            });
        }
        else
        {
            _sourceLabel = Args.IdStartsWith is { } prefix ? $"camera {prefix}*" : "camera";
            DeviceProfile profile = await CameraTracking
                .BuildProfileAsync(Args.IdStartsWith, Args.CameraIndex, _recorder.Logger, _windowCts.Token)
                .ConfigureAwait(true);
            _host = await _recorder
                .StartCameraAsync(
                    profile,
                    // On unplug, drop the frozen last frame so the preview goes
                    // black (the view sits on a black Border) until a camera returns.
                    onDisconnected: () => Dispatcher.UIThread.Post(() => VideoView.Clear()),
                    _windowCts.Token
                )
                .ConfigureAwait(true);
        }

        PopulateSectorOverlay(_recorder.Gate.SectorMask);

        _lastStatsAt = DateTime.UtcNow;
        _statsTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnStatsTick
        );
        _statsTimer.Start();

        if (Args.ExitAfterSeconds > 0)
        {
            var exitTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(Args.ExitAfterSeconds),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    _recorder.Logger.LogInformation("--exit-after elapsed: closing.");
                    Close();
                }
            );
            exitTimer.Start();
        }

        _recorder.Logger.LogInformation("Pipeline running. Source = {Source}.", _sourceLabel);
    }

    /// <summary>
    /// Renders the 3×3 numpad-grid overlay against the live preview to show
    /// which sectors motion detection is watching. Un-armed cells get a
    /// semi-transparent dark tint plus the numpad number; armed cells stay
    /// fully transparent. When every sector is armed (default), the overlay
    /// is hidden — no visual noise for the common case.
    /// </summary>
    private void PopulateSectorOverlay(MotionSectorMask mask)
    {
        SectorOverlay.Children.Clear();
        if (mask.AllArmed)
        {
            SectorOverlay.IsVisible = false;
            return;
        }
        SectorOverlay.IsVisible = true;

        // Tint colour for un-armed cells. ~55% black is dark enough to read
        // as "this is dimmed" without obscuring the underlying preview
        // entirely.
        var unarmedBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0x00, 0x00, 0x00));

        for (int n = 1; n <= MotionSectorMask.SectorCount; n++)
        {
            (int row, int col) = MotionSectorMask.RowColFor(n);
            bool armed = mask.IsArmed(n);

            var cell = new Border
            {
                Background = armed ? Brushes.Transparent : unarmedBrush,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = n.ToString(),
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
                    Margin = new global::Avalonia.Thickness(4, 2, 0, 0),
                },
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            SectorOverlay.Children.Add(cell);
        }
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        if (_recorder is null)
            return;

        DateTime now = DateTime.UtcNow;
        double dt = (now - _lastStatsAt).TotalSeconds;
        if (dt < 0.5)
            return;
        _lastStatsAt = now;

        int rendered = VideoView.RenderedFrameCount;
        double fps = (rendered - _lastRendered) / dt;
        _lastRendered = rendered;

        bool recording = _recorder.Gate.IsBuilding;
        StatusChip.Text = recording ? "● Recording" : "○ Idle";
        StatusChip.Foreground = recording ? RecordingBrush : IdleBrush;

        // fps near zero in camera mode means "no camera connected yet".
        string state = !Args.Synthetic && fps < 0.5 ? "waiting for camera" : $"src≈{fps,4:F0} fps";
        StatsText.Text =
            $"{state}   motion={_recorder.Gate.LastMotionRatio,6:P1}   "
            + $"clips={_recorder.Encoder.ClipsSaved}   {_sourceLabel}";
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_cleanupDone)
            return;

        // Defer the actual close until the pipeline drains and any in-progress
        // clip is saved, so --exit-after / manual-close runs produce a complete
        // MP4 instead of a truncated one.
        e.Cancel = true;
        _statsTimer?.Stop();

        try
        {
            _windowCts.Cancel();
        }
        catch
        {
            // idempotent
        }

        if (_graphTask is not null)
        {
            try
            {
                await _graphTask.ConfigureAwait(true);
            }
            catch
            {
                // graph faults are logged in the run task
            }
        }

        if (_host is not null)
        {
            try
            {
                await _host.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _recorder?.Logger.LogWarning(ex, "Camera host teardown threw.");
            }
        }

        if (_recorder is not null)
        {
            // Flushes any in-progress clip, drains the encoder worker, logs
            // the final clip count. Single call replaces the duplicated
            // flush-+-drain-+-log pattern that lived in OnClosing and
            // Program.RunHeadlessAsync.
            await _recorder.DisposeAsync().ConfigureAwait(true);
        }

        _loggerFactory.Dispose();
        _cleanupDone = true;
        Close();
    }
}
