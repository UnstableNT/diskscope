namespace DiskScope.Core.Devices;

/// <summary>Identifies a physical drive by its <c>\\.\PhysicalDriveN</c> number.</summary>
public readonly record struct DriveId(int Number)
{
    public string DevicePath => $@"\\.\PhysicalDrive{Number}";

    public override string ToString() => Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public enum BusType { Sata, Nvme, Usb, Sas, Scsi, Raid, Virtual, Other }

public enum MediaKind { Hdd, Ssd, Unknown }

/// <summary>Drive capabilities shown in the Info tab (SPEC 4.2).</summary>
public enum FeatureKind
{
    Smart,
    FirmwareUpdate,
    PowerManagement,
    AdvancedPowerManagement,
    InterfacePowerManagement,
    Lba48,
    Ncq,
    Trim,
    AutomaticAcousticManagement,
    PowerUpInStandby,
    ReadLookAhead,
    WriteCache,
    Sct,
    HostProtectedArea,
    DeviceConfigurationOverlay,
    Security,
}

/// <param name="Enabled">Current state if the device reports it, otherwise <c>null</c>.</param>
public sealed record Feature(FeatureKind Kind, bool Supported, bool? Enabled);

public sealed record VolumeInfo(
    string Label,
    string MountPoint,
    ulong Capacity,
    ulong Free,
    string FileSystem,
    string Serial,
    ulong Alignment)
{
    /// <summary>Used space in percent (0–100), rounded to one decimal.</summary>
    public double UsedPercent => Capacity == 0 ? 0 : Math.Round((Capacity - Free) * 100.0 / Capacity, 1);
}

public sealed record DriveInfo(
    DriveId Id,
    string DevicePath,
    string Model,
    string? Vendor,
    string? Serial,
    string? Firmware,
    BusType Bus,
    string? Link,
    MediaKind Media,
    ulong SizeBytes,
    uint LogicalSectorSize,
    uint PhysicalSectorSize,
    ulong? BufferSize,
    bool IsSystemDisk,
    IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<Feature> Features)
{
    /// <summary>Rounded capacity for display, e.g. "1 TB"; <see cref="SizeBytes"/> holds the exact value.</summary>
    public string SizeDisplay => ByteSize.Format(SizeBytes);
}
