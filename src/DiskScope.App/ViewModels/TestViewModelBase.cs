using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;

namespace DiskScope.App.ViewModels;

/// <summary>Results of a tab as plain text for "Ergebnisse kopieren".</summary>
public interface IResultText
{
    string? ResultText { get; }

    /// <summary>The full result as JSON for export (SPEC 4.15), or null.</summary>
    string? ResultJson { get; }
}

/// <summary>
/// Common behaviour of the raw-device test tabs: start/stop with cancellation, the global test lock,
/// the "Bereich" option and status text (SPEC 4.1, 6, 7).
/// </summary>
public abstract partial class TestViewModelBase : ObservableObject, IResultText
{
    private CancellationTokenSource? _cancellation;

    protected TestViewModelBase(DriveSelection selection)
    {
        Selection = selection;
        selection.PropertyChanged += OnSelectionChanged;
    }

    /// <summary>Raised after a run finished successfully (not when cancelled or failed).</summary>
    public event EventHandler? Completed;

    public DriveSelection Selection { get; }

    public bool IsElevated { get; } = Privileges.IsElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText), nameof(CanStartStop), nameof(OptionsEnabled))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    public partial bool UseRange { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    public partial double RangeStart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    public partial double RangeEnd { get; set; } = 100;

    /// <summary>True: start/end in %, false: in GB.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    public partial bool RangeInPercent { get; set; } = true;

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>Write instead of read (SPEC 4.4, 4.10, 4.11 "schreiben"); guarded by SPEC 9.</summary>
    [ObservableProperty]
    public partial bool WriteMode { get; set; }

    /// <summary>Lock and dismount the drive's volumes before writing (SPEC 9.3).</summary>
    [ObservableProperty]
    public partial bool DismountVolumes { get; set; }

    /// <summary>Set by tabs that support writing.</summary>
    protected WriteSupport? Writes { get; init; }

    public bool CanWrite => Writes is not null && IsElevated;

    public string StartStopText => IsRunning ? "Stopp" : StartText;

    protected virtual string StartText => "Start";

    public bool CanStartStop =>
        IsRunning || ((IsElevated || !RequiresElevation) && (Selection.IsIdle || !Exclusive) && (Selection.Current is not null || !RequiresDrive));

    /// <summary>Raw-device tests need admin rights; the file benchmark does not (SPEC 1).</summary>
    protected virtual bool RequiresElevation => true;

    protected virtual bool RequiresDrive => true;

    /// <summary>Raw tests lock drive switching and other tests; passive tools like the monitor do not.</summary>
    protected virtual bool Exclusive => true;

    /// <summary>Cancels a running operation, e.g. when the drive changes.</summary>
    protected void CancelRun() => _cancellation?.Cancel();

    public bool OptionsEnabled => !IsRunning;

    public string RangeText => UseRange
        ? $"{RangeStart:0.##}–{RangeEnd:0.##} {(RangeInPercent ? "%" : "GB")}"
        : "Ganzes Laufwerk";

    public abstract string? ResultText { get; }

    public virtual string? ResultJson => null;

    // Concurrent execution: the second invocation is the "Stopp" click while the test runs.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartStopAsync()
    {
        if (IsRunning)
        {
            await _cancellation!.CancelAsync().ConfigureAwait(true);
            return;
        }

        var drive = Selection.Current;
        if (drive is null && RequiresDrive)
        {
            return;
        }

        using var lease = Exclusive ? Selection.BeginTest() : null;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsRunning = true;
        Progress = 0;
        Status = "Läuft …";
        try
        {
            await RunAsync(drive!, cancellation.Token).ConfigureAwait(true);
            Status = $"Fertig um {DateTime.Now:T}";
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Abgebrochen";
        }
        catch (Core.Destructive.WriteRefusedException ex)
        {
            Status = "Gesperrt: " + string.Join(" ", ex.Reasons);
        }
        catch (Exception ex) when (ex is DiskScopeException or ArgumentException or System.IO.IOException)
        {
            Status = $"Fehler: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cancellation = null;
        }
    }

    protected abstract Task RunAsync(DriveInfo drive, CancellationToken cancellationToken);

    /// <summary>Asks for confirmation and authorization; throws <see cref="OperationCanceledException"/> if declined.</summary>
    protected async Task<Core.Destructive.WriteAuthorization> AuthorizeWriteAsync(DriveInfo drive, string operation)
    {
        var authorization = Writes is null
            ? null
            : await Writes.AuthorizeAsync(drive, BuildRange(drive), DismountVolumes, operation).ConfigureAwait(true);
        return authorization ?? throw new OperationCanceledException();
    }

    /// <summary>The selected range, or null for the whole drive.</summary>
    protected DriveRange? BuildRange(DriveInfo drive)
    {
        if (!UseRange)
        {
            return null;
        }

        return RangeInPercent
            ? DriveRange.FromPercent(drive.SizeBytes, RangeStart, RangeEnd)
            : DriveRange.Parse(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{RangeStart}GB-{RangeEnd}GB"), drive.SizeBytes);
    }

    /// <summary>Called when another drive is selected; clears old results.</summary>
    protected virtual void OnDriveChanged(DriveInfo? drive)
    {
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DriveSelection.Current))
        {
            Status = string.Empty;
            OnDriveChanged(Selection.Current);
        }

        OnPropertyChanged(nameof(CanStartStop));
    }
}
