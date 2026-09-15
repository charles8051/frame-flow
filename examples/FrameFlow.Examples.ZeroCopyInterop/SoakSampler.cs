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
/// Rows are appended, and the header is written only for a new file, so a before run and an after
/// run can share one file and be told apart by the run column.
/// </para>
/// <para>
/// A write that fails leaves the samples incomplete, which would read as a soak with a gap in it.
/// The sampler stops and says so through <see cref="Faulted"/> instead, and the window stops
/// sampling.
/// </para>
/// </remarks>
internal sealed class SoakSampler : IDisposable
{
    private const string Header =
        "run,elapsed_s,pane,clip,state,repeat,position_s,presented,presented_total,"
        + "dropped_total,dropped_for_sync_total,loops,loop_stalled,errors,fps";

    private readonly StreamWriter _csv;
    private readonly ILogger _logger;
    private readonly string _label;
    private readonly List<Pane> _panes = [];
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    private int _samples;

    private SoakSampler(StreamWriter csv, string csvPath, string label, ILogger logger)
    {
        _csv = csv;
        _logger = logger;
        _label = label;
        CsvPath = csvPath;
    }

    /// <summary>
    /// Opens <paramref name="csvPath"/> for appending, writing the header when the file is new.
    /// Returns <see langword="null"/> when the file cannot be opened, which aborts the soak rather
    /// than running it with nowhere to record it.
    /// </summary>
    public static SoakSampler? TryCreate(string csvPath, string label, ILogger logger)
    {
        StreamWriter? csv = null;
        try
        {
            if (Path.GetDirectoryName(csvPath) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            var isNew = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
            var endsMidRow = !isNew && !EndsWithNewLine(csvPath);

            csv = new StreamWriter(csvPath, append: true, Encoding.UTF8) { AutoFlush = true };
            if (isNew)
                csv.WriteLine(Header);
            else if (endsMidRow)
                // A file another tool wrote, or one a run was killed in the middle of, can end
                // mid-row. Start on a line of our own rather than extending that one.
                csv.WriteLine();

            var sampler = new SoakSampler(csv, csvPath, label, logger);
            csv = null;
            return sampler;
        }
        catch (Exception ex)
            when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
            )
        {
            // Disposing flushes, which can throw for the same reason the write did. The failure
            // being reported is the first one.
            try
            {
                csv?.Dispose();
            }
            catch (Exception cleanup)
            {
                logger.LogDebug(cleanup, "Closing the soak's CSV threw after it failed to open.");
            }

            logger.LogError(ex, "Soak samples cannot be written to {Path}.", csvPath);
            return null;
        }
    }

    /// <summary>Whether the file's last byte is a line feed.</summary>
    private static bool EndsWithNewLine(string path)
    {
        using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        probe.Seek(-1, SeekOrigin.End);
        return probe.ReadByte() == '\n';
    }

    public string CsvPath { get; }

    /// <summary>Whether a write failed. The run's samples stop at that point.</summary>
    public bool Faulted { get; private set; }

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
        if (Faulted)
            return "Soak stopped: the samples could not be written.";

        _samples++;
        var elapsed = (DateTimeOffset.UtcNow - _started).TotalSeconds;
        var status = new StringBuilder();
        foreach (var pane in _panes)
        {
            var row = pane.Read(elapsed);
            var line = string.Join(
                ',',
                Field(_label),
                Number(elapsed),
                Field(pane.Name),
                Field(pane.Clip),
                Field(row.State),
                Field(row.Repeat),
                Number(row.PositionSeconds),
                row.Presented,
                row.PresentedTotal,
                row.DroppedTotal,
                row.DroppedForSyncTotal,
                row.Loops,
                row.LoopStalls,
                row.Errors,
                Number(row.Fps)
            );

            try
            {
                _csv.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Faulted = true;
                _logger.LogError(ex, "Soak sample {N} could not be written; sampling stops.", _samples);
                return "Soak stopped: the samples could not be written.";
            }

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

    private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>
    /// Quotes a field that holds a comma, a quote or a line break, doubling any quote inside it.
    /// A label or a file name carrying one would otherwise shift every column after it.
    /// </summary>
    private static string Field(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

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
