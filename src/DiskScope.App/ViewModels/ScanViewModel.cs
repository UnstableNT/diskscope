using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Devices;
using DiskScope.Core.Scanning;

namespace DiskScope.App.ViewModels;

/// <summary>Error scan tab: block grid, damaged blocks, speed, position, export (SPEC 4.5).</summary>
public sealed partial class ScanViewModel : TestViewModelBase
{
    private readonly ErrorScanner _scanner;

    public ScanViewModel(ErrorScanner scanner, DriveSelection selection)
        : base(selection)
    {
        _scanner = scanner;
    }

    /// <summary>Raised when blocks changed; the grid redraws (throttled).</summary>
    public event EventHandler? BlocksChanged;

    /// <summary>Block count the grid can show; set by the view from its size before a scan.</summary>
    public int GridCapacity { get; set; } = 2000;

    public BlockState[] Blocks { get; private set; } = [];

    public double[] Speeds { get; private set; } = [];

    public double MaxSpeed { get; private set; }

    public ScanResult? Result { get; private set; }

    [ObservableProperty]
    public partial bool Quick { get; set; }

    [ObservableProperty]
    public partial bool SpeedMap { get; set; }

    [ObservableProperty]
    public partial string Damaged { get; set; } = "–";

    [ObservableProperty]
    public partial string Speed { get; set; } = "–";

    [ObservableProperty]
    public partial string Position { get; set; } = "–";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial bool HasResult { get; set; }

    public bool CanExport => HasResult && !IsRunning;

    public override string? ResultJson => Result is null ? null : System.Text.Json.JsonSerializer.Serialize(Result, Core.Serialization.DiskScopeJsonContext.Default.ScanResult);

    public override string? ResultText
    {
        get
        {
            if (Result is not { } r)
            {
                return null;
            }

            var c = CultureInfo.CurrentCulture;
            return new StringBuilder()
                .AppendLine(c, $"Fehlerscan {Selection.Current?.Model}{(r.Quick ? " (Schnellscan)" : string.Empty)}")
                .AppendLine(c, $"Blöcke: {r.Blocks.Count} à {ByteSize.FormatBinary(r.BlockSize, c)}")
                .AppendLine(c, $"Beschädigt: {Damaged}")
                .AppendLine(c, $"Defekte LBAs: {r.BadLbas.Count}")
                .AppendLine(c, $"Geschwindigkeit: {Speed}")
                .ToString();
        }
    }

    protected override async Task RunAsync(DriveInfo drive, CancellationToken cancellationToken)
    {
        Result = null;
        HasResult = false;
        var options = new ScanOptions(BuildRange(drive), Math.Max(1, GridCapacity), Quick, SpeedMap: SpeedMap);

        // The scanner may reduce the count for tiny ranges; size the arrays generously and trim afterwards.
        Blocks = new BlockState[options.BlockCount];
        Speeds = new double[options.BlockCount];
        MaxSpeed = 0;
        Damaged = "0 (0 %)";
        BlocksChanged?.Invoke(this, EventArgs.Empty);

        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Block >= Blocks.Length)
            {
                return;
            }

            Blocks[p.Block] = p.State;
            if (p.MegabytesPerSecond is { } mbps)
            {
                Speeds[p.Block] = mbps;
                MaxSpeed = Math.Max(MaxSpeed, mbps);
                Speed = Units.Rate(mbps);
            }

            Progress = p.Fraction * 100;
            Position = ByteSize.Format(p.Position, CultureInfo.CurrentCulture);
            Damaged = $"{p.DamagedBlocks} ({p.DamagedBlocks * 100.0 / Blocks.Length:0.##} %)";
            BlocksChanged?.Invoke(this, EventArgs.Empty);
        });

        try
        {
            var result = await _scanner.ScanAsync(drive, options, progress, cancellationToken).ConfigureAwait(true);
            Result = result;
            Blocks = [.. result.Blocks];
            Damaged = $"{result.DamagedBlocks} ({result.DamagedPercent:0.###} %)";
            Speed = Units.Rate(result.AverageMBps);
            HasResult = true;
            if (result.BadLbas.Count > 0)
            {
                Status = $"{result.BadLbas.Count} defekte LBAs gefunden";
            }
        }
        finally
        {
            BlocksChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(CanExport));
        }
    }

    protected override void OnDriveChanged(DriveInfo? drive)
    {
        Result = null;
        HasResult = false;
        Blocks = [];
        Speeds = [];
        Damaged = Speed = Position = "–";
        BlocksChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnHasResultChanged(bool value) => OnPropertyChanged(nameof(CanExport));
}
