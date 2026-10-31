using DiskScope.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskScope.App.Views;

/// <summary>Tabs that arrive with later milestones (P2/P3).</summary>
public sealed partial class PlaceholderView : UserControl, IViewWithResults, IResultText
{
    public PlaceholderView(string name)
    {
        Content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = "", FontSize = 40 },
                new TextBlock { Text = name, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Diese Funktion folgt in einer späteren Version.", HorizontalAlignment = HorizontalAlignment.Center },
            },
        };
    }

    public IResultText Results => this;

    public string? ResultText => null;

    public string? ResultJson => null;
}
