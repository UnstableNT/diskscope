using System.Buffers.Binary;
using System.Text;

namespace DiskScope.Core.Devices;

/// <summary>Fields of the NVMe Identify Controller data structure (CNS 01h, 4096 bytes; NVMe Base Specification).</summary>
public sealed record NvmeIdentifyData(
    ushort PciVendorId,
    string Model,
    string Serial,
    string Firmware,
    IReadOnlyList<Feature> Features);

public static class NvmeIdentify
{
    public const int Length = 4096;

    public static NvmeIdentifyData Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length)
        {
            throw new ArgumentException($"Identify-Controller-Daten müssen {Length} Byte lang sein.", nameof(data));
        }

        return new NvmeIdentifyData(
            PciVendorId: BinaryPrimitives.ReadUInt16LittleEndian(data),
            Serial: Ascii(data.Slice(4, 20)),
            Model: Ascii(data.Slice(24, 40)),
            Firmware: Ascii(data.Slice(64, 8)),
            Features: ParseFeatures(data));
    }

    private static IReadOnlyList<Feature> ParseFeatures(ReadOnlySpan<byte> data)
    {
        // OACS (bytes 257:256): bit 0 Security Send/Receive, bit 2 Firmware Commit/Image Download.
        var oacs = BinaryPrimitives.ReadUInt16LittleEndian(data[256..]);

        // NPSS (byte 263): number of power states supported, zero-based.
        var npss = data[263];

        // APSTA (byte 265): bit 0 autonomous power state transitions.
        var apsta = data[265];

        // ONCS (bytes 521:520): bit 2 Dataset Management (deallocate / TRIM).
        var oncs = BinaryPrimitives.ReadUInt16LittleEndian(data[520..]);

        // VWC (byte 525): bit 0 volatile write cache present.
        var vwc = data[525];

        // The SMART / Health Information log page is mandatory for NVMe controllers.
        // ATA-only features (AAM, PUIS, HPA, DCO, SCT, 48-bit LBA, NCQ, HIPM/DIPM, look-ahead) have no NVMe equivalent
        // and are omitted.
        return
        [
            new Feature(FeatureKind.Smart, true, true),
            new Feature(FeatureKind.FirmwareUpdate, (oacs & 0x4) != 0, null),
            new Feature(FeatureKind.PowerManagement, npss > 0, null),
            new Feature(FeatureKind.AdvancedPowerManagement, (apsta & 0x1) != 0, null),
            new Feature(FeatureKind.Trim, (oncs & 0x4) != 0, null),
            new Feature(FeatureKind.WriteCache, (vwc & 0x1) != 0, null),
            new Feature(FeatureKind.Security, (oacs & 0x1) != 0, null),
        ];
    }

    private static string Ascii(ReadOnlySpan<byte> field)
    {
        var builder = new StringBuilder(field.Length);
        foreach (var b in field)
        {
            builder.Append(b is >= 0x20 and < 0x7F ? (char)b : ' ');
        }

        return builder.ToString().Trim();
    }
}
