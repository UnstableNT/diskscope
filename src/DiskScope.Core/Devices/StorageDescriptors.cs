using System.Buffers.Binary;
using System.Text;

namespace DiskScope.Core.Devices;

/// <summary>Parsed STORAGE_DEVICE_DESCRIPTOR (IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceProperty).</summary>
public sealed record StorageDeviceDescriptor(
    string? VendorId,
    string? ProductId,
    string? ProductRevision,
    string? SerialNumber,
    int RawBusType,
    bool RemovableMedia);

/// <summary>Parsers for the fixed-layout descriptors returned by IOCTL_STORAGE_QUERY_PROPERTY.</summary>
public static class StorageDescriptors
{
    // STORAGE_BUS_TYPE values (ntddstor.h)
    private const int BusTypeScsi = 1;
    private const int BusTypeAtapi = 2;
    private const int BusTypeAta = 3;
    private const int BusTypeFibre = 6;
    private const int BusTypeUsb = 7;
    private const int BusTypeRaid = 8;
    private const int BusTypeIScsi = 9;
    private const int BusTypeSas = 10;
    private const int BusTypeSata = 11;
    private const int BusTypeVirtual = 14;
    private const int BusTypeFileBackedVirtual = 15;
    private const int BusTypeSpaces = 16;
    private const int BusTypeNvme = 17;

    public static StorageDeviceDescriptor? ParseDevice(ReadOnlySpan<byte> data)
    {
        // Version (4), Size (4), DeviceType, DeviceTypeModifier, RemovableMedia, CommandQueueing (1 each),
        // VendorIdOffset (12), ProductIdOffset (16), ProductRevisionOffset (20), SerialNumberOffset (24), BusType (28)
        if (data.Length < 32)
        {
            return null;
        }

        return new StorageDeviceDescriptor(
            VendorId: StringAt(data, ReadOffset(data, 12)),
            ProductId: StringAt(data, ReadOffset(data, 16)),
            ProductRevision: StringAt(data, ReadOffset(data, 20)),
            SerialNumber: StringAt(data, ReadOffset(data, 24)),
            RawBusType: BinaryPrimitives.ReadInt32LittleEndian(data[28..]),
            RemovableMedia: data[10] != 0);
    }

    /// <summary>STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR: logical sector at offset 16, physical at 20.</summary>
    public static (uint Logical, uint Physical)? ParseAccessAlignment(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24)
        {
            return null;
        }

        var logical = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var physical = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        return logical == 0 ? null : (logical, physical == 0 ? logical : physical);
    }

    /// <summary>DEVICE_SEEK_PENALTY_DESCRIPTOR and DEVICE_TRIM_DESCRIPTOR: a BOOLEAN at offset 8.</summary>
    public static bool? ParseBooleanDescriptor(ReadOnlySpan<byte> data) => data.Length < 9 ? null : data[8] != 0;

    public static BusType MapBusType(int rawBusType) => rawBusType switch
    {
        BusTypeSata or BusTypeAta or BusTypeAtapi => BusType.Sata,
        BusTypeNvme => BusType.Nvme,
        BusTypeUsb => BusType.Usb,
        BusTypeSas => BusType.Sas,
        BusTypeScsi or BusTypeIScsi or BusTypeFibre => BusType.Scsi,
        BusTypeRaid or BusTypeSpaces => BusType.Raid,
        BusTypeVirtual or BusTypeFileBackedVirtual => BusType.Virtual,
        _ => BusType.Other,
    };

    private static uint ReadOffset(ReadOnlySpan<byte> data, int position) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);

    private static string? StringAt(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset == 0 || offset >= data.Length)
        {
            return null;
        }

        var tail = data[(int)offset..];
        var end = tail.IndexOf((byte)0);
        var raw = end < 0 ? tail : tail[..end];

        var builder = new StringBuilder(raw.Length);
        foreach (var b in raw)
        {
            builder.Append(b is >= 0x20 and < 0x7F ? (char)b : ' ');
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }
}
