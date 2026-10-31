using DiskScope.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace DiskScope.App.Views;

/// <summary>Settings: temperature unit, thresholds, intervals, default block size (SPEC 4.3, 4.4, 4.12).</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(SettingsService settings)
    {
        var current = settings.Current;
        var unit = new ComboBox
        {
            Header = "Temperatureinheit",
            ItemsSource = new[] { "°C", "°F" },
            SelectedIndex = current.TemperatureUnit == TemperatureUnit.Fahrenheit ? 1 : 0,
        };
        var interval = Number("Temperatur abfragen alle (Sekunden)", current.TemperatureIntervalSeconds, 5, 3600);
        var hdd = Number("Warnschwelle HDD (°C)", current.HddWarningCelsius, 20, 100);
        var ssd = Number("Warnschwelle SSD (°C)", current.SsdWarningCelsius, 20, 120);
        var smart = Number("S.M.A.R.T. aktualisieren alle (Minuten)", current.SmartRefreshMinutes, 1, 1440);
        var block = new ComboBox
        {
            Header = "Standard-Blockgröße Benchmark",
            ItemsSource = new[] { "4K", "16K", "64K", "256K", "1M", "8M" },
            SelectedItem = current.BenchmarkBlockSize,
        };

        var autoShot = new ToggleSwitch { Header = "Screenshot nach jedem Test automatisch speichern", IsOn = current.AutoScreenshot };
        var folder = new TextBox { Header = "Ordner für automatische Screenshots", Text = current.ScreenshotFolder };

        Title = "Einstellungen";
        PrimaryButtonText = "Speichern";
        CloseButtonText = "Abbrechen";
        DefaultButton = ContentDialogButton.Primary;
        Content = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 12, Width = 320, Children = { unit, interval, hdd, ssd, smart, block, autoShot, folder } },
        };

        PrimaryButtonClick += (_, _) => settings.Save(current with
        {
            TemperatureUnit = unit.SelectedIndex == 1 ? TemperatureUnit.Fahrenheit : TemperatureUnit.Celsius,
            TemperatureIntervalSeconds = (int)interval.Value,
            HddWarningCelsius = (int)hdd.Value,
            SsdWarningCelsius = (int)ssd.Value,
            SmartRefreshMinutes = (int)smart.Value,
            BenchmarkBlockSize = block.SelectedItem as string ?? current.BenchmarkBlockSize,
            AutoScreenshot = autoShot.IsOn,
            ScreenshotFolder = string.IsNullOrWhiteSpace(folder.Text) ? current.ScreenshotFolder : folder.Text.Trim(),
        });
    }

    private static NumberBox Number(string header, int value, int min, int max) => new()
    {
        Header = header,
        Value = value,
        Minimum = min,
        Maximum = max,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };
}
