// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FrameFlow.MotionClip;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Same parser the headless path uses, so the two modes can't drift.
            ClipRecorderArgs args = ClipRecorderArgs.Parse(desktop.Args ?? Array.Empty<string>());
            desktop.MainWindow = new MainWindow { Args = args };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
