using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using static DiskScope.Core.Tests.TestBuffers;

namespace DiskScope.Core.Tests;

public class DriveInfoBuilderTests
{
    private const int BusSata = 11;
    private const int BusNvme = 17;
    private const int BusUsb = 7;

    private static RawDiskData Raw(
        byte[]? descriptor = null,
        byte[]? ata = null,
        byte[]? nvme = null,
        byte[]? seekPenalty = null,
        byte[]? trim = null,
        PciLink? pci = null) =>
        new(descriptor, null, seekPenalty, trim, 1_000_000_000UL, 512, 1_000_000_000UL, ata, nvme, pci);

    private static byte[] Bool(bool value) => [12, 0, 0, 0, 12, 0, 0, 0, value ? (byte)1 : (byte)0, 0, 0, 0];

    [Fact]
    public void Prefers_identify_data_over_descriptor()
    {
        var ata = new byte[AtaIdentify.Length];
        SetAtaString(ata, 27, 46, "Identify Model");
        SetAtaString(ata, 10, 19, "IDSERIAL");
        SetAtaString(ata, 23, 26, "IDFW");
        SetWord(ata, 76, 0x000E);
        SetWord(ata, 77, 0x0006);
        SetWord(ata, 217, 1);
        var descriptor = StorageDescriptorsTests.CreateDeviceDescriptor("ATA", "Descriptor Model", "DREV", "DSER", BusSata);

        var drive = DriveInfoBuilder.Build(new DriveId(2), Raw(descriptor, ata), [], isSystemDisk: false);

        drive.Model.ShouldBe("Identify Model");
        drive.Serial.ShouldBe("IDSERIAL");
        drive.Firmware.ShouldBe("IDFW");
        drive.Vendor.ShouldBeNull();
        drive.Bus.ShouldBe(BusType.Sata);
        drive.Link.ShouldBe("SATA 3 (6.0 Gb/s)");
        drive.Media.ShouldBe(MediaKind.Ssd);
        drive.DevicePath.ShouldBe(@"\\.\PhysicalDrive2");
        drive.Features.Count.ShouldBe(Enum.GetValues<FeatureKind>().Length);
    }

    [Fact]
    public void Falls_back_to_descriptor_without_identify()
    {
        var descriptor = StorageDescriptorsTests.CreateDeviceDescriptor("Vendor", "USB Disk", "1.00", "ABC", BusUsb);

        var drive = DriveInfoBuilder.Build(
            new DriveId(3), Raw(descriptor, seekPenalty: Bool(true), trim: Bool(false)), [], isSystemDisk: false);

        drive.Model.ShouldBe("USB Disk");
        drive.Vendor.ShouldBe("Vendor");
        drive.Firmware.ShouldBe("1.00");
        drive.Bus.ShouldBe(BusType.Usb);
        drive.Link.ShouldBe("USB");
        drive.Media.ShouldBe(MediaKind.Hdd);
        drive.Features.ShouldBe([new Feature(FeatureKind.Trim, false, null)]);
    }

    [Fact]
    public void Rotating_ata_drive_is_hdd()
    {
        var ata = new byte[AtaIdentify.Length];
        SetWord(ata, 217, 5400);

        var drive = DriveInfoBuilder.Build(
            new DriveId(0), Raw(StorageDescriptorsTests.CreateDeviceDescriptor(null, "X", "Y", null, BusSata), ata), [], false);

        drive.Media.ShouldBe(MediaKind.Hdd);
    }

    [Fact]
    public void Nvme_drive_uses_pci_link()
    {
        var nvme = new byte[NvmeIdentify.Length];
        SetAscii(nvme, 24, 40, "NVMe Model");
        var descriptor = StorageDescriptorsTests.CreateDeviceDescriptor("NVMe", "NVMe Model", "FW", null, BusNvme);

        var drive = DriveInfoBuilder.Build(
            new DriveId(1), Raw(descriptor, nvme: nvme, pci: new PciLink(4, 4, 4, 4)), [], isSystemDisk: true);

        drive.Model.ShouldBe("NVMe Model");
        drive.Vendor.ShouldBeNull();
        drive.Bus.ShouldBe(BusType.Nvme);
        drive.Link.ShouldBe("PCIe 4.0 x4");
        drive.Media.ShouldBe(MediaKind.Ssd);
        drive.IsSystemDisk.ShouldBeTrue();
    }

    [Theory]
    [InlineData(3u, 4u, 3u, 4u, "PCIe 3.0 x4")]
    [InlineData(3u, 2u, 4u, 4u, "PCIe 3.0 x2, max. PCIe 4.0 x4")]
    [InlineData(0u, 4u, 4u, 4u, null)]
    public void Formats_pci_link(uint speed, uint width, uint maxSpeed, uint maxWidth, string? expected) =>
        DriveInfoBuilder.FormatPciLink(new PciLink(speed, width, maxSpeed, maxWidth)).ShouldBe(expected);

    [Fact]
    public void Unknown_drive_gets_placeholder_model() =>
        DriveInfoBuilder.Build(new DriveId(9), Raw(), [], false).Model.ShouldBe("Unbekanntes Laufwerk");
}
