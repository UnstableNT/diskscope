using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;

namespace DiskScope.App.ViewModels;

/// <summary>A row of the extra tests list: selection checkbox and result columns.</summary>
public sealed partial class ExtraRow(ExtraTestKind kind, string name) : ObservableObject
{
    public ExtraTestKind Kind { get; } = kind;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    [ObservableProperty]
    public partial string Iops { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Time { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Transfer { get; set; } = string.Empty;
}

/// <summary>Extra tests tab (SPEC 4.11), read-only.</summary>
public sealed partial class ExtraTestsViewModel : TestViewModelBase
{
    private readonly ExtraTestRunner _runner;
    private ExtraTestResult? _result;

    public ExtraTestsViewModel(ExtraTestRunner runner, DriveSelection selection, WriteSupport writes)
        : base(selection)
    {
        _runner = runner;
        Writes = writes;
        Rows =
        [
            new(ExtraTestKind.RandomSector, "Zufällige Positionierung (Sektorgröße)"),
            new(ExtraTestKind.Random4K, "Zufällige Positionierung (4 KiB)"),
            new(ExtraTestKind.Random64K, "Zufällige Positionierung (64 KiB)"),
            new(ExtraTestKind.Random8M, "Zufällige Positionierung (8 MiB)"),
            new(ExtraTestKind.Butterfly, "Butterfly-Positionierung"),
            new(ExtraTestKind.SequentialOuter, "Sequenziell außen"),
            new(ExtraTestKind.SequentialMiddle, "Sequenziell Mitte"),
            new(ExtraTestKind.SequentialInner, "Sequenziell innen"),
            new(ExtraTestKind.Burst, "Burst-Rate"),
        ];
    }

    public event EventHandler? CacheChanged;

    public ObservableCollection<ExtraRow> Rows { get; }

    public List<double> CacheX { get; } = [];

    public List<double> CacheY { get; } = [];

    [ObservableProperty]
    public partial bool Align4K { get; set; }

    [ObservableProperty]
    public partial bool CacheTest { get; set; }

    public override string? ResultJson => _result is null ? null : System.Text.Json.JsonSerializer.Serialize(_result, Core.Serialization.DiskScopeJsonContext.Default.ExtraTestResult);

    public override string? ResultText
    {
        get
        {
            if (_result is null)
            {
                return null;
            }

            var c = CultureInfo.CurrentCulture;
            var text = new StringBuilder().AppendLine(c, $"Zusatztests {Selection.Current?.Model}").AppendLine("Test\tI/O/s\tZeit\tMB/s");
            foreach (var r in Rows.Where(r => r.Iops.Length > 0))
            {
                text.AppendLine(c, $"{r.Name}\t{r.Iops}\t{r.Time}\t{r.Transfer}");
            }

            for (var i = 0; i < CacheX.Count; i++)
            {
                text.AppendLine(c, $"Cache {CacheX[i]:0.#} MiB\t{CacheY[i]:0.0} MB/s");
            }

            return text.ToString();
        }
    }

    protected override async Task RunAsync(Core.Devices.DriveInfo drive, CancellationToken cancellationToken)
    {
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Kind).ToArray();
        if (selected.Length == 0 && !CacheTest)
        {
            throw new ArgumentException("Mindestens einen Test auswählen.");
        }

        Clear();
        var options = new ExtraTestOptions(selected.Length > 0 ? selected : [ExtraTestKind.Burst], Align4K, BuildRange(drive), CacheTest: CacheTest);
        var progress = new Progress<ExtraTestProgress>(p =>
        {
            Progress = p.Fraction * 100;
            if (p.Row is { } row && Rows.FirstOrDefault(r => r.Kind == row.Kind) is { } target)
            {
                target.Iops = row.Iops.ToString("N0", CultureInfo.CurrentCulture);
                target.Time = Units.Milliseconds(row.AverageMs);
                target.Transfer = Units.Rate(row.MBps);
            }

            if (p.Cache is { } point)
            {
                CacheX.Add(point.RegionBytes / 1048576.0);
                CacheY.Add(point.MBps);
                CacheChanged?.Invoke(this, EventArgs.Empty);
            }
        });

        _result = WriteMode
            ? await Writes!.Operations.WriteExtraTestsAsync(await AuthorizeWriteAsync(drive, "Zusatztests schreibend").ConfigureAwait(true), options, progress, cancellationToken).ConfigureAwait(true)
            : await _runner.RunAsync(drive, options, progress, cancellationToken).ConfigureAwait(true);
        if (_result.ReadErrors > 0)
        {
            Status = $"{_result.ReadErrors} Lesefehler";
        }
    }

    protected override void OnDriveChanged(Core.Devices.DriveInfo? drive) => Clear();

    private void Clear()
    {
        _result = null;
        foreach (var row in Rows)
        {
            row.Iops = row.Time = row.Transfer = string.Empty;
        }

        CacheX.Clear();
        CacheY.Clear();
        Progress = 0;
        CacheChanged?.Invoke(this, EventArgs.Empty);
    }
}
