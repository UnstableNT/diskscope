using System.Buffers.Binary;
using System.Globalization;

namespace DiskScope.Core.Smart;

/// <summary>Parses the NVMe SMART / Health Information log page (Log Identifier 02h, 512 bytes).</summary>
public static class NvmeHealth
{
    public const int Length = 512;

    // Offsets from the NVMe Base Specification.
    public const int CriticalWarning = 0;
    public const int CompositeTemperature = 1;
    public const int AvailableSpare = 3;
    public const int AvailableSpareThreshold = 4;
    public const int PercentageUsed = 5;
    public const int DataUnitsRead = 32;
    public const int DataUnitsWritten = 48;
    public const int HostReadCommands = 64;
    public const int HostWriteCommands = 80;
    public const int ControllerBusyTime = 96;
    public const int PowerCycles = 112;
    public const int PowerOnHours = 128;
    public const int UnsafeShutdowns = 144;
    public const int MediaErrors = 160;
    public const int ErrorLogEntries = 176;
    public const int WarningTemperatureTime = 192;
    public const int CriticalTemperatureTime = 196;

    /// <summary>One data unit is 1000 units of 512 bytes.</summary>
    public const ulong DataUnitBytes = 512_000;

    private const int KelvinOffset = 273;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<SmartEntry> Parse(ReadOnlySpan<byte> log)
    {
        if (log.Length < Length)
        {
            throw new ArgumentException($"Health-Log muss {Length} Byte lang sein.", nameof(log));
        }

        var temperature = CelsiusOrNull(BinaryPrimitives.ReadUInt16LittleEndian(log[CompositeTemperature..]));
        var spare = log[AvailableSpare];
        var spareThreshold = log[AvailableSpareThreshold];

        List<SmartEntry> rows =
        [
            Row(CriticalWarning, "Critical Warning", ValueKind.Raw, log[CriticalWarning], $"0x{log[CriticalWarning]:X2}"),
            Row(CompositeTemperature, "Composite Temperature", ValueKind.Temperature,
                BinaryPrimitives.ReadUInt16LittleEndian(log[CompositeTemperature..]),
                temperature is { } c ? $"{c} °C" : "–"),
            Row(AvailableSpare, "Available Spare", ValueKind.Percent, spare, $"{spare} %"),
            Row(AvailableSpareThreshold, "Available Spare Threshold", ValueKind.Percent, spareThreshold, $"{spareThreshold} %"),
            Row(PercentageUsed, "Percentage Used", ValueKind.Percent, log[PercentageUsed], $"{log[PercentageUsed]} %"),
            DataRow(log, DataUnitsRead, "Data Units Read"),
            DataRow(log, DataUnitsWritten, "Data Units Written"),
            CountRow(log, HostReadCommands, "Host Read Commands"),
            CountRow(log, HostWriteCommands, "Host Write Commands"),
            CountRow(log, ControllerBusyTime, "Controller Busy Time", ValueKind.Minutes, " min"),
            CountRow(log, PowerCycles, "Power Cycles"),
            CountRow(log, PowerOnHours, "Power-On Hours", ValueKind.Hours, " h"),
            CountRow(log, UnsafeShutdowns, "Unsafe Shutdowns"),
            CountRow(log, MediaErrors, "Media and Data Integrity Errors"),
            CountRow(log, ErrorLogEntries, "Error Information Log Entries"),
            Row(WarningTemperatureTime, "Warning Temperature Time", ValueKind.Minutes,
                BinaryPrimitives.ReadUInt32LittleEndian(log[WarningTemperatureTime..]),
                $"{BinaryPrimitives.ReadUInt32LittleEndian(log[WarningTemperatureTime..])} min"),
            Row(CriticalTemperatureTime, "Critical Temperature Time", ValueKind.Minutes,
                BinaryPrimitives.ReadUInt32LittleEndian(log[CriticalTemperatureTime..]),
                $"{BinaryPrimitives.ReadUInt32LittleEndian(log[CriticalTemperatureTime..])} min"),
        ];

        return SmartEvaluator.EvaluateNvme(rows);
    }

    public static int? Temperature(ReadOnlySpan<byte> log) =>
        log.Length < Length ? null : CelsiusOrNull(BinaryPrimitives.ReadUInt16LittleEndian(log[CompositeTemperature..]));

    /// <summary>Reads a 128-bit little-endian counter, saturating at <see cref="ulong.MaxValue"/>.</summary>
    internal static ulong ReadCounter(ReadOnlySpan<byte> log, int offset)
    {
        var low = BinaryPrimitives.ReadUInt64LittleEndian(log[offset..]);
        var high = BinaryPrimitives.ReadUInt64LittleEndian(log[(offset + 8)..]);
        return high != 0 ? ulong.MaxValue : low;
    }

    private static int? CelsiusOrNull(ushort kelvin) => kelvin == 0 ? null : kelvin - KelvinOffset;

    private static SmartEntry Row(int offset, string name, ValueKind kind, ulong raw, string value) =>
        new(offset, name, kind, value, null, null, null, raw.ToString(Invariant), raw, HealthStatus.Ok);

    private static SmartEntry CountRow(
        ReadOnlySpan<byte> log, int offset, string name, ValueKind kind = ValueKind.Count, string unit = "")
    {
        var value = ReadCounter(log, offset);
        return Row(offset, name, kind, value, string.Create(Invariant, $"{value}{unit}"));
    }

    private static SmartEntry DataRow(ReadOnlySpan<byte> log, int offset, string name)
    {
        var units = ReadCounter(log, offset);
        var bytes = units > ulong.MaxValue / DataUnitBytes ? ulong.MaxValue : units * DataUnitBytes;
        return Row(offset, name, ValueKind.DataSize, units, ByteSize.Format(bytes));
    }
}
