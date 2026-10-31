using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.IscsiDisc;

namespace DiskScope.Core.Native;

/// <summary>
/// SCSI/ATA Translation (SAT): ATA PASS-THROUGH (16), opcode 85h, sent with IOCTL_SCSI_PASS_THROUGH_DIRECT.
/// Used for ATA drives in USB enclosures. Only data-in commands from <see cref="AtaCommands"/>.
/// </summary>
internal static class SatPassThrough
{
    private const byte AtaPassThrough16 = 0x85;
    private const byte ProtocolPioDataIn = 4;
    private const byte SenseLength = 32;
    private const uint TimeoutSeconds = 10;
    private const nuint BufferAlignment = 4096;

    public static unsafe byte[]? TryReadData(SafeFileHandle handle, AtaTaskFile taskFile, int length)
    {
        AtaCommands.EnsureReadOnly(taskFile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        var headerSize = Unsafe.SizeOf<SCSI_PASS_THROUGH_DIRECT>();
        var senseOffset = (headerSize + 7) & ~7;
        var request = new byte[senseOffset + SenseLength];

        var data = (byte*)NativeMemory.AlignedAlloc((nuint)length, BufferAlignment);
        try
        {
            new Span<byte>(data, length).Clear();

            var sptd = new SCSI_PASS_THROUGH_DIRECT
            {
                Length = (ushort)headerSize,
                CdbLength = 16,
                SenseInfoLength = SenseLength,
                DataIn = (byte)PInvoke.SCSI_IOCTL_DATA_IN,
                DataTransferLength = (uint)length,
                TimeOutValue = TimeoutSeconds,
                DataBuffer = data,
                SenseInfoOffset = (uint)senseOffset,
            };

            sptd.Cdb[0] = AtaPassThrough16;
            sptd.Cdb[1] = ProtocolPioDataIn << 1;
            // T_DIR = from device (bit 3), BYT_BLOK = blocks (bit 2), T_LENGTH = sector count field (2)
            sptd.Cdb[2] = 0x08 | 0x04 | 0x02;
            sptd.Cdb[4] = taskFile.Features;
            sptd.Cdb[6] = taskFile.SectorCount;
            sptd.Cdb[8] = taskFile.LbaLow;
            sptd.Cdb[10] = taskFile.LbaMid;
            sptd.Cdb[12] = taskFile.LbaHigh;
            sptd.Cdb[13] = taskFile.Device;
            sptd.Cdb[14] = taskFile.Command;
            Unsafe.WriteUnaligned(ref request[0], sptd);

            if (!DeviceIo.TryControl(handle, PInvoke.IOCTL_SCSI_PASS_THROUGH_DIRECT, request, request, out _, out var error))
            {
                if (DeviceIo.IsUnsupported(error) || error == DiskScopeException.ErrorAccessDenied)
                {
                    return null;
                }

                throw new DiskScopeException("IOCTL_SCSI_PASS_THROUGH_DIRECT", error, $"SAT 0x{taskFile.Command:X2}");
            }

            var result = Unsafe.ReadUnaligned<SCSI_PASS_THROUGH_DIRECT>(ref request[0]);
            if (result.ScsiStatus != 0 || result.DataTransferLength < (uint)length)
            {
                return null;
            }

            return new ReadOnlySpan<byte>(data, length).ToArray();
        }
        finally
        {
            NativeMemory.AlignedFree(data);
        }
    }
}
