using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(FrameFlow.Avalonia.Tests.HeadlessAppBuilder))]

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Minimal Avalonia app for headless UI tests. Needed because
/// <c>FrameFlowVolumeControl</c> is a real control: its widgets only exist once
/// an Avalonia application is initialized, so its enable/disable behaviour
/// cannot be asserted from a plain unit test.
/// </summary>
internal static class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
