using System.Globalization;
using System.Text;
using FrameFlow.Media;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.ZeroCopyInterop;

/// <summary>
/// Reads both players' diagnostics on every sample and writes one CSV row per player. A row
/// carries the counters the loop soak judges: frames presented in the window, frames dropped,
/// loops reported, and whether the loop-stall watchdog has fired.
/// </summary>
/// <remarks>
/// <para>
/// The frame counters come from the video sink, which lives as long as the window, so they keep
/// climbing across loops and across a session the player rebuilds. Each row's <c>presented</c> is
/// the difference since the previous row, so a pass that presents nothing shows as a zero.
/// </para>
/// <para>
/// The sampler owns nothing but its writer. It is driven by a timer the window starts.
/// </para>
/// </remarks>
internal sealed class SoakSampler : IDisposable
{
    private readonly StreamWriter _csv;
    private readonly ILogger _logger;
    private readonly string _label;
    private readonly List<Pane> _panes = [];
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    private int _samples;

    public SoakSampler(string csvPath, string label, ILogger logger)
    {
        _logger = logger;
        _label = label;
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        _csv = new StreamWriter(csvPath, append: false, Encoding.UTF8) { AutoFlush = true };
        _csv.WriteLine(
            "run,elapsed_s,pane,clip,state,repeat,position_s,presented,presented_total,"
                + "dropped_total,dropped_for_sync_total,loops,loop_stalled,errors,fps"
        );
        CsvPath = csvPath;
    }

    public string CsvPath { get; }

    /// <summary>Watches <paramref name="player"/> as the pane named <paramref name="name"/>.</summary>
    public void Watch(string name, string clip, IMediaPlayer player)
    {
        var pane = new Pane(name, clip, player);
        pane.Subscribe();
        _panes.Add(pane);
    }

    /// <summary>Writes one row per pane, and returns a line for the window's status text.</summary>
    public string Sample()
    {
        _samples++;
        var elapsed = (DateTimeOffset.UtcNow - _started).TotalSeconds;
        var status = new StringBuilder();
        foreach (var pane in _panes)
        {
            var row = pane.Read(elapsed);
            _csv.WriteLine(
                string.Join(
                    ',',
                    _label,
                    F(elapsed),
                    pane.Name,
                    pane.Clip,
                    row.State,
                    row.Repeat,
                    F(row.PositionSeconds),
                    row.Presented,
                    row.PresentedTotal,
                    row.DroppedTotal,
                    row.DroppedForSyncTotal,
                    row.Loops,
                    row.LoopStalls,
                    row.Errors,
                    F(row.Fps)
                )
            );
            status.Append(
                CultureInfo.InvariantCulture,
                $"{pane.Name}: {row.State} {row.Fps:F1} fps · {row.Loops} loops · "
                    + $"{row.LoopStalls} stalls · {row.Errors} errors\n"
            );
        }

        if (_samples % 10 == 1)
            _logger.LogInformation("Soak sample {N} written to {Path}.", _samples, CsvPath);

        return status.ToString();
    }

    public void Dispose()
    {
        foreach (var pane in _panes)
            pane.Dispose();
        _csv.Dispose();
    }

    private static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private sealed record Row(
        string State,
        string Repeat,
        double PositionSeconds,
        long Presented,
        long PresentedTotal,
        long DroppedTotal,
        long DroppedForSyncTotal,
        int Loops,
        int LoopStalls,
        int Errors,
        double Fps
    );

    private sealed class Pane(string name, string clip, IMediaPlayer player) : IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];
        private long _presentedAtLastSample;
        private double _elapsedAtLastSample;
        private int _loops;
        private int _loopStalls;
        private int _errors;

        public string Name { get; } = name;

        public string Clip { get; } = clip;

        public void Subscribe()
        {
            _subscriptions.Add(
                player.LoopRestarted.Subscribe(
                    new ActionObserver<LoopRestarted>(_ => Interlocked.Increment(ref _loops))
                )
            );
            _subscriptions.Add(
                player.LoopStalled.Subscribe(
                    new ActionObserver<LoopStalled>(_ => Interlocked.Increment(ref _loopStalls))
                )
            );
            _subscriptions.Add(
                player.ErrorOccurred.Subscribe(
                    new ActionObserver<PlaybackError>(_ => Interlocked.Increment(ref _errors))
                )
            );
        }

        public Row Read(double elapsed)
        {
            var diagnostics = player.GetDiagnostics();
            var presentedTotal = diagnostics.Pipeline.VideoSink.FramesPresented;
            var presented = presentedTotal - _presentedAtLastSample;
            var window = elapsed - _elapsedAtLastSample;
            _presentedAtLastSample = presentedTotal;
            _elapsedAtLastSample = elapsed;

            return new Row(
                diagnostics.State.ToString(),
                diagnostics.RepeatMode.ToString(),
                diagnostics.Position.TotalSeconds,
                presented,
                presentedTotal,
                diagnostics.Pipeline.VideoSink.FramesDropped,
                diagnostics.Pipeline.VideoFramesDroppedForSync,
                Volatile.Read(ref _loops),
                Volatile.Read(ref _loopStalls),
                Volatile.Read(ref _errors),
                window > 0 ? presented / window : 0
            );
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
        }
    }

    /// <summary>An observer that runs an action on each value and ignores completion.</summary>
    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value) => onNext(value);
    }
}
