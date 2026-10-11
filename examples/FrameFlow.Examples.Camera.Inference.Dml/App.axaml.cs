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
            var args = desktop.Args ?? [];
            desktop.MainWindow = new MainWindow
            {
                // --model <path.onnx>: a YOLOv8 export to run instead of the downloaded yolov8n.
                // The detector reads the input size and classes from the model.
                ModelPath = ValueAfter(args, "--model"),
                // --face <path.onnx>: a BlazeFace model to run instead of YOLO. FrameFlow does
                // not download one.
                FaceModelPath = ValueAfter(args, "--face"),
                // --camera <index>: the camera to open first.
                CameraIndex = int.TryParse(ValueAfter(args, "--camera"), out var camera) ? camera : 0,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
