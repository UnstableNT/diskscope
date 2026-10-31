namespace DiskScope.Core.Devices;

/// <summary>User-facing names shared by CLI and GUI.</summary>
public static class DisplayNames
{
    public static string Of(FeatureKind kind) => kind switch
    {
        FeatureKind.Smart => "S.M.A.R.T.",
        FeatureKind.FirmwareUpdate => "Firmware-Update",
        FeatureKind.PowerManagement => "Power Management",
        FeatureKind.AdvancedPowerManagement => "Advanced Power Management",
        FeatureKind.InterfacePowerManagement => "Interface Power Management",
        FeatureKind.Lba48 => "48-Bit-Adressierung",
        FeatureKind.Ncq => "NCQ",
        FeatureKind.Trim => "TRIM",
        FeatureKind.AutomaticAcousticManagement => "Automatic Acoustic Management",
        FeatureKind.PowerUpInStandby => "Power-Up in Standby",
        FeatureKind.ReadLookAhead => "Read Look-Ahead",
        FeatureKind.WriteCache => "Schreibcache",
        FeatureKind.Sct => "SCT-Befehle",
        FeatureKind.HostProtectedArea => "Host Protected Area",
        FeatureKind.DeviceConfigurationOverlay => "Device Configuration Overlay",
        FeatureKind.Security => "Security-Modus",
        _ => kind.ToString(),
    };

    public static string Of(BusType bus) => bus switch
    {
        BusType.Sata => "SATA",
        BusType.Nvme => "NVMe",
        BusType.Usb => "USB",
        BusType.Sas => "SAS",
        BusType.Scsi => "SCSI",
        BusType.Raid => "RAID",
        BusType.Virtual => "Virtuell",
        _ => "Andere",
    };

    public static string Of(MediaKind media) => media switch
    {
        MediaKind.Hdd => "HDD",
        MediaKind.Ssd => "SSD",
        _ => "Unbekannt",
    };
}
