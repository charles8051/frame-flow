using System.Diagnostics;
using System.Globalization;
using FrameFlow.Graph;
using FrameFlow.Inference;
using FrameFlow.Inference.Dml;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using FrameFlow.Yolo;

// Question (#231): does a deeper video ring help an overlay, or does keying the overlay off the
// presented frame decide how well it lines up? One run plays a clip through a player whose video
// branch runs YOLO on DirectML, and measures both overlay rules against every presented frame:
//
//   keyed   the result for the frame on screen, PresentedResults' rule: the latest at or before it
//   posted  whichever result was posted last, the rule an overlay drawn from the branch follows
//
// The ring depth is ClockSelectVideoSink.DefaultCapacity. Measuring a deeper ring means building
// with that constant raised, until #236 lets the session pass a depth.
//
//   dotnet run --project spikes/LookaheadOverlayProbe -c Release -- <clip.mp4> [--label name]

string? clip = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
if (clip is null)
{
    Console.WriteLine("usage: LookaheadOverlayProbe <clip> [--label name]");
    return 2;
}

// Internal to FrameFlow.Playback; read so the output says which build ran.
object? depth = typeof(PlaybackController).Assembly.GetType("FrameFlow.Playback.ClockSelectVideoSink")
    ?.GetField("DefaultCapacity")?.GetValue(null);
string label = Arg("--label") ?? $"ring depth {depth}";

using var detector = await Task.Run(() => Yolov8Detector.CreateAsync(sessionFactory: path => new DmlInferenceSession(path)));

var sink = new PresentingSink();
using var keyed = new PresentedResults<IReadOnlyList<Detection>>(sink);
InferenceResult<IReadOnlyList<Detection>>? latestPosted = null;
var posted = new List<(TimeSpan Pts, long At)>();
var presented = new List<(TimeSpan Pts, long At, TimeSpan? Keyed, TimeSpan? Posted)>();

// PresentedResults subscribed first, so its Current is this frame's when this handler runs.
sink.FramePresented += (_, e) =>
{
    long at = Stopwatch.GetTimestamp();
    lock (presented)
        presented.Add((e.PresentationTime, at, keyed.Current?.Timestamp, Volatile.Read(ref latestPosted)?.Timestamp));
};

void OnResult(InferenceResult<IReadOnlyList<Detection>> result)
{
    lock (posted)
        posted.Add((result.Timestamp, Stopwatch.GetTimestamp()));
    Volatile.Write(ref latestPosted, result);
    keyed.Post(result);
}

await using var player = await FrameFlowPlayer.Create()
    .WithMedia(clip)
    .WithVideoSink(sink)
    .ConfigureVideo(chain => chain.Infer("yolo", detector, OnResult))
    .BuildPlayerAsync();

var ended = new TaskCompletionSource<PlaybackState>(TaskCreationOptions.RunContinuationsAsynchronously);
using var subscription = player.StateChanged.Subscribe(new Observer(state =>
{
    if (state is PlaybackState.Ended or PlaybackState.Error)
        ended.TrySetResult(state);
}));

var clock = Stopwatch.StartNew();
var played = await player.PlayAsync();
if (!played.IsSuccess)
{
    Console.WriteLine($"play failed: {played.Error?.Message}");
    return 1;
}

var final = await ended.Task.WaitAsync(TimeSpan.FromMinutes(5));
clock.Stop();

double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
double P(IReadOnlyList<double> values, double q) =>
    values.Count == 0 ? double.NaN : values.OrderBy(v => v).ElementAt(Math.Min(values.Count - 1, (int)(q * values.Count)));

var shownKeyed = presented.Where(p => p.Keyed is not null).Select(p => (p.Pts - p.Keyed!.Value).TotalMilliseconds).ToList();
var shownPosted = presented.Where(p => p.Posted is not null).Select(p => (p.Pts - p.Posted!.Value).TotalMilliseconds).ToList();
var presentedAt = presented.GroupBy(p => p.Pts).ToDictionary(g => g.Key, g => g.First().At);
var leads = posted.Where(r => presentedAt.ContainsKey(r.Pts)).Select(r => Ms(presentedAt[r.Pts] - r.At)).ToList();

Console.WriteLine($"{label}: {final}, {presented.Count} frames presented, {posted.Count} results in {clock.Elapsed.TotalSeconds:F1} s");
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"  keyed   age of the result shown (ms): p50 {P(shownKeyed, 0.5):F1}  p95 {P(shownKeyed, 0.95):F1}  max {shownKeyed.DefaultIfEmpty(double.NaN).Max():F1}  own result {100.0 * shownKeyed.Count(v => v == 0) / Math.Max(1, presented.Count):F1}%"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"  posted  age of the result shown (ms): p50 {P(shownPosted, 0.5):F1}  p95 |age| {P(shownPosted.Select(Math.Abs).ToList(), 0.95):F1}  from a later frame {100.0 * shownPosted.Count(v => v < 0) / Math.Max(1, presented.Count):F1}%  earliest {shownPosted.DefaultIfEmpty(double.NaN).Min():F1}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"  result ready before its frame is shown (ms): p50 {P(leads, 0.5):F1}  p5 {P(leads, 0.05):F1}  ready in time {100.0 * leads.Count(v => v >= 0) / Math.Max(1, leads.Count):F1}%"));
return 0;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

/// <summary>A video sink that shows nothing and raises FramePresented as each frame is handed to it.</summary>
sealed class PresentingSink : IVideoSink, IFramePresentedSource
{
    public event EventHandler<FramePresentedInfo>? FramePresented;

    public int? MaxHeldFrames => 0;

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        using (frame)
            FramePresented?.Invoke(this, new FramePresentedInfo(frame.Pts, DateTime.UtcNow));
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class Observer(Action<PlaybackState> onNext) : IObserver<PlaybackState>
{
    public void OnNext(PlaybackState value) => onNext(value);

    public void OnError(Exception error)
    {
    }

    public void OnCompleted()
    {
    }
}
