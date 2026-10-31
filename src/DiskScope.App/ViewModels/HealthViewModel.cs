using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Devices;
using DiskScope.Core.Smart;
using Microsoft.UI.Dispatching;

namespace DiskScope.App.ViewModels;

public sealed record SmartRow(
    string Id, string Name, string Value, string Current, string Worst, string Threshold, string Raw, HealthStatus Status)
{
    public string StatusText => HealthTexts.Of(Status);
}

public static class HealthTexts
{
    public static string Of(HealthStatus status) => status switch
    {
        HealthStatus.Ok => "OK",
        HealthStatus.Warning => "Warnung",
        HealthStatus.Error => "Fehler",
        _ => "Unbekannt",
    };
}

/// <summary>Health tab: SMART / NVMe table, overall status, automatic refresh (SPEC 4.3).</summary>
public sealed partial class HealthViewModel : ObservableObject, IResultText
{
    private readonly ISmartService _smart;
    private readonly SettingsService _settings;
    private readonly DriveSelection _selection;
    private readonly DispatcherQueueTimer _timer;
    private DateTime _nextRefresh;
    private SmartResult? _result;

    private readonly ISelfTestService _selfTests;
    private readonly SmartHistory _history;

    public HealthViewModel(ISmartService smart, ISelfTestService selfTests, SmartHistory history, SettingsService settings, DriveSelection selection)
    {
        _smart = smart;
        _selfTests = selfTests;
        _history = history;
        _settings = settings;
        _selection = selection;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
        _timer.Start();

        selection.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(DriveSelection.Current))
            {
                await RefreshAsync().ConfigureAwait(true);
            }
        };
    }

    public ObservableCollection<SmartRow> Rows { get; } = [];

    [ObservableProperty]
    public partial HealthStatus Overall { get; set; } = HealthStatus.Unknown;

    [ObservableProperty]
    public partial string OverallText { get; set; } = "–";

    [ObservableProperty]
    public partial string SourceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Hint { get; set; }

    [ObservableProperty]
    public partial double CountdownFraction { get; set; }

    [ObservableProperty]
    public partial string CountdownText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string SelfTestText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SelfTestRunning { get; set; }

    public bool SelfTestAvailable => IsElevated && _selection.Current?.Bus is Core.Devices.BusType.Sata or Core.Devices.BusType.Raid;

    public bool IsElevated { get; } = Privileges.IsElevated;

    /// <summary>Attribute changes between stored queries, newest first ("Protokoll").</summary>
    public IReadOnlyList<SmartChange> HistoryChanges(out int entries)
    {
        if (_selection.Current is not { } drive)
        {
            entries = 0;
            return [];
        }

        var all = _history.Load(drive);
        entries = all.Count;
        return SmartHistory.Changes(all);
    }

    [RelayCommand]
    private Task StartQuickSelfTestAsync() => SelfTestAsync(d => _selfTests.Start(d, SelfTestKind.Quick));

    [RelayCommand]
    private Task StartExtendedSelfTestAsync() => SelfTestAsync(d => _selfTests.Start(d, SelfTestKind.Extended));

    [RelayCommand]
    private Task AbortSelfTestAsync() => SelfTestAsync(_selfTests.Abort);

    private async Task SelfTestAsync(Func<Core.Devices.DriveInfo, SelfTestStatus> action)
    {
        if (_selection.Current is not { } drive)
        {
            return;
        }

        try
        {
            ShowSelfTest(await Task.Run(() => action(drive)).ConfigureAwait(true));
        }
        catch (DiskScopeException ex)
        {
            SelfTestText = ex.Message;
        }
    }

    private void ShowSelfTest(SelfTestStatus status)
    {
        SelfTestRunning = status.State == SelfTestState.Running;
        var duration = status.ShortMinutes is { } s && status.ExtendedMinutes is { } e ? $" (Dauer kurz ≈ {s} min, erweitert ≈ {e} min)" : string.Empty;
        SelfTestText = status.Text + duration;
    }

    public string? ResultJson => _result is null ? null : System.Text.Json.JsonSerializer.Serialize(_result, Core.Serialization.DiskScopeJsonContext.Default.SmartResult);

    public string? ResultText
    {
        get
        {
            if (_result is null)
            {
                return null;
            }

            var text = new StringBuilder().AppendLine(CultureInfo.CurrentCulture, $"S.M.A.R.T. – {OverallText} ({SourceText})");
            foreach (var r in Rows)
            {
                text.AppendLine(CultureInfo.CurrentCulture, $"{r.Id}\t{r.Name}\t{r.Value}\t{r.Current}\t{r.Worst}\t{r.Threshold}\t{r.Raw}\t{r.StatusText}");
            }

            return text.ToString();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _nextRefresh = DateTime.Now + Interval;
        if (_selection.Current is not { } drive)
        {
            Rows.Clear();
            return;
        }

        IsLoading = true;
        try
        {
            var result = await Task.Run(() => _smart.Read(drive)).ConfigureAwait(true);
            if (_selection.Current?.Id == drive.Id)
            {
                Show(result);
            }

            OnPropertyChanged(nameof(SelfTestAvailable));
            var available = SelfTestAvailable;
            var status = await Task.Run(() =>
            {
                _history.Append(drive, result);
                return available ? _selfTests.Query(drive) : null;
            }).ConfigureAwait(true);

            if (status is not null)
            {
                ShowSelfTest(status);
            }
            else
            {
                SelfTestRunning = false;
                SelfTestText = drive.Bus == Core.Devices.BusType.Nvme
                    ? "NVMe-Selbsttests werden von DiskScope unter Windows nicht unterstützt."
                    : IsElevated ? "Selbsttests sind nur für ATA-/SATA-Laufwerke verfügbar." : "Selbsttests brauchen Administratorrechte.";
            }
        }
        catch (DiskScopeException ex)
        {
            Rows.Clear();
            Overall = HealthStatus.Unknown;
            OverallText = "Unbekannt";
            Hint = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, _settings.Current.SmartRefreshMinutes));

    private void Show(SmartResult result)
    {
        _result = result;
        Rows.Clear();
        var nvme = result.Source == SmartSource.NvmeLog;
        foreach (var a in result.Attributes)
        {
            Rows.Add(new SmartRow(
                a.Id.ToString("X2", CultureInfo.InvariantCulture),
                a.Name,
                a.Value,
                a.Current?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                a.Worst?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                a.Threshold?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                nvme ? a.RawValue.ToString("N0", CultureInfo.CurrentCulture) : a.Raw,
                a.Status));
        }

        Overall = result.Overall;
        OverallText = HealthTexts.Of(result.Overall);
        SourceText = result.Source switch
        {
            SmartSource.AtaPassThrough => "ATA-Pass-Through",
            SmartSource.Sat => "SAT (USB)",
            SmartSource.LegacyIoctl => "SMART-IOCTL",
            SmartSource.PredictFailure => result.Attributes.Count > 0 ? "ohne Schwellwerte" : "nur Ja/Nein",
            SmartSource.NvmeLog => "NVMe-Health-Log",
            _ => "nicht verfügbar",
        };
        Hint = result.UnsupportedReason;
    }

    private async Task TickAsync()
    {
        // While a self-test runs, its progress is polled every 10 seconds.
        if (SelfTestRunning && DateTime.Now.Second % 10 == 0 && _selection.Current is { } running)
        {
            ShowSelfTest(await Task.Run(() => _selfTests.Query(running)).ConfigureAwait(true));
        }

        var remaining = _nextRefresh - DateTime.Now;
        if (remaining <= TimeSpan.Zero)
        {
            if (!IsLoading && !_selection.IsBusy)
            {
                await RefreshAsync().ConfigureAwait(true);
            }

            return;
        }

        CountdownFraction = 1 - (remaining / Interval);
        CountdownText = $"Nächste Aktualisierung in {remaining:mm\\:ss}";
    }
}
