using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace DiskScope.Core.Native;

/// <summary>Unparsed answers of a physical drive to the queries listed in SPEC 4.2.</summary>
internal sealed record RawDiskData(
    byte[]? DeviceDescriptor,
    byte[]? AccessAlignment,
    byte[]? SeekPenalty,
    byte[]? Trim,
    ulong? Length,
    uint? GeometryBytesPerSector,
    ulong? GeometryDiskSize,
    byte[]? AtaIdentify,
    byte[]? NvmeIdentifyController,
    PciLink? PciLink);

/// <summary>Collects <see cref="RawDiskData"/> with read-only access.</summary>
internal static class DiskProbe
{
    public const int AtaIdentifyLength = 512;
    public const int NvmeIdentifyLength = 4096;

    private const uint NvmeIdentifyCnsController = 1;

    public static RawDiskData Probe(string devicePath, uint devInst)
    {
        using var handle = DeviceIo.Open(devicePath, DeviceAccess.Query);

        var descriptor = StorageQuery.TryQuery(handle, STORAGE_PROPERTY_ID.StorageDeviceProperty);
        var bus = descriptor is { Length: >= 32 }
            ? (STORAGE_BUS_TYPE)BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(28))
            : STORAGE_BUS_TYPE.BusTypeUnknown;
        var alignment = StorageQuery.TryQuery(handle, STORAGE_PROPERTY_ID.StorageAccessAlignmentProperty, 64);
        var seekPenalty = StorageQuery.TryQuery(handle, STORAGE_PROPERTY_ID.StorageDeviceSeekPenaltyProperty, 16);
        var trim = StorageQuery.TryQuery(handle, STORAGE_PROPERTY_ID.StorageDeviceTrimProperty, 16);

        ulong? length = null;
        Span<byte> lengthBuffer = stackalloc byte[8];
        if (DeviceIo.TryControl(handle, PInvoke.IOCTL_DISK_GET_LENGTH_INFO, default, lengthBuffer, out var lengthBytes, out _)
            && lengthBytes == 8)
        {
            length = BinaryPrimitives.ReadUInt64LittleEndian(lengthBuffer);
        }

        // DISK_GEOMETRY_EX: DISK_GEOMETRY (24 bytes, BytesPerSector at 20), DiskSize (8) at 24
        uint? bytesPerSector = null;
        ulong? diskSize = null;
        Span<byte> geometry = stackalloc byte[256];
        if (DeviceIo.TryControl(handle, PInvoke.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, default, geometry, out var geometryBytes, out _)
            && geometryBytes >= 32)
        {
            bytesPerSector = BinaryPrimitives.ReadUInt32LittleEndian(geometry[20..]);
            diskSize = BinaryPrimitives.ReadUInt64LittleEndian(geometry[24..]);
        }

        byte[]? ataIdentify = null;
        byte[]? nvmeIdentify = null;
        if (bus == STORAGE_BUS_TYPE.BusTypeNvme)
        {
            nvmeIdentify = StorageQuery.TryQueryProtocolData(
                handle,
                STORAGE_PROPERTY_ID.StorageAdapterProtocolSpecificProperty,
                STORAGE_PROTOCOL_TYPE.ProtocolTypeNvme,
                (uint)STORAGE_PROTOCOL_NVME_DATA_TYPE.NVMeDataTypeIdentify,
                NvmeIdentifyCnsController,
                0,
                NvmeIdentifyLength);
        }
        else
        {
            var allowPassThrough = bus is STORAGE_BUS_TYPE.BusTypeSata or STORAGE_BUS_TYPE.BusTypeAta or STORAGE_BUS_TYPE.BusTypeRAID;
            ataIdentify = TryAtaIdentify(handle, devicePath, allowPassThrough);
        }

        var pciLink = devInst != 0 ? DeviceTree.TryGetPciLink(devInst) : null;

        return new RawDiskData(
            descriptor, alignment, seekPenalty, trim, length, bytesPerSector, diskSize, ataIdentify, nvmeIdentify, pciLink);
    }

    private static byte[]? TryAtaIdentify(SafeFileHandle queryHandle, string devicePath, bool allowPassThrough)
    {
        // Preferred: IDENTIFY through the storage stack, no write access needed.
        var identify = StorageQuery.TryQueryProtocolData(
            queryHandle,
            STORAGE_PROPERTY_ID.StorageDeviceProtocolSpecificProperty,
            STORAGE_PROTOCOL_TYPE.ProtocolTypeAta,
            (uint)STORAGE_PROTOCOL_ATA_DATA_TYPE.AtaDataTypeIdentify,
            0,
            0,
            AtaIdentifyLength);
        if (identify is not null || !allowPassThrough)
        {
            return identify;
        }

        // Fallback: ATA pass-through (requires admin).
        try
        {
            using var command = DeviceIo.Open(devicePath, DeviceAccess.Command);
            return AtaPassThrough.TryReadData(command, AtaCommands.Identify, AtaIdentifyLength);
        }
        catch (DiskScopeException ex) when (ex.IsAccessDenied)
        {
            return null;
        }
    }
}
