using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;

namespace DiskScope.App.ViewModels;

/// <summary>Benchmark tab: transfer rate over position, access times, burst rate (SPEC 4.4).</summary>
public sealed partial class BenchmarkViewModel : TestViewModelBase
{
    private const double Gigabyte = 1e9;

    private readonly BenchmarkRunner _runner;
    private BenchmarkResult? _result;

    public BenchmarkViewModel(BenchmarkRunner runner, SettingsService settings, DriveSelection selection, WriteSupport writes)
        : base(selection)
    {
        _runner = runner;
        Writes = writes;
        var configured = Array.IndexOf(BlockSizes, settings.Current.BenchmarkBlockSize);
        SelectedBlockSize = configured >= 0 ? configured : Array.IndexOf(BlockSizes, "64K");
    }

    /// <summary>Raised when chart data changed; the view redraws (throttled).</summary>
    public event EventHandler? SamplesChanged;

    public string[] BlockSizes { get; } =
        ["512", "1K", "2K", "4K", "8K", "16K", "32K", "64K", "128K", "256K", "512K", "1M", "2M", "4M", "8M"];

    public List<double> TransferX { get; } = [];

    public List<double> TransferY { get; } = [];

    public List<double> AccessX { get; } = [];

    public List<double> AccessY { get; } = [];

    public double DriveGigabytes => (Selection.Current?.SizeBytes ?? 0) / Gigabyte;

    [ObservableProperty]
    public partial int SelectedBlockSize { get; set; }

    [ObservableProperty]
    public partial bool MeasureTransfer { get; set; } = true;

    [ObservableProperty]
    public partial bool MeasureAccess { get; set; } = true;

    [ObservableProperty]
    public partial bool MeasureBurst { get; set; } = true;

    [ObservableProperty]
    public partial string Minimum { get; set; } = "–";

    [ObservableProperty]
    public partial string Maximum { get; set; } = "–";

    [ObservableProperty]
    public partial string Average { get; set; } = "–";

    [ObservableProperty]
    public partial string AccessTime { get; set; } = "–";

    [ObservableProperty]
    public partial string Burst { get; set; } = "–";

    [ObservableProperty]
    public partial string Cpu { get; set; } = "–";

    public override string? ResultJson => _result is null ? null : System.Text.Json.JsonSerializer.Serialize(_result, Core.Serialization.DiskScopeJsonContext.Default.BenchmarkResult);

    public override string? ResultText
    {
        get
        {
            if (_result is not { } r)
            {
                return null;
            }

            var c = CultureInfo.CurrentCulture;
            return new StringBuilder()
                .AppendLine(c, $"Benchmark {Selection.Current?.Model}, Block {ByteSize.FormatBinary((ulong)r.BlockSize, c)}")
                .AppendLine(c, $"Minimum: {Minimum}")
                .AppendLine(c, $"Maximum: {Maximum}")
                .AppendLine(c, $"Durchschnitt: {Average}")
                .AppendLine(c, $"Zugriffszeit: {AccessTime}")
                .AppendLine(c, $"Burst-Rate: {Burst}")
                .AppendLine(c, $"CPU-Last: {Cpu}")
                .ToString();
        }
    }

    protected override async Task RunAsync(DriveInfo drive, CancellationToken cancellationToken)
    {
        Clear();
        var options = new BenchmarkOptions(
            BlockSize: BlockSize.Parse(BlockSizes[SelectedBlockSize]),
            Range: BuildRange(drive),
            MeasureTransfer: MeasureTransfer,
            MeasureAccessTime: MeasureAccess,
            MeasureBurst: MeasureBurst);

        var progress = new Progress<BenchmarkProgress>(p =>
        {
            Progress = p.Fraction * 100;
            if (p.Transfer is { } t)
            {
                TransferX.Add(t.Position / Gigabyte);
                TransferY.Add(t.MegabytesPerSecond);
                UpdateLiveTiles();
            }

            if (p.Access is { } a)
            {
                AccessX.Add(a.Position / Gigabyte);
                AccessY.Add(a.Milliseconds);
            }

            SamplesChanged?.Invoke(this, EventArgs.Empty);
        });

        _result = WriteMode
            ? await Writes!.Operations.WriteBenchmarkAsync(await AuthorizeWriteAsync(drive, "Schreib-Benchmark").ConfigureAwait(true), options, progress, cancellationToken).ConfigureAwait(true)
            : await _runner.RunAsync(drive, options, progress, cancellationToken).ConfigureAwait(true);
        Minimum = Units.Rate(_result.MinimumMBps);
        Maximum = Units.Rate(_result.MaximumMBps);
        Average = Units.Rate(_result.AverageMBps);
        AccessTime = Units.Milliseconds(_result.AccessTimeMs);
        Burst = Units.Rate(_result.BurstMBps);
        Cpu = $"{_result.CpuPercent:0.0} %";
        if (_result.ReadErrors > 0)
        {
            Status = $"{_result.ReadErrors} Lesefehler";
        }
    }

    protected override void OnDriveChanged(DriveInfo? drive)
    {
        Clear();
        OnPropertyChanged(nameof(DriveGigabytes));
    }

    private void UpdateLiveTiles()
    {
        Minimum = Units.Rate(TransferY.Min());
        Maximum = Units.Rate(TransferY.Max());
        Average = Units.Rate(TransferY.Average());
    }

    private void Clear()
    {
        _result = null;
        TransferX.Clear();
        TransferY.Clear();
        AccessX.Clear();
        AccessY.Clear();
        Minimum = Maximum = Average = AccessTime = Burst = Cpu = "–";
        Progress = 0;
        SamplesChanged?.Invoke(this, EventArgs.Empty);
    }
}
