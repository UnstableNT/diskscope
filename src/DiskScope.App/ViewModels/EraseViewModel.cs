using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Destructive;

namespace DiskScope.App.ViewModels;

/// <summary>"Löschen" tab (SPEC 4.7): overwrite a drive or range under the rules of SPEC 9.</summary>
public sealed partial class EraseViewModel : TestViewModelBase
{
    private EraseResult? _result;

    public EraseViewModel(DriveSelection selection, WriteSupport writes)
        : base(selection)
    {
        Writes = writes;
        WriteMode = true;
    }

    public event EventHandler? BlocksChanged;

    public int GridCapacity { get; set; } = 2000;

    public EraseBlockState[] Blocks { get; private set; } = [];

    /// <summary>0 = Nullen, 1 = Zufallsdaten, 2 = festes Byte.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesFixedByte))]
    public partial int PatternIndex { get; set; }

    [ObservableProperty]
    public partial string FixedByte { get; set; } = "AA";

    [ObservableProperty]
    public partial double Passes { get; set; } = 1;

    [ObservableProperty]
    public partial bool Verify { get; set; }

    [ObservableProperty]
    public partial string Pass { get; set; } = "–";

    [ObservableProperty]
    public partial string Speed { get; set; } = "–";

    [ObservableProperty]
    public partial string Errors { get; set; } = "–";

    [ObservableProperty]
    public partial string Position { get; set; } = "–";

    public bool UsesFixedByte => PatternIndex == 2;

    protected override string StartText => "Löschen …";

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
                .AppendLine(c, $"Löschen {Selection.Current?.Model}: {ByteSize.Format(r.BytesWritten, c)} geschrieben")
                .AppendLine(c, $"Muster {r.Pattern}, {r.Passes} Durchgang/Durchgänge, Prüflesen {(r.Verified ? "ja" : "nein")}, Fehler {r.Errors}")
                .ToString();
        }
    }

    public override string? ResultJson => _result is null
        ? null
        : System.Text.Json.JsonSerializer.Serialize(_result, Core.Serialization.DiskScopeJsonContext.Default.EraseResult);

    [RelayCommand]
    private async Task DryRunAsync()
    {
        if (Selection.Current is not { } drive || Writes is null)
        {
            return;
        }

        try
        {
            await Writes.ShowPlanAsync(Writes.Plan(drive, BuildRange(drive), DismountVolumes), "Löschen").ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DiskScopeException or ArgumentException)
        {
            Status = $"Fehler: {ex.Message}";
        }
    }

    protected override async Task RunAsync(Core.Devices.DriveInfo drive, CancellationToken cancellationToken)
    {
        var options = new EraseOptions(
            (ErasePattern)PatternIndex,
            PatternIndex == 2 && byte.TryParse(FixedByte.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)
                ? b
                : PatternIndex == 2 ? throw new ArgumentException($"Ungültiges Füllbyte „{FixedByte}“ (hexadezimal, z. B. AA).") : (byte)0,
            (int)Math.Clamp(Passes, 1, 35),
            Verify,
            Math.Max(1, GridCapacity));

        var authorization = await AuthorizeWriteAsync(drive, "Löschen").ConfigureAwait(true);
        _result = null;
        Blocks = new EraseBlockState[options.BlockCount];
        BlocksChanged?.Invoke(this, EventArgs.Empty);

        var progress = new Progress<EraseProgress>(p =>
        {
            if (p.Block == 0)
            {
                Array.Clear(Blocks);
            }

            if (p.Block < Blocks.Length)
            {
                Blocks[p.Block] = p.State;
            }

            Progress = p.Fraction * 100;
            Pass = $"{p.Pass} / {p.Passes}";
            Speed = Units.Rate(p.MegabytesPerSecond);
            Errors = p.Errors.ToString(CultureInfo.CurrentCulture);
            Position = ByteSize.Format(p.Position, CultureInfo.CurrentCulture);
            BlocksChanged?.Invoke(this, EventArgs.Empty);
        });

        _result = await Writes!.Operations.EraseAsync(authorization, options, progress, cancellationToken).ConfigureAwait(true);
        Blocks = [.. _result.Blocks];
        BlocksChanged?.Invoke(this, EventArgs.Empty);
        Status = _result.Errors > 0 ? $"{_result.Errors} Fehler beim Schreiben/Prüflesen" : $"{ByteSize.Format(_result.BytesWritten, CultureInfo.CurrentCulture)} überschrieben";
    }

    protected override void OnDriveChanged(Core.Devices.DriveInfo? drive)
    {
        _result = null;
        Blocks = [];
        Pass = Speed = Errors = Position = "–";
        BlocksChanged?.Invoke(this, EventArgs.Empty);
    }
}
