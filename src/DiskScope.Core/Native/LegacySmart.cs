using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace DiskScope.Core.Native;

/// <summary>SMART_GET_VERSION / SMART_RCV_DRIVE_DATA / SMART_SEND_DRIVE_COMMAND for older drivers.</summary>
internal static class LegacySmart
{
    // GETVERSIONINPARAMS: bVersion, bRevision, bReserved, bIDEDeviceMap, fCapabilities (4), dwReserved[4]
    private const int VersionSize = 24;
    private const uint CapSmartCommands = 0x00000004;

    // SENDCMDINPARAMS (pack 1): cBufferSize (4), IDEREGS (8), bDriveNumber, bReserved[3], dwReserved[4], bBuffer[1]
    private const int InParamsSize = 33;
    private const int RegistersOffset = 4;
    private const int DriveNumberOffset = 12;

    // SENDCMDOUTPARAMS (pack 1): cBufferSize (4), DRIVERSTATUS (12), bBuffer[]
    private const int OutDataOffset = 16;

    public static bool IsSupported(SafeFileHandle handle)
    {
        Span<byte> version = stackalloc byte[VersionSize];
        if (!DeviceIo.TryControl(handle, PInvoke.SMART_GET_VERSION, default, version, out var bytes, out _) || bytes < 8)
        {
            return false;
        }

        return (BinaryPrimitives.ReadUInt32LittleEndian(version[4..]) & CapSmartCommands) != 0;
    }

    /// <summary>SMART READ DATA / READ THRESHOLDS through SMART_RCV_DRIVE_DATA (512 bytes).</summary>
    public static byte[]? TryReadData(SafeFileHandle handle, AtaTaskFile taskFile, int length)
    {
        AtaCommands.EnsureReadOnly(taskFile);
        var input = BuildInput(taskFile, length);
        var output = new byte[OutDataOffset + length];
        if (!DeviceIo.TryControl(handle, PInvoke.SMART_RCV_DRIVE_DATA, input, output, out var bytes, out var error))
        {
            return error == DiskScopeException.ErrorAccessDenied || DeviceIo.IsUnsupported(error)
                ? null
                : throw new DiskScopeException("SMART_RCV_DRIVE_DATA", error);
        }

        return bytes < OutDataOffset + length || output[4] != 0
            ? null
            : output.AsSpan(OutDataOffset, length).ToArray();
    }

    /// <summary>
    /// SMART RETURN STATUS through SMART_SEND_DRIVE_COMMAND; the returned IDEREGS follow the 16-byte header.
    /// Returns true if a threshold is exceeded, null if unknown.
    /// </summary>
    public static bool? TryReturnStatus(SafeFileHandle handle)
    {
        var taskFile = AtaCommands.SmartCommand(AtaCommands.SmartReturnStatus);
        AtaCommands.EnsureReadOnly(taskFile);
        var input = BuildInput(taskFile, 0);
        var output = new byte[OutDataOffset + 8];
        if (!DeviceIo.TryControl(handle, PInvoke.SMART_SEND_DRIVE_COMMAND, input, output, out var bytes, out _)
            || bytes < OutDataOffset + 8 || output[4] != 0)
        {
            return null;
        }

        // IDEREGS: features, sector count, sector number, cyl low (LBA mid), cyl high (LBA high), ...
        var mid = output[OutDataOffset + 3];
        var high = output[OutDataOffset + 4];
        if (mid == AtaCommands.SmartLbaMid && high == AtaCommands.SmartLbaHigh)
        {
            return false;
        }

        return mid == AtaCommands.SmartFailingLbaMid && high == AtaCommands.SmartFailingLbaHigh ? true : null;
    }

    private static byte[] BuildInput(AtaTaskFile taskFile, int length)
    {
        var input = new byte[InParamsSize];
        BinaryPrimitives.WriteInt32LittleEndian(input, length);
        input[RegistersOffset + 0] = taskFile.Features;
        input[RegistersOffset + 1] = taskFile.SectorCount;
        input[RegistersOffset + 2] = taskFile.LbaLow;
        input[RegistersOffset + 3] = taskFile.LbaMid;
        input[RegistersOffset + 4] = taskFile.LbaHigh;
        input[RegistersOffset + 5] = taskFile.Device;
        input[RegistersOffset + 6] = taskFile.Command;

        // The handle already addresses the drive; the drive number is only used by very old miniports.
        input[DriveNumberOffset] = 0;
        return input;
    }
}
