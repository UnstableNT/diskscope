using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.IscsiDisc;

namespace DiskScope.Core.Native;

/// <summary>ATA register values for a 28-bit command (ATA/ACS task file order).</summary>
internal readonly record struct AtaTaskFile(
    byte Features, byte SectorCount, byte LbaLow, byte LbaMid, byte LbaHigh, byte Device, byte Command)
{
    /// <summary>In a returned task file the command register holds the status register.</summary>
    public byte Status => Command;

    public bool HasError => (Status & 0x01) != 0;
}

/// <summary>
/// The ATA commands DiskScope may send through the read-only diagnostic paths
/// (ATA pass-through, SAT, legacy SMART IOCTLs). Anything that writes data or changes
/// device settings belongs in DiskScope.Core.Destructive (SPEC 9).
/// </summary>
internal static class AtaCommands
{
    public const byte IdentifyDevice = 0xEC;
    public const byte Smart = 0xB0;

    public const byte SmartReadData = 0xD0;
    public const byte SmartReadThresholds = 0xD1;
    public const byte SmartReturnStatus = 0xDA;

    public const byte SmartLbaMid = 0x4F;
    public const byte SmartLbaHigh = 0xC2;

    // SMART RETURN STATUS answers 2Ch/F4h in LBA High/Mid when a threshold is exceeded.
    public const byte SmartFailingLbaMid = 0xF4;
    public const byte SmartFailingLbaHigh = 0x2C;

    public static readonly AtaTaskFile Identify = new(0, 1, 0, 0, 0, 0xA0, IdentifyDevice);

    public static AtaTaskFile SmartCommand(byte feature) =>
        new(feature, 1, 0, SmartLbaMid, SmartLbaHigh, 0xA0, Smart);

    public static bool IsReadOnly(AtaTaskFile taskFile) => taskFile.Command switch
    {
        IdentifyDevice => true,
        Smart => taskFile.Features is SmartReadData or SmartReadThresholds or SmartReturnStatus,
        _ => false,
    };

    public static void EnsureReadOnly(AtaTaskFile taskFile)
    {
        if (!IsReadOnly(taskFile))
        {
            throw new InvalidOperationException(
                $"ATA-Befehl 0x{taskFile.Command:X2}/0x{taskFile.Features:X2} ist für lesende Aufrufe nicht freigegeben.");
        }
    }

    // SMART EXECUTE OFF-LINE IMMEDIATE (SPEC 4.3): LBA Low selects the routine.
    public const byte SmartExecuteOffline = 0xD4;
    public const byte SelfTestShort = 1;
    public const byte SelfTestExtended = 2;
    public const byte SelfTestAbort = 127;

    public static AtaTaskFile SmartSelfTest(byte subcommand) =>
        new(SmartExecuteOffline, 0, subcommand, SmartLbaMid, SmartLbaHigh, 0xA0, Smart);

    /// <summary>
    /// Starting or aborting a background self-test does not change user data. Only the off-line (background)
    /// variants are allowed; captive tests (0x81/0x82) would block the drive.
    /// </summary>
    public static bool IsSelfTestControl(AtaTaskFile taskFile) =>
        taskFile.Command == Smart
        && taskFile.Features == SmartExecuteOffline
        && taskFile.LbaLow is SelfTestShort or SelfTestExtended or SelfTestAbort;

    // SET FEATURES (SPEC 4.14): AAM and APM only. Used solely by DiskScope.Core.Destructive.
    public const byte SetFeatures = 0xEF;
    public const byte EnableAam = 0x42;
    public const byte DisableAam = 0xC2;
    public const byte EnableApm = 0x05;
    public const byte DisableApm = 0x85;

    public static AtaTaskFile SetFeature(byte feature, byte level) => new(feature, level, 0, 0, 0, 0xA0, SetFeatures);

    public static bool IsAcousticPowerSetting(AtaTaskFile taskFile) =>
        taskFile.Command == SetFeatures && taskFile.Features switch
        {
            EnableAam => taskFile.SectorCount is >= 0x80 and <= 0xFE,
            EnableApm => taskFile.SectorCount is >= 0x01 and <= 0xFE,
            DisableAam or DisableApm => true,
            _ => false,
        };

    public static void EnsureNonDestructive(AtaTaskFile taskFile)
    {
        if (!IsReadOnly(taskFile) && !IsSelfTestControl(taskFile))
        {
            throw new InvalidOperationException(
                $"ATA-Befehl 0x{taskFile.Command:X2}/0x{taskFile.Features:X2}/0x{taskFile.LbaLow:X2} ist außerhalb von DiskScope.Core.Destructive nicht freigegeben.");
        }
    }
}

/// <summary>IOCTL_ATA_PASS_THROUGH for the read-only commands in <see cref="AtaCommands"/>.</summary>
internal static class AtaPassThrough
{
    private const uint TimeoutSeconds = 10;

    private static readonly int HeaderSize = Unsafe.SizeOf<ATA_PASS_THROUGH_EX>();

    /// <summary>
    /// Runs a data-in command (<paramref name="length"/> &gt; 0) or a non-data command (<paramref name="length"/> = 0).
    /// Returns <c>null</c> if the driver or device rejects it.
    /// </summary>
    public static (byte[] Data, AtaTaskFile Registers)? TryExecute(SafeFileHandle handle, AtaTaskFile taskFile, int length)
    {
        AtaCommands.EnsureNonDestructive(taskFile);
        if (length > 0)
        {
            AtaCommands.EnsureReadOnly(taskFile);
        }

        return Send(handle, taskFile, length);
    }

    /// <summary>Sends a command whose permission the caller has already checked.</summary>
    private static (byte[] Data, AtaTaskFile Registers)? Send(SafeFileHandle handle, AtaTaskFile taskFile, int length)
    {
        // Data buffer directly after the header, aligned to 8 bytes.
        var dataOffset = (HeaderSize + 7) & ~7;
        var buffer = new byte[dataOffset + length];

        var flags = PInvoke.ATA_FLAGS_DRDY_REQUIRED | (length > 0 ? PInvoke.ATA_FLAGS_DATA_IN : 0);
        var header = new ATA_PASS_THROUGH_EX
        {
            Length = (ushort)HeaderSize,
            AtaFlags = (ushort)flags,
            DataTransferLength = (uint)length,
            TimeOutValue = TimeoutSeconds,
            DataBufferOffset = length > 0 ? (nuint)dataOffset : 0,
        };
        header.CurrentTaskFile[0] = taskFile.Features;
        header.CurrentTaskFile[1] = taskFile.SectorCount;
        header.CurrentTaskFile[2] = taskFile.LbaLow;
        header.CurrentTaskFile[3] = taskFile.LbaMid;
        header.CurrentTaskFile[4] = taskFile.LbaHigh;
        header.CurrentTaskFile[5] = taskFile.Device;
        header.CurrentTaskFile[6] = taskFile.Command;
        Unsafe.WriteUnaligned(ref buffer[0], header);

        if (!DeviceIo.TryControl(handle, PInvoke.IOCTL_ATA_PASS_THROUGH, buffer, buffer, out _, out var error))
        {
            if (DeviceIo.IsUnsupported(error) || error == DiskScopeException.ErrorAccessDenied)
            {
                return null;
            }

            throw new DiskScopeException("IOCTL_ATA_PASS_THROUGH", error, $"0x{taskFile.Command:X2}");
        }

        var result = Unsafe.ReadUnaligned<ATA_PASS_THROUGH_EX>(ref buffer[0]);
        var registers = new AtaTaskFile(
            result.CurrentTaskFile[0],
            result.CurrentTaskFile[1],
            result.CurrentTaskFile[2],
            result.CurrentTaskFile[3],
            result.CurrentTaskFile[4],
            result.CurrentTaskFile[5],
            result.CurrentTaskFile[6]);
        if (registers.HasError)
        {
            return null;
        }

        return (buffer.AsSpan(dataOffset, length).ToArray(), registers);
    }

    public static byte[]? TryReadData(SafeFileHandle handle, AtaTaskFile taskFile, int length) =>
        TryExecute(handle, taskFile, length)?.Data;

    /// <summary>
    /// SET FEATURES for AAM/APM only. Changes a device setting, therefore reserved for DiskScope.Core.Destructive
    /// (checked by an architecture test).
    /// </summary>
    public static bool TryApplySetting(SafeFileHandle handle, AtaTaskFile taskFile)
    {
        if (!AtaCommands.IsAcousticPowerSetting(taskFile))
        {
            throw new InvalidOperationException($"SET FEATURES 0x{taskFile.Features:X2}/0x{taskFile.SectorCount:X2} ist nicht freigegeben.");
        }

        return Send(handle, taskFile, 0) is not null;
    }
}
