using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;

namespace DiskScope.App.ViewModels;

public sealed record FileRow(string Name, string Read, string ReadIops, string Write, string WriteIops);

/// <summary>File benchmark tab (SPEC 4.8). Works on a volume and without admin rights.</summary>
public sealed partial class FileBenchmarkViewModel : TestViewModelBase
{
    private const double Megabyte = 1e6;

    private readonly FileBenchmarkRunner _runner;
    private FileBenchmarkResult? _result;

    public FileBenchmarkViewModel(FileBenchmarkRunner runner, DriveSelection selection)
        : base(selection)
    {
        _runner = runner;
        Volumes = System.IO.DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is System.IO.DriveType.Fixed or System.IO.DriveType.Removable)
            .Select(d => d.Name)
            .ToArray();
        SelectedVolume = Volumes.FirstOrDefault(v => !v.StartsWith(System.IO.Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase))
            ?? (Volumes.Count > 0 ? Volumes[0] : null);
    }

    public event EventHandler? SamplesChanged;

    public IReadOnlyList<string> Volumes { get; }

    public string[] FileSizes { get; } = ["100M", "500M", "1G", "2G", "4G"];

    public List<double> WriteX { get; } = [];

    public List<double> WriteY { get; } = [];

    public List<double> ReadX { get; } = [];

    public List<double> ReadY { get; } = [];

    public ObservableCollection<FileRow> Rows { get; } = [];

    public IReadOnlyList<BlockSizeRow> BlockSizes => _result?.BlockSizes ?? [];

    [ObservableProperty]
    public partial string? SelectedVolume { get; set; }

    [ObservableProperty]
    public partial int SelectedFileSize { get; set; } = 1;

    [ObservableProperty]
    public partial bool RandomPattern { get; set; } = true;

    [ObservableProperty]
    public partial double QueueDepth { get; set; } = 32;

    [ObservableProperty]
    public partial bool BlockSizeTest { get; set; }

    [ObservableProperty]
    public partial double BlockTestMegabytes { get; set; } = 64;

    [ObservableProperty]
    public partial double BlockPauseSeconds { get; set; } = 0.5;

    [ObservableProperty]
    public partial string Phase { get; set; } = string.Empty;

    public override string? ResultJson => _result is null ? null : System.Text.Json.JsonSerializer.Serialize(_result, Core.Serialization.DiskScopeJsonContext.Default.FileBenchmarkResult);

    public override string? ResultText
    {
        get
        {
            if (_result is null)
            {
                return null;
            }

            var c = CultureInfo.CurrentCulture;
            var text = new StringBuilder()
                .AppendLine(c, $"Datei-Benchmark {_result.Target}, {ByteSize.FormatBinary((ulong)_result.FileSize, c)}, Muster {(_result.Pattern == DataPattern.Random ? "Zufall" : "Nullen")}")
                .AppendLine("Test\tLesen\tIOPS\tSchreiben\tIOPS");
            foreach (var r in Rows)
            {
                text.AppendLine(c, $"{r.Name}\t{r.Read}\t{r.ReadIops}\t{r.Write}\t{r.WriteIops}");
            }

            foreach (var b in BlockSizes)
            {
                text.AppendLine(c, $"{ByteSize.FormatBinary((ulong)b.BlockSize, c)}\t{b.ReadMBps:0.0} MB/s\t{b.WriteMBps:0.0} MB/s");
            }

            return text.ToString();
        }
    }

    protected override bool RequiresElevation => false;

    protected override bool RequiresDrive => false;

    protected override async Task RunAsync(Core.Devices.DriveInfo drive, CancellationToken cancellationToken)
    {
        if (SelectedVolume is null)
        {
            throw new ArgumentException("Kein Ziel-Volume gewählt.");
        }

        _result = null;
        WriteX.Clear();
        WriteY.Clear();
        ReadX.Clear();
        ReadY.Clear();
        Rows.Clear();
        SamplesChanged?.Invoke(this, EventArgs.Empty);

        var options = new FileBenchmarkOptions(
            SelectedVolume,
            ByteSize.ParseBinary(FileSizes[SelectedFileSize]),
            RandomPattern ? DataPattern.Random : DataPattern.Zero,
            (int)Math.Clamp(QueueDepth, 1, 256),
            BlockSizeTest: BlockSizeTest,
            BlockTestFileSize: (long)(Math.Clamp(BlockTestMegabytes, 1, 4096) * 1024 * 1024),
            BlockPause: TimeSpan.FromSeconds(Math.Clamp(BlockPauseSeconds, 0, 30)));

        var progress = new Progress<FileBenchmarkProgress>(p =>
        {
            Progress = p.Fraction * 100;
            Phase = p.Phase switch
            {
                FilePhase.SequentialWrite => "Sequenziell schreiben",
                FilePhase.SequentialRead => "Sequenziell lesen",
                FilePhase.Random4KSingle => "4 KiB zufällig (QD1)",
                FilePhase.Random4KMulti => $"4 KiB zufällig (QD{options.QueueDepth})",
                _ => "Blockgrößen",
            };
            if (p.Sample is { } s)
            {
                (p.IsWrite ? WriteX : ReadX).Add(s.Position / Megabyte);
                (p.IsWrite ? WriteY : ReadY).Add(s.MegabytesPerSecond);
                SamplesChanged?.Invoke(this, EventArgs.Empty);
            }
        });

        _result = await _runner.RunAsync(options, progress, cancellationToken).ConfigureAwait(true);
        foreach (var row in _result.Rows)
        {
            Rows.Add(new FileRow(
                row.Kind switch
                {
                    FileTestKind.Sequential => "Sequenziell",
                    FileTestKind.Random4KSingle => "4 KiB zufällig (QD1)",
                    _ => $"4 KiB zufällig (QD{_result.QueueDepth})",
                },
                Units.Rate(row.ReadMBps),
                row.ReadIops > 0 ? row.ReadIops.ToString("N0", CultureInfo.CurrentCulture) : "–",
                Units.Rate(row.WriteMBps),
                row.WriteIops > 0 ? row.WriteIops.ToString("N0", CultureInfo.CurrentCulture) : "–"));
        }

        Phase = string.Empty;
        SamplesChanged?.Invoke(this, EventArgs.Empty);
    }
}
