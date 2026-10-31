using System.Buffers.Binary;
using DiskScope.Core.Smart;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace DiskScope.Core.Native;

/// <summary>Unparsed SMART / health answers of a drive.</summary>
internal sealed record RawSmartData(
    SmartSource Source,
    byte[]? AtaData,
    byte[]? AtaThresholds,
    bool? ThresholdExceeded,
    byte[]? NvmeHealthLog,
    byte[]? TemperatureDescriptor,
    string? Reason);

/// <summary>
/// Reads SMART data along the fallback chain of SPEC 4.3:
/// NVMe log page → ATA pass-through → SAT (USB) → legacy SMART IOCTLs → IOCTL_STORAGE_PREDICT_FAILURE.
/// </summary>
internal static class SmartProbe
{
    public const int SectorLength = 512;

    private const uint NvmeLogPageHealthInfo = 0x02;

    public static RawSmartData Probe(string devicePath)
    {
        using var query = DeviceIo.Open(devicePath, DeviceAccess.Query);
        var bus = ReadBusType(query);
        var temperature = StorageQuery.TryQuery(query, STORAGE_PROPERTY_ID.StorageDeviceTemperatureProperty, 512);

        if (bus == STORAGE_BUS_TYPE.BusTypeNvme)
        {
            var log = TryNvmeHealthLog(query);
            return log is not null
                ? new RawSmartData(SmartSource.NvmeLog, null, null, null, log, temperature, null)
                : PredictFailureOrNone(query, temperature, "NVMe-Health-Log nicht lesbar");
        }

        SafeFileHandle? command = null;
        try
        {
            command = DeviceIo.Open(devicePath, DeviceAccess.Command);
        }
        catch (DiskScopeException ex) when (ex.IsAccessDenied)
        {
            // Without admin rights only IOCTL_STORAGE_PREDICT_FAILURE remains.
        }

        using (command)
        {
            if (command is not null)
            {
                var ata = bus is STORAGE_BUS_TYPE.BusTypeUsb or STORAGE_BUS_TYPE.BusTypeScsi
                    ? TrySat(command)
                    : TryAtaPassThrough(command) ?? TryLegacy(command);
                if (ata is not null)
                {
                    return ata with { TemperatureDescriptor = temperature };
                }
            }
        }

        var result = PredictFailureOrNone(
            query,
            temperature,
            command is null ? "Für S.M.A.R.T.-Werte sind Administratorrechte erforderlich" : "S.M.A.R.T. nicht verfügbar");
        return result.AtaData is not null && command is null
            ? result with { Reason = "Ohne Administratorrechte keine Schwellwerte und kein SMART-Status" }
            : result;
    }

    /// <summary>Only the temperature property; cheap and available without admin rights.</summary>
    public static byte[]? ProbeTemperature(string devicePath)
    {
        using var query = DeviceIo.Open(devicePath, DeviceAccess.Query);
        return StorageQuery.TryQuery(query, STORAGE_PROPERTY_ID.StorageDeviceTemperatureProperty, 512);
    }

    private static STORAGE_BUS_TYPE ReadBusType(SafeFileHandle handle)
    {
        var descriptor = StorageQuery.TryQuery(handle, STORAGE_PROPERTY_ID.StorageDeviceProperty);
        return descriptor is { Length: >= 32 }
            ? (STORAGE_BUS_TYPE)BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(28))
            : STORAGE_BUS_TYPE.BusTypeUnknown;
    }

    private static byte[]? TryNvmeHealthLog(SafeFileHandle handle)
    {
        foreach (var property in new[]
        {
            STORAGE_PROPERTY_ID.StorageDeviceProtocolSpecificProperty,
            STORAGE_PROPERTY_ID.StorageAdapterProtocolSpecificProperty,
        })
        {
            var log = StorageQuery.TryQueryProtocolData(
                handle,
                property,
                STORAGE_PROTOCOL_TYPE.ProtocolTypeNvme,
                (uint)STORAGE_PROTOCOL_NVME_DATA_TYPE.NVMeDataTypeLogPage,
                NvmeLogPageHealthInfo,
                0,
                SectorLength);
            if (log is not null)
            {
                return log;
            }
        }

        return null;
    }

    private static RawSmartData? TryAtaPassThrough(SafeFileHandle handle)
    {
        var data = AtaPassThrough.TryReadData(handle, AtaCommands.SmartCommand(AtaCommands.SmartReadData), SectorLength);
        if (data is null)
        {
            return null;
        }

        var thresholds = AtaPassThrough.TryReadData(
            handle, AtaCommands.SmartCommand(AtaCommands.SmartReadThresholds), SectorLength);
        var status = AtaPassThrough.TryExecute(handle, AtaCommands.SmartCommand(AtaCommands.SmartReturnStatus), 0);
        return new RawSmartData(
            SmartSource.AtaPassThrough, data, thresholds, InterpretReturnStatus(status?.Registers), null, null, null);
    }

    private static RawSmartData? TrySat(SafeFileHandle handle)
    {
        var data = SatPassThrough.TryReadData(handle, AtaCommands.SmartCommand(AtaCommands.SmartReadData), SectorLength);
        if (data is null)
        {
            return null;
        }

        var thresholds = SatPassThrough.TryReadData(
            handle, AtaCommands.SmartCommand(AtaCommands.SmartReadThresholds), SectorLength);
        return new RawSmartData(SmartSource.Sat, data, thresholds, null, null, null, null);
    }

    private static RawSmartData? TryLegacy(SafeFileHandle handle)
    {
        if (!LegacySmart.IsSupported(handle))
        {
            return null;
        }

        var data = LegacySmart.TryReadData(handle, AtaCommands.SmartCommand(AtaCommands.SmartReadData), SectorLength);
        if (data is null)
        {
            return null;
        }

        var thresholds = LegacySmart.TryReadData(
            handle, AtaCommands.SmartCommand(AtaCommands.SmartReadThresholds), SectorLength);
        return new RawSmartData(
            SmartSource.LegacyIoctl, data, thresholds, LegacySmart.TryReturnStatus(handle), null, null, null);
    }

    private static RawSmartData PredictFailureOrNone(SafeFileHandle handle, byte[]? temperature, string reason)
    {
        // STORAGE_PREDICT_FAILURE: PredictFailure (ULONG), VendorSpecific[512]
        Span<byte> output = stackalloc byte[4 + SectorLength];
        if (DeviceIo.TryControl(handle, PInvoke.IOCTL_STORAGE_PREDICT_FAILURE, default, output, out var bytes, out _)
            && bytes >= 4)
        {
            var failing = BinaryPrimitives.ReadUInt32LittleEndian(output) != 0;

            // On ATA drives the storage stack fills VendorSpecific with the SMART READ DATA structure;
            // use it as attribute source (without thresholds) when it passes the plausibility check.
            var vendor = bytes >= 4 + SectorLength ? output.Slice(4, SectorLength).ToArray() : null;
            var data = vendor is not null && LooksLikeSmartData(vendor) ? vendor : null;
            return new RawSmartData(SmartSource.PredictFailure, data, null, failing, null, temperature, reason);
        }

        return new RawSmartData(SmartSource.None, null, null, null, null, temperature, reason);
    }

    /// <summary>
    /// SMART READ DATA: valid checksum (all 512 bytes sum to 0 mod 256), or a plausible revision
    /// with at least three entries whose normalized values lie in 1..253.
    /// </summary>
    internal static bool LooksLikeSmartData(ReadOnlySpan<byte> data)
    {
        if (data.Length < SectorLength)
        {
            return false;
        }

        byte sum = 0;
        foreach (var b in data[..SectorLength])
        {
            sum += b;
        }

        var revision = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (revision == 0)
        {
            return false;
        }

        if (sum == 0)
        {
            return true;
        }

        var plausible = 0;
        for (var i = 0; i < 30; i++)
        {
            var entry = data.Slice(2 + (i * 12), 12);
            if (entry[0] != 0 && entry[3] is >= 1 and <= 253 && entry[4] is >= 1 and <= 253)
            {
                plausible++;
            }
        }

        return revision <= 0x10 && plausible >= 3;
    }

    internal static bool? InterpretReturnStatus(AtaTaskFile? registers) => registers switch
    {
        { LbaMid: AtaCommands.SmartLbaMid, LbaHigh: AtaCommands.SmartLbaHigh } => false,
        { LbaMid: AtaCommands.SmartFailingLbaMid, LbaHigh: AtaCommands.SmartFailingLbaHigh } => true,
        _ => null,
    };
}
