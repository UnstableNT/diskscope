using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.App.ViewModels;
using DiskScope.Core;
using DiskScope.Core.Devices;
using DiskScope.Core.Smart;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DiskScope.App.Services;

/// <summary>One drive in the status overview.</summary>
public sealed partial class DriveStatusRow(DriveInfo drive) : ObservableObject
{
    public DriveInfo Drive { get; } = drive;

    public string Title => $"{Drive.Id.Number}: {Drive.Model}";

    [ObservableProperty]
    public partial string Temperature { get; set; } = "–";

    [ObservableProperty]
    public partial HealthStatus Status { get; set; } = HealthStatus.Unknown;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Wird geprüft …";
}

/// <summary>
/// Drive status overview (SPEC 4.13, P2): checks health and temperature of all drives periodically and
/// shows a Windows notification when a drive's status changes.
/// </summary>
public sealed partial class DriveStatusMonitor : IDisposable
{
    private readonly IDriveService _drives;
    private readonly ISmartService _smart;
    private readonly SettingsService _settings;
    private readonly DriveSelection _selection;
    private readonly ILogger<DriveStatusMonitor> _logger;
    private readonly DispatcherQueueTimer _timer;
    private bool _notificationsRegistered;

    public DriveStatusMonitor(
        IDriveService drives, ISmartService smart, SettingsService settings, DriveSelection selection, ILogger<DriveStatusMonitor> logger)
    {
        _drives = drives;
        _smart = smart;
        _settings = settings;
        _selection = selection;
        _logger = logger;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    public ObservableCollection<DriveStatusRow> Rows { get; } = [];

    public void Start()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _notificationsRegistered = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            LogNotificationsUnavailable(ex);
        }

        _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.Current.SmartRefreshMinutes));
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        // Raw-device tests have priority; the next tick catches up.
        if (_selection.IsBusy)
        {
            return;
        }

        try
        {
            var drives = await Task.Run(_drives.GetDrives).ConfigureAwait(true);
            foreach (var drive in drives)
            {
                var row = Rows.FirstOrDefault(r => r.Drive.Id == drive.Id);
                if (row is null)
                {
                    row = new DriveStatusRow(drive);
                    Rows.Add(row);
                }

                var result = await Task.Run(() => _smart.Read(drive)).ConfigureAwait(true);
                var previous = row.Status;
                row.Status = result.Overall;
                row.StatusText = HealthTexts.Of(result.Overall);
                row.Temperature = result.TemperatureCelsius is { } c ? Units.Temperature(c, _settings.Current.TemperatureUnit) : "–";

                if (previous != HealthStatus.Unknown && previous != result.Overall)
                {
                    Notify(drive, previous, result.Overall);
                }
            }

            foreach (var gone in Rows.Where(r => drives.All(d => d.Id != r.Drive.Id)).ToArray())
            {
                Rows.Remove(gone);
            }
        }
        catch (DiskScopeException ex)
        {
            LogRefreshFailed(ex);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_notificationsRegistered)
        {
            AppNotificationManager.Default.Unregister();
        }
    }

    private void Notify(DriveInfo drive, HealthStatus before, HealthStatus after)
    {
        LogStatusChanged(drive.Id.Number, before, after);
        if (!_notificationsRegistered)
        {
            return;
        }

        var notification = new AppNotificationBuilder()
            .AddText($"DiskScope: {drive.Model}")
            .AddText($"Gesundheitsstatus geändert: {HealthTexts.Of(before)} → {HealthTexts.Of(after)}")
            .BuildNotification();
        AppNotificationManager.Default.Show(notification);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "App notifications unavailable")]
    private partial void LogNotificationsUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Status overview refresh failed")]
    private partial void LogRefreshFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drive {Drive} health changed from {Before} to {After}")]
    private partial void LogStatusChanged(int drive, HealthStatus before, HealthStatus after);
}
