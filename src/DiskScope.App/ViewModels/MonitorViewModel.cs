using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Monitoring;
using Microsoft.UI.Dispatching;

namespace DiskScope.App.ViewModels;

public sealed record ProcessRow(string Name, string Pid, string Read, string Write, string ReadCount, string WriteCount);

public sealed record StatisticRow(string Label, string Read, string Write);

/// <summary>Monitor tab: live disk I/O from ETW (SPEC 4.9). Passive, so it does not lock other tests.</summary>
public sealed partial class MonitorViewModel : TestViewModelBase
{
    private readonly DiskMonitor _monitor;
    private readonly DispatcherQueueTimer _timer;
    private MonitorStatistics? _statistics;

    public MonitorViewModel(DiskMonitor monitor, DriveSelection selection)
        : base(selection)
    {
        _monitor = monitor;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Refresh();
    }

    public event EventHandler? SnapshotChanged;

    public MonitorSnapshot? Snapshot { get; private set; }

    public ObservableCollection<ProcessRow> Processes { get; } = [];

    public ObservableCollection<StatisticRow> Statistics { get; } = [];

    [ObservableProperty]
    public partial double WindowMinutes { get; set; } = 30;

    [ObservableProperty]
    public partial string Elapsed { get; set; } = "00:00:00";

    [ObservableProperty]
    public partial string ReadCurrent { get; set; } = "–";

    [ObservableProperty]
    public partial string ReadMaximum { get; set; } = "–";

    [ObservableProperty]
    public partial string ReadCount { get; set; } = "–";

    [ObservableProperty]
    public partial string ReadTotal { get; set; } = "–";

    [ObservableProperty]
    public partial string WriteCurrent { get; set; } = "–";

    [ObservableProperty]
    public partial string WriteMaximum { get; set; } = "–";

    [ObservableProperty]
    public partial string WriteCount { get; set; } = "–";

    [ObservableProperty]
    public partial string WriteTotal { get; set; } = "–";

    public override string? ResultJson => Snapshot is null ? null : System.Text.Json.JsonSerializer.Serialize(Snapshot, Core.Serialization.DiskScopeJsonContext.Default.MonitorSnapshot);

    public override string? ResultText
    {
        get
        {
            if (Snapshot is null)
            {
                return null;
            }

            var c = CultureInfo.CurrentCulture;
            var text = new StringBuilder().AppendLine(c, $"Monitor {Selection.Current?.Model}, {Elapsed}").AppendLine("\tLesen\tSchreiben");
            foreach (var s in Statistics)
            {
                text.AppendLine(c, $"{s.Label}\t{s.Read}\t{s.Write}");
            }

            text.AppendLine().AppendLine("Programm\tPID\tGelesen\tGeschrieben");
            foreach (var p in Processes)
            {
                text.AppendLine(c, $"{p.Name}\t{p.Pid}\t{p.Read}\t{p.Write}");
            }

            return text.ToString();
        }
    }

    protected override bool Exclusive => false;

    protected override async Task RunAsync(Core.Devices.DriveInfo drive, CancellationToken cancellationToken)
    {
        _statistics = new MonitorStatistics((long)drive.SizeBytes, TimeSpan.FromMinutes(Math.Clamp(WindowMinutes, 1, 240)), DateTime.Now);
        Snapshot = null;
        Status = "Zeichnet auf …";
        _timer.Start();
        try
        {
            await _monitor.RunAsync(drive.Id.Number, _statistics, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _timer.Stop();
            Refresh();
        }
    }

    protected override void OnDriveChanged(Core.Devices.DriveInfo? drive) => CancelRun();

    private void Refresh()
    {
        if (_statistics is null)
        {
            return;
        }

        var s = _statistics.Snapshot(DateTime.Now);
        Snapshot = s;
        var c = CultureInfo.CurrentCulture;
        Elapsed = s.Elapsed.ToString(@"hh\:mm\:ss", c);
        ReadCurrent = $"{s.Read.CurrentKBps:N0} KB/s";
        ReadMaximum = $"{s.Read.MaximumKBps:N0} KB/s";
        ReadCount = s.Read.Count.ToString("N0", c);
        ReadTotal = ByteSize.Format((ulong)s.Read.Bytes, c);
        WriteCurrent = $"{s.Write.CurrentKBps:N0} KB/s";
        WriteMaximum = $"{s.Write.MaximumKBps:N0} KB/s";
        WriteCount = s.Write.Count.ToString("N0", c);
        WriteTotal = ByteSize.Format((ulong)s.Write.Bytes, c);

        Processes.Clear();
        foreach (var p in s.Processes.Take(100))
        {
            Processes.Add(new ProcessRow(
                p.Name,
                p.ProcessId.ToString(c),
                ByteSize.Format((ulong)p.ReadBytes, c),
                ByteSize.Format((ulong)p.WriteBytes, c),
                p.ReadCount.ToString("N0", c),
                p.WriteCount.ToString("N0", c)));
        }

        var seconds = Math.Max(1, s.Elapsed.TotalSeconds);
        Statistics.Clear();
        Statistics.Add(new StatisticRow("Datenmenge", ReadTotal, WriteTotal));
        Statistics.Add(new StatisticRow("Anfragen", ReadCount, WriteCount));
        Statistics.Add(new StatisticRow("Ø Rate", $"{s.Read.Bytes / 1024.0 / seconds:N1} KB/s", $"{s.Write.Bytes / 1024.0 / seconds:N1} KB/s"));
        Statistics.Add(new StatisticRow("Spitzenrate", ReadMaximum, WriteMaximum));
        Statistics.Add(new StatisticRow("Ø Anfragegröße", ByteSize.FormatBinary((ulong)s.Read.AverageRequestBytes, c), ByteSize.FormatBinary((ulong)s.Write.AverageRequestBytes, c)));
        Statistics.Add(new StatisticRow("Spitze I/O pro Sekunde", s.PeakReadIops.ToString("N0", c), s.PeakWriteIops.ToString("N0", c)));
        var small = s.Sizes.Where(b => b.MaxBytes <= MonitorStatistics.SmallRequestLimit).ToArray();
        var large = s.Sizes.Where(b => b.MaxBytes > MonitorStatistics.SmallRequestLimit).ToArray();
        Statistics.Add(new StatisticRow("Anfragen ≤ 64 KiB", small.Sum(b => b.ReadCount).ToString("N0", c), small.Sum(b => b.WriteCount).ToString("N0", c)));
        Statistics.Add(new StatisticRow("Anfragen > 64 KiB", large.Sum(b => b.ReadCount).ToString("N0", c), large.Sum(b => b.WriteCount).ToString("N0", c)));
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }
}
