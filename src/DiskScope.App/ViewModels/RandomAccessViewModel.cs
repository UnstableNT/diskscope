using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;

namespace DiskScope.App.ViewModels;

/// <summary>Scatter points of one transfer size.</summary>
public sealed class RandomSeries(int size, string label, string color)
{
    public int Size { get; } = size;

    public string Label { get; } = label;

    public string Color { get; } = color;

    public List<double> X { get; } = [];

    public List<double> Y { get; } = [];
}

public sealed record RandomRow(string Size, string Color, string Iops, string AverageMs, string MaximumMs, string Speed);

/// <summary>Random access tab: scatter plot access time over position, table per size (SPEC 4.10).</summary>
public sealed partial class RandomAccessViewModel : TestViewModelBase
{
    private static readonly (int Size, string Label, string Color)[] AllSizes =
    [
        (512, "512 B", "#4CC2FF"),
        (4096, "4 KiB", "#6CCB5F"),
        (65536, "64 KiB", "#FFB900"),
        (1024 * 1024, "1 MiB", "#E86BD8"),
        (RandomAccessOptions.Mixed, "zufällig", "#FF8C64"),
    ];

    private readonly RandomAccessRunner _runner;
    private RandomAccessResult? _result;

    public RandomAccessViewModel(RandomAccessRunner runner, DriveSelection selection, WriteSupport writes)
        : base(selection)
    {
        _runner = runner;
        Writes = writes;
    }

    public event EventHandler? SamplesChanged;

    public List<RandomSeries> Series { get; } = [];

    public ObservableCollection<RandomRow> Rows { get; } = [];

    [ObservableProperty]
    public partial bool Use512 { get; set; } = true;

    [ObservableProperty]
    public partial bool Use4K { get; set; } = true;

    [ObservableProperty]
    public partial bool Use64K { get; set; } = true;

    [ObservableProperty]
    public partial bool Use1M { get; set; } = true;

    [ObservableProperty]
    public partial bool UseMixed { get; set; } = true;

    [ObservableProperty]
    public partial bool Align4K { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; } = 3;

    public override string? ResultJson => _result is null ? null : System.Text.Json.JsonSerializer.Serialize(_result, Core.Serialization.DiskScopeJsonContext.Default.RandomAccessResult);

    public override string? ResultText
    {
        get
        {
            if (_result is null)
            {
                return null;
            }

            var text = new StringBuilder().AppendLine(CultureInfo.CurrentCulture, $"Zufallszugriff {Selection.Current?.Model}{(Align4K ? ", 4K-ausgerichtet" : string.Empty)}");
            text.AppendLine("Größe\tIOPS\tMittel\tMax\tMB/s");
            foreach (var r in Rows)
            {
                text.AppendLine(CultureInfo.CurrentCulture, $"{r.Size}\t{r.Iops}\t{r.AverageMs}\t{r.MaximumMs}\t{r.Speed}");
            }

            return text.ToString();
        }
    }

    protected override async Task RunAsync(DriveInfo drive, CancellationToken cancellationToken)
    {
        Clear();
        bool[] chosen = [Use512, Use4K, Use64K, Use1M, UseMixed];
        var sizes = AllSizes.Where((_, i) => chosen[i]).ToArray();
        if (sizes.Length == 0)
        {
            throw new ArgumentException("Mindestens eine Transfergröße wählen.");
        }

        foreach (var (size, label, color) in sizes)
        {
            Series.Add(new RandomSeries(size, label, color));
        }

        var options = new RandomAccessOptions(
            sizes.Select(s => s.Size).ToArray(),
            Align4K,
            BuildRange(drive),
            TimeSpan.FromSeconds(Math.Clamp(DurationSeconds, 1, 60)));

        var pending = 0;
        var progress = new Progress<RandomAccessProgress>(p =>
        {
            Progress = p.Fraction * 100;
            if (p.Sample is { } s && Series.FirstOrDefault(x => x.Size == p.Size) is { } series)
            {
                series.X.Add(s.Position / 1e9);
                series.Y.Add(s.Milliseconds);
                if (++pending >= 25)
                {
                    pending = 0;
                    SamplesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        });

        _result = WriteMode
            ? await Writes!.Operations.WriteRandomAccessAsync(await AuthorizeWriteAsync(drive, "Zufallszugriff schreibend").ConfigureAwait(true), options, progress, cancellationToken).ConfigureAwait(true)
            : await _runner.RunAsync(drive, options, progress, cancellationToken).ConfigureAwait(true);
        foreach (var s in _result.Sizes)
        {
            var meta = AllSizes.First(x => x.Size == s.Size);
            Rows.Add(new RandomRow(
                meta.Label,
                meta.Color,
                s.OperationsPerSecond.ToString("N0", CultureInfo.CurrentCulture),
                Units.Milliseconds(s.AverageMs),
                Units.Milliseconds(s.MaximumMs),
                Units.Rate(s.AverageMBps)));
        }

        SamplesChanged?.Invoke(this, EventArgs.Empty);
        if (_result.ReadErrors > 0)
        {
            Status = $"{_result.ReadErrors} Lesefehler";
        }
    }

    protected override void OnDriveChanged(DriveInfo? drive) => Clear();

    private void Clear()
    {
        _result = null;
        Series.Clear();
        Rows.Clear();
        Progress = 0;
        SamplesChanged?.Invoke(this, EventArgs.Empty);
    }
}
