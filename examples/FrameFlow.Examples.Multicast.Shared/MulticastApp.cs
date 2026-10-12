using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace FrameFlow.Examples.Multicast.Shared;

/// <summary>The Avalonia application each multicast example runs its main window in.</summary>
public sealed class MulticastApp(Func<Window> createMainWindow) : Application
{
    /// <summary>Starts the application and shows the window <paramref name="createMainWindow"/> builds.</summary>
    public static void Run(string[] args, Func<Window> createMainWindow) =>
        AppBuilder
            .Configure(() => new MulticastApp(createMainWindow))
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);

    /// <inheritdoc />
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = createMainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
