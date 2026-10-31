using DiskScope.Core.Devices;

namespace DiskScope.Core.Smart;

public enum HealthStatus { Ok, Warning, Error, Unknown }

/// <summary>Which path delivered the SMART data (SPEC 4.3 fallback chain).</summary>
public enum SmartSource { None, AtaPassThrough, Sat, LegacyIoctl, PredictFailure, NvmeLog }

/// <summary>How an attribute value is shown ("Darstellungsart").</summary>
public enum ValueKind { Count, Hours, Minutes, Temperature, Percent, DataSize, Raw }

/// <summary>One row of the health table.</summary>
/// <param name="Id">ATA attribute ID, or for NVMe the byte offset of the field in the health log.</param>
/// <param name="Value">Human-readable interpretation, e.g. "28 °C", "1003 GB", "100 %".</param>
/// <param name="Current">Normalized current value (ATA only).</param>
/// <param name="Worst">Normalized worst value (ATA only).</param>
/// <param name="Threshold">Failure threshold (ATA only, 0 = none).</param>
/// <param name="Raw">Raw value as hex (ATA, 6 bytes) or decimal (NVMe).</param>
/// <param name="RawValue">Raw value as a number for calculations and history.</param>
public sealed record SmartEntry(
    int Id,
    string Name,
    ValueKind Kind,
    string Value,
    byte? Current,
    byte? Worst,
    byte? Threshold,
    string Raw,
    ulong RawValue,
    HealthStatus Status);

public sealed record SmartResult(
    DriveId Drive,
    SmartSource Source,
    HealthStatus Overall,
    int? TemperatureCelsius,
    IReadOnlyList<SmartEntry> Attributes,
    string? UnsupportedReason,
    DateTimeOffset Timestamp)
{
    public bool IsSupported => Source != SmartSource.None;

    /// <summary>"Not supported" is a result state, not an error (SPEC 7).</summary>
    public static SmartResult Unsupported(DriveId drive, string reason, int? temperature, DateTimeOffset timestamp) =>
        new(drive, SmartSource.None, HealthStatus.Unknown, temperature, [], reason, timestamp);
}

/// <summary>A temperature reading with the drive-reported limits, if any.</summary>
public sealed record TemperatureReading(
    DriveId Drive,
    int? Celsius,
    int? WarningCelsius,
    int? CriticalCelsius,
    string Source,
    DateTimeOffset Timestamp);
