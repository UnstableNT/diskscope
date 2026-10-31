using DiskScope.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskScope.App.Controls;

public sealed partial class RangeOption : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(TestViewModelBase), typeof(RangeOption), new PropertyMetadata(null));

    public RangeOption()
    {
        InitializeComponent();
    }

    public TestViewModelBase ViewModel
    {
        get => (TestViewModelBase)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        var unit = new ComboBox { Header = "Einheit", ItemsSource = new[] { "Prozent", "GB" }, SelectedIndex = ViewModel.RangeInPercent ? 0 : 1 };
        var start = new NumberBox { Header = "Start", Value = ViewModel.RangeStart, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var end = new NumberBox { Header = "Ende", Value = ViewModel.RangeEnd, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["StatusErrorBrush"], TextWrapping = TextWrapping.Wrap };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Bereich festlegen",
            PrimaryButtonText = "Übernehmen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 12, Width = 280, Children = { unit, start, end, error } },
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var percent = unit.SelectedIndex == 0;
            if (double.IsNaN(start.Value) || double.IsNaN(end.Value) || start.Value >= end.Value || (percent && end.Value > 100))
            {
                error.Text = percent ? "Start muss kleiner als Ende sein, Ende höchstens 100 %." : "Start muss kleiner als Ende sein.";
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.RangeInPercent = unit.SelectedIndex == 0;
            ViewModel.RangeStart = start.Value;
            ViewModel.RangeEnd = end.Value;
            ViewModel.UseRange = true;
        }
    }
}
