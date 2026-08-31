using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FrameFlow.Examples.Camera.Inference.Dml;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();

            desktop.MainWindow = new MainWindow
            {
                // --model <path.onnx>: run this model instead of downloading
                // the stock yolov8n. The detector auto-infers input size /
                // class count (ADR-0050), so any minted variant works.
                ModelPath = ValueAfter(args, "--model"),
                // --face <path.onnx>: run BlazeFace face detection (box + 6
                // keypoints) instead of YOLO. Takes precedence over --model.
                // The module ships no weights (ADR-0051), so this must point
                // at a BlazeFace ONNX you supply.
                FaceModelPath = ValueAfter(args, "--face"),
                // --log-file <path>: opt-in debug file sink (per-frame
                // inference timing lands here at Debug).
                StartupLogFilePath = ValueAfter(args, "--log-file"),
                // --camera <index>: which enumerated camera to auto-select
                // (default 0). The first camera connects automatically on
                // startup regardless — no UI click needed.
                CameraIndex = IntAfter(args, "--camera", 0),
                // --exit-after <seconds>: self-close after N seconds so the
                // file logger flushes for non-interactive diagnostic runs.
                ExitAfterSeconds = DoubleAfter(args, "--exit-after", 0),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag)
                return args[i + 1];
        return null;
    }

    private static int IntAfter(string[] args, string flag, int fallback)
    {
        var v = ValueAfter(args, flag);
        return v is not null && int.TryParse(v, out var n) ? n : fallback;
    }

    private static double DoubleAfter(string[] args, string flag, double fallback)
    {
        var v = ValueAfter(args, flag);
        return v is not null
            && double.TryParse(
                v,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var n)
            ? n
            : fallback;
    }
}
