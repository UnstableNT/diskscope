using DiskScope.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskScope.App.Views;

public sealed partial class HealthView : UserControl, IViewWithResults
{
    public HealthView()
    {
        ViewModel = App.Services.GetRequiredService<HealthViewModel>();
        InitializeComponent();
        if (ViewModel.Rows.Count == 0)
        {
            ViewModel.RefreshCommand.Execute(null);
        }
    }

    public HealthViewModel ViewModel { get; }

    public IResultText Results => ViewModel;

    /// <summary>"Protokoll": stored queries with the changes per attribute (SPEC 4.3).</summary>
    private async void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        var changes = ViewModel.HistoryChanges(out var entries);
        var list = new StackPanel { Spacing = 6 };
        if (changes.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = entries <= 1 ? "Noch keine Änderungen – das Protokoll speichert jede Abfrage, bei der sich Werte ändern." : "Keine Änderungen.",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (var change in changes.Take(500))
        {
            list.Children.Add(new TextBlock
            {
                Text = $"{change.Timestamp.LocalDateTime:g}   {change.Id:X2} {change.Name}:  {change.Before} → {change.After}",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"S.M.A.R.T.-Protokoll ({entries} gespeicherte Abfragen)",
            CloseButtonText = "Schließen",
            Content = new ScrollViewer { Content = list, MaxHeight = 480, Width = 640 },
        };
        await dialog.ShowAsync();
    }
}
