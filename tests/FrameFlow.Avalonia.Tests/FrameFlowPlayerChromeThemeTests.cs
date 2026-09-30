using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// The chrome ships no templates of its own: every button and slider it builds
/// takes its template from the host app's theme. A control the theme does not
/// match takes up space and draws nothing, and nothing else here renders the
/// chrome under a theme to notice.
/// </summary>
/// <remarks>
/// CI runs this against Avalonia 11 and again against 12, so it covers both
/// versions of Fluent's templates. <see cref="FrameFlowSeekBar"/> derives from
/// <see cref="Slider"/> and gets Fluent's Slider template only through its
/// <c>StyleKeyOverride</c>.
/// </remarks>
public sealed class FrameFlowPlayerChromeThemeTests
{
    [AvaloniaFact]
    public void EveryTemplatedControl_GetsATemplate_FromFluent()
    {
        var chrome = new FrameFlowPlayerChrome();
        // The theme goes in before the chrome, as an app loads it. Added after, Fluent's Slider
        // template never applies, on 11 or 12.
        var window = new Window { Width = 800, Height = 200 };
        window.Styles.Add(new FluentTheme());
        window.Content = chrome;
        window.Show();

        try
        {
            var templated = chrome.GetSelfAndLogicalDescendants().OfType<TemplatedControl>().ToList();

            Assert.Contains(chrome.SeekBar, templated);
            Assert.Contains(templated, c => c.GetType() == typeof(ToggleButton));
            Assert.Contains(templated, c => c.GetType() == typeof(Slider));
            Assert.All(
                templated,
                c => Assert.True(
                    c.Template is not null && c.GetVisualChildren().Any(),
                    $"{c.GetType().Name} has no template"));
        }
        finally
        {
            window.Close();
        }
    }
}
