using System.Globalization;
using DiskScope.Core.Native;

namespace DiskScope.Core.Devices;

/// <summary>Combines raw query results into a <see cref="DriveInfo"/>. Free of I/O so it can be unit-tested.</summary>
internal static class DriveInfoBuilder
{
    private const ushort NonRotatingMedia = 0x0001;
    private const ushort MinRotationRate = 0x0401;
    private const uint DefaultSectorSize = 512;

    public static DriveInfo Build(DriveId id, RawDiskData raw, IReadOnlyList<VolumeInfo> volumes, bool isSystemDisk)
    {
        var descriptor = raw.DeviceDescriptor is null ? null : StorageDescriptors.ParseDevice(raw.DeviceDescriptor);
        var bus = descriptor is null ? BusType.Other : StorageDescriptors.MapBusType(descriptor.RawBusType);

        var ata = raw.AtaIdentify is { Length: >= AtaIdentify.Length } ? AtaIdentify.Parse(raw.AtaIdentify) : null;
        var nvme = raw.NvmeIdentifyController is { Length: >= NvmeIdentify.Length }
            ? NvmeIdentify.Parse(raw.NvmeIdentifyController)
            : null;

        var model = FirstNonEmpty(ata?.Model, nvme?.Model, descriptor?.ProductId) ?? "Unbekanntes Laufwerk";
        var serial = FirstNonEmpty(ata?.Serial, nvme?.Serial, descriptor?.SerialNumber);
        var firmware = FirstNonEmpty(ata?.Firmware, nvme?.Firmware, descriptor?.ProductRevision);

        var alignment = raw.AccessAlignment is null ? null : StorageDescriptors.ParseAccessAlignment(raw.AccessAlignment);
        var logical = alignment?.Logical ?? raw.GeometryBytesPerSector ?? DefaultSectorSize;
        var physical = alignment?.Physical ?? logical;

        return new DriveInfo(
            Id: id,
            DevicePath: id.DevicePath,
            Model: model,
            Vendor: VendorOrNull(descriptor?.VendorId),
            Serial: serial,
            Firmware: firmware,
            Bus: bus,
            Link: BuildLink(bus, ata, raw.PciLink),
            Media: DetectMedia(bus, ata, raw.SeekPenalty),
            SizeBytes: raw.Length ?? raw.GeometryDiskSize ?? 0,
            LogicalSectorSize: logical,
            PhysicalSectorSize: physical,
            BufferSize: ata?.BufferBytes,
            IsSystemDisk: isSystemDisk,
            Volumes: volumes,
            Features: ata?.Features ?? nvme?.Features ?? FallbackFeatures(raw));
    }

    internal static string? FormatPciLink(PciLink link)
    {
        if (link.CurrentSpeed == 0 || link.CurrentWidth == 0)
        {
            return null;
        }

        var text = $"PCIe {PciGeneration(link.CurrentSpeed)} x{link.CurrentWidth}";
        var maxBetter = link.MaxSpeed > link.CurrentSpeed || link.MaxWidth > link.CurrentWidth;
        return maxBetter && link.MaxSpeed != 0 && link.MaxWidth != 0
            ? $"{text}, max. PCIe {PciGeneration(link.MaxSpeed)} x{link.MaxWidth}"
            : text;
    }

    // DEVPKEY_PciDevice_*LinkSpeed: 1 = 2.5 GT/s (Gen 1) ... 5 = 32 GT/s (Gen 5)
    private static string PciGeneration(uint speed) => string.Create(CultureInfo.InvariantCulture, $"{speed}.0");

    private static string? BuildLink(BusType bus, AtaIdentifyData? ata, PciLink? pci) => bus switch
    {
        BusType.Nvme => pci is { } link ? FormatPciLink(link) : "PCIe",
        BusType.Sata => ata?.Link ?? "SATA",
        BusType.Usb => "USB",
        BusType.Sas => "SAS",
        _ => null,
    };

    private static MediaKind DetectMedia(BusType bus, AtaIdentifyData? ata, byte[]? seekPenalty)
    {
        if (bus == BusType.Nvme)
        {
            return MediaKind.Ssd;
        }

        if (ata is not null)
        {
            if (ata.RotationRate == NonRotatingMedia)
            {
                return MediaKind.Ssd;
            }

            if (ata.RotationRate >= MinRotationRate && ata.RotationRate != 0xFFFF)
            {
                return MediaKind.Hdd;
            }
        }

        return seekPenalty is null
            ? MediaKind.Unknown
            : StorageDescriptors.ParseBooleanDescriptor(seekPenalty) switch
            {
                true => MediaKind.Hdd,
                false => MediaKind.Ssd,
                null => MediaKind.Unknown,
            };
    }

    private static IReadOnlyList<Feature> FallbackFeatures(RawDiskData raw)
    {
        // Without IDENTIFY data only TRIM is known (from DEVICE_TRIM_DESCRIPTOR).
        var trim = raw.Trim is null ? null : StorageDescriptors.ParseBooleanDescriptor(raw.Trim);
        return trim is null ? [] : [new Feature(FeatureKind.Trim, trim.Value, trim.Value ? true : null)];
    }

    private static string? VendorOrNull(string? vendor) =>
        string.IsNullOrWhiteSpace(vendor)
        || vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase)
        || vendor.Equals("NVMe", StringComparison.OrdinalIgnoreCase)
            ? null
            : vendor;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
