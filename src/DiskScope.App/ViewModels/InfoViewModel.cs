using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Devices;

namespace DiskScope.App.ViewModels;

public sealed record InfoRow(string Label, string Value);

public sealed record FeatureRow(string Name, bool Supported, string State)
{
    public string Glyph => Supported ? "" : "";
}

public sealed record VolumeRow(string Name, string Capacity, string Free, double UsedPercent, string FileSystem, string Serial, string Alignment)
{
    public string Used => $"{UsedPercent:0.0} %";
}

/// <summary>Info tab: device data, supported features, volumes (SPEC 4.2).</summary>
public sealed partial class InfoViewModel : ObservableObject, IResultText
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    private readonly Core.Destructive.IAcousticPowerService _acoustic;
    private readonly IWriteConfirmation _confirmation;

    public InfoViewModel(DriveSelection selection, Core.Destructive.IAcousticPowerService acoustic, IWriteConfirmation confirmation)
    {
        _acoustic = acoustic;
        _confirmation = confirmation;
        selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DriveSelection.Current))
            {
                Show(selection.Current);
            }
        };
        Show(selection.Current);
    }

    public bool IsElevated { get; } = Privileges.IsElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AcousticVisible))]
    public partial Core.Destructive.AcousticPowerState Acoustic { get; set; } = Core.Destructive.AcousticPowerState.None;

    public bool AcousticVisible => Acoustic.AamSupported || Acoustic.ApmSupported;

    public string AamText => !Acoustic.AamSupported ? "AAM wird nicht unterstützt"
        : Acoustic.AamEnabled ? $"AAM aktiv, Stufe 0x{Acoustic.AamLevel:X2} ({Acoustic.AamLevel}), empfohlen 0x{Acoustic.AamRecommended:X2}" : "AAM aus";

    public string ApmText => !Acoustic.ApmSupported ? "APM wird nicht unterstützt"
        : Acoustic.ApmEnabled ? $"APM aktiv, Stufe 0x{Acoustic.ApmLevel:X2} ({Acoustic.ApmLevel})" : "APM aus";

    public bool CanChangeAam => IsElevated && Acoustic.AamSupported;

    public bool CanChangeApm => IsElevated && Acoustic.ApmSupported;

    /// <summary>0x80 (quiet) … 0xFE (fast).</summary>
    [ObservableProperty]
    public partial double AamLevel { get; set; } = 0xFE;

    /// <summary>0x01 (max. saving, may spin down) … 0xFE (max. performance).</summary>
    [ObservableProperty]
    public partial double ApmLevel { get; set; } = 0x80;

    [ObservableProperty]
    public partial string? AcousticStatus { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task ApplyAamAsync() => ChangeAsync("AAM", $"AAM auf Stufe 0x{(int)AamLevel:X2} setzen?", d => _acoustic.SetAam(d, (int)AamLevel, true));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task DisableAamAsync() => ChangeAsync("AAM", "AAM ausschalten?", d => _acoustic.SetAam(d, null, true));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task ApplyApmAsync() => ChangeAsync("APM", $"APM auf Stufe 0x{(int)ApmLevel:X2} setzen? Stufen unter 0x80 erlauben dem Laufwerk, in den Standby zu gehen.", d => _acoustic.SetApm(d, (int)ApmLevel, true));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task DisableApmAsync() => ChangeAsync("APM", "APM ausschalten?", d => _acoustic.SetApm(d, null, true));

    /// <summary>SPEC 4.14: device setting changes are confirmed in a dialog first.</summary>
    private async Task ChangeAsync(string name, string question, Func<DriveInfo, Core.Destructive.AcousticPowerState> change)
    {
        if (_drive is not { } drive || !await _confirmation.ConfirmSettingAsync($"{name} ändern", $"{drive.Model}: {question}").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            SetAcoustic(await Task.Run(() => change(drive)).ConfigureAwait(true));
            AcousticStatus = $"{name} geändert.";
        }
        catch (Exception ex) when (ex is Core.Destructive.WriteRefusedException or DiskScopeException or ArgumentException)
        {
            AcousticStatus = ex.Message;
        }
    }

    private void SetAcoustic(Core.Destructive.AcousticPowerState state)
    {
        Acoustic = state;
        if (state.AamEnabled)
        {
            AamLevel = state.AamLevel;
        }

        if (state.ApmEnabled)
        {
            ApmLevel = state.ApmLevel;
        }

        OnPropertyChanged(nameof(AamText));
        OnPropertyChanged(nameof(ApmText));
        OnPropertyChanged(nameof(CanChangeAam));
        OnPropertyChanged(nameof(CanChangeApm));
    }

    private async Task LoadAcousticAsync(DriveInfo drive)
    {
        try
        {
            var state = await Task.Run(() => _acoustic.Read(drive)).ConfigureAwait(true);
            if (_drive == drive)
            {
                SetAcoustic(state);
            }
        }
        catch (DiskScopeException)
        {
            SetAcoustic(Core.Destructive.AcousticPowerState.None);
        }
    }

    public ObservableCollection<InfoRow> Device { get; } = [];

    public ObservableCollection<FeatureRow> Features { get; } = [];

    public ObservableCollection<VolumeRow> Volumes { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    public string? ResultJson => _drive is null ? null : System.Text.Json.JsonSerializer.Serialize(_drive, Core.Serialization.DiskScopeJsonContext.Default.DriveInfo);

    public string? ResultText
    {
        get
        {
            if (Device.Count == 0)
            {
                return null;
            }

            var text = new StringBuilder().AppendLine(Title);
            foreach (var row in Device)
            {
                text.AppendLine(Culture, $"{row.Label}: {row.Value}");
            }

            text.AppendLine().AppendLine("Funktionen");
            foreach (var f in Features)
            {
                text.AppendLine(Culture, $"[{(f.Supported ? 'x' : ' ')}] {f.Name} {f.State}");
            }

            text.AppendLine().AppendLine("Volumes");
            foreach (var v in Volumes)
            {
                text.AppendLine(Culture, $"{v.Name}\t{v.Capacity}\t{v.Free}\t{v.Used}\t{v.FileSystem}\t{v.Serial}\t{v.Alignment}");
            }

            return text.ToString();
        }
    }

    private DriveInfo? _drive;

    private void Show(DriveInfo? drive)
    {
        _drive = drive;
        AcousticStatus = null;
        SetAcoustic(Core.Destructive.AcousticPowerState.None);
        if (drive is not null)
        {
            _ = LoadAcousticAsync(drive);
        }

        Device.Clear();
        Features.Clear();
        Volumes.Clear();
        if (drive is null)
        {
            Title = string.Empty;
            return;
        }

        Title = drive.Model;
        Device.Add(new InfoRow("Modell", drive.Model));
        Device.Add(new InfoRow("Hersteller", drive.Vendor ?? "–"));
        Device.Add(new InfoRow("Firmware", drive.Firmware ?? "–"));
        Device.Add(new InfoRow("Seriennummer", drive.Serial ?? "–"));
        Device.Add(new InfoRow("Kapazität", $"{ByteSize.Format(drive.SizeBytes, Culture)} ({drive.SizeBytes.ToString("N0", Culture)} Byte)"));
        Device.Add(new InfoRow("Schnittstelle", drive.Link is null ? DisplayNames.Of(drive.Bus) : $"{DisplayNames.Of(drive.Bus)}, {drive.Link}"));
        Device.Add(new InfoRow("Medium", DisplayNames.Of(drive.Media)));
        Device.Add(new InfoRow("Sektorgröße", $"{drive.LogicalSectorSize} / {drive.PhysicalSectorSize} Byte (logisch / physisch)"));
        Device.Add(new InfoRow("Puffergröße", drive.BufferSize is { } b ? ByteSize.FormatBinary(b, Culture) : "–"));
        Device.Add(new InfoRow("Gerätepfad", drive.DevicePath));
        Device.Add(new InfoRow("Systemlaufwerk", drive.IsSystemDisk ? "ja" : "nein"));

        foreach (var feature in drive.Features)
        {
            var state = feature.Enabled switch
            {
                true => "aktiv",
                false when feature.Supported => "inaktiv",
                _ => string.Empty,
            };
            Features.Add(new FeatureRow(DisplayNames.Of(feature.Kind), feature.Supported, state));
        }

        foreach (var volume in drive.Volumes)
        {
            var label = volume.Label.Length > 0 ? volume.Label : "(ohne Bezeichnung)";
            Volumes.Add(new VolumeRow(
                volume.MountPoint.Length > 0 ? $"{label} ({volume.MountPoint})" : label,
                ByteSize.Format(volume.Capacity, Culture),
                ByteSize.Format(volume.Free, Culture),
                volume.UsedPercent,
                volume.FileSystem,
                volume.Serial,
                $"{(volume.Alignment / 1024).ToString("N0", Culture)} KiB"));
        }
    }
}
