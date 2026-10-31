using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Devices;
using DiskScope.Core.Smart;
using Microsoft.UI.Dispatching;

namespace DiskScope.App.ViewModels;

public enum TemperatureLevel { Unknown, Normal, Warning, Critical }

/// <summary>A drive entry in the header list.</summary>
public sealed record DriveItem(DriveInfo Drive)
{
    public string Title => $"{Drive.Id.Number}: {Drive.Model} ({ByteSize.Format(Drive.SizeBytes)})";

    public override string ToString() => Title;
}

/// <summary>Header: drive list, temperature, admin hint (SPEC 4.1, 4.12, 6).</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IDriveService _drives;
    private readonly ISmartService _smart;
    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _temperatureTimer;
    private int? _minimum;
    private int? _maximum;

    public ShellViewModel(IDriveService drives, ISmartService smart, SettingsService settings, DriveSelection selection)
    {
        _drives = drives;
        _smart = smart;
        _settings = settings;
        Selection = selection;

        _temperatureTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _temperatureTimer.Tick += async (_, _) => await RefreshTemperatureAsync().ConfigureAwait(true);
        settings.Changed += (_, _) =>
        {
            ApplyTimerInterval();
            UpdateTemperatureText(LastCelsius);
        };
        ApplyTimerInterval();
    }

    public DriveSelection Selection { get; }

    public ObservableCollection<DriveItem> Drives { get; } = [];

    public bool IsElevated { get; } = Privileges.IsElevated;

    public bool ShowElevationHint => !IsElevated;

    [ObservableProperty]
    public partial DriveItem? SelectedDrive { get; set; }

    [ObservableProperty]
    public partial string TemperatureText { get; set; } = "– °C";

    [ObservableProperty]
    public partial string TemperatureDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TemperatureLevel TemperatureLevel { get; set; }

    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    private int? LastCelsius { get; set; }

    public async Task LoadAsync()
    {
        try
        {
            var all = await Task.Run(_drives.GetDrives).ConfigureAwait(true);
            Drives.Clear();
            foreach (var drive in all)
            {
                Drives.Add(new DriveItem(drive));
            }

            SelectedDrive = Drives.FirstOrDefault(d => !d.Drive.IsSystemDisk) ?? Drives.FirstOrDefault();
            ErrorText = null;
        }
        catch (DiskScopeException ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private static void RestartElevated()
    {
        if (Elevation.TryRestartElevated())
        {
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
    }

    partial void OnSelectedDriveChanged(DriveItem? value)
    {
        Selection.Current = value?.Drive;
        _minimum = _maximum = null;
        LastCelsius = null;
        UpdateTemperatureText(null);
        _ = RefreshTemperatureAsync();
    }

    private void ApplyTimerInterval()
    {
        _temperatureTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, _settings.Current.TemperatureIntervalSeconds));
        _temperatureTimer.Start();
    }

    private async Task RefreshTemperatureAsync()
    {
        if (Selection.Current is not { } drive)
        {
            return;
        }

        try
        {
            var reading = await Task.Run(() => _smart.ReadTemperature(drive)).ConfigureAwait(true);
            if (Selection.Current?.Id != drive.Id)
            {
                return;
            }

            if (reading.Celsius is { } c)
            {
                _minimum = Math.Min(_minimum ?? c, c);
                _maximum = Math.Max(_maximum ?? c, c);
            }

            LastCelsius = reading.Celsius;
            UpdateTemperatureText(reading.Celsius, reading.WarningCelsius, reading.CriticalCelsius);
        }
        catch (DiskScopeException)
        {
            UpdateTemperatureText(null);
        }
    }

    private void UpdateTemperatureText(int? celsius, int? driveWarning = null, int? driveCritical = null)
    {
        var settings = _settings.Current;
        if (celsius is not { } c || Selection.Current is not { } drive)
        {
            TemperatureText = settings.TemperatureUnit == TemperatureUnit.Fahrenheit ? "– °F" : "– °C";
            TemperatureLevel = TemperatureLevel.Unknown;
            TemperatureDetail = "Keine Temperatur verfügbar";
            return;
        }

        var warning = drive.Media == MediaKind.Hdd ? settings.HddWarningCelsius : settings.SsdWarningCelsius;
        var critical = driveCritical ?? warning + 10;
        TemperatureText = Units.Temperature(c, settings.TemperatureUnit);
        TemperatureLevel = c >= critical ? TemperatureLevel.Critical : c >= warning ? TemperatureLevel.Warning : TemperatureLevel.Normal;
        TemperatureDetail =
            $"Aktuell {Units.Temperature(c, settings.TemperatureUnit)}, " +
            $"min {Units.Temperature(_minimum ?? c, settings.TemperatureUnit)}, max {Units.Temperature(_maximum ?? c, settings.TemperatureUnit)}\n" +
            $"Warnschwelle {Units.Temperature(warning, settings.TemperatureUnit)}" +
            (driveWarning is { } dw ? $" (Laufwerk meldet {Units.Temperature(dw, settings.TemperatureUnit)})" : string.Empty);
    }

    public void Dispose() => _temperatureTimer.Stop();
}

public static class Units
{
    public static string Temperature(int celsius, TemperatureUnit unit) => unit == TemperatureUnit.Fahrenheit
        ? $"{TemperatureLimits.ToFahrenheit(celsius):0} °F"
        : $"{celsius} °C";

    public static string Rate(double? mbps) => mbps is { } v ? $"{v:0.0} MB/s" : "–";

    public static string Milliseconds(double? ms) => ms is { } v ? $"{v:0.000} ms" : "–";
}
