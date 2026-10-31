using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DiskScope.App.Services;

/// <summary>
/// Draws the app content into the window frame (no white system title bar) and keeps the caption buttons
/// readable in light and dark mode.
/// </summary>
public static class TitleBarTheme
{
    public static void Apply(Window window, UIElement dragRegion)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(dragRegion);

        var bar = window.AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (window.Content is FrameworkElement root)
        {
            ApplyTheme(window, root.ActualTheme);
            root.ActualThemeChanged += (sender, _) => ApplyTheme(window, sender.ActualTheme);
        }
    }

    private static void ApplyTheme(Window window, ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        var bar = window.AppWindow.TitleBar;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x8B, 0x94, 0x9E) : Color.FromArgb(0xFF, 0x60, 0x60, 0x60);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0x00, 0x00, 0x00);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x40, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }
}
