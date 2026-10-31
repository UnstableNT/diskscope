using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Smart;

public interface ISmartService
{
    SmartResult Read(DriveInfo drive);

    TemperatureReading ReadTemperature(DriveInfo drive);
}

/// <summary>Reads SMART / NVMe health data and temperatures (SPEC 4.3, 4.12).</summary>
public sealed partial class SmartService(ILogger<SmartService>? logger = null, AttributeTable? table = null) : ISmartService
{
    private readonly ILogger _logger = logger ?? NullLogger<SmartService>.Instance;
    private readonly AttributeTable _table = table ?? AttributeTable.Default;

    public SmartResult Read(DriveInfo drive)
    {
        var raw = SmartProbe.Probe(drive.DevicePath);
        var result = Interpret(drive, raw, _table, DateTimeOffset.Now);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var serial = LogMask.Serial(drive.Serial);
            LogResult(drive.Id.Number, serial, result.Source, result.Overall, result.Attributes.Count);
        }

        return result;
    }

    public TemperatureReading ReadTemperature(DriveInfo drive)
    {
        var now = DateTimeOffset.Now;
        var property = SmartProbe.ProbeTemperature(drive.DevicePath) is { } bytes ? TemperatureDescriptor.Parse(bytes) : null;
        if (property?.Current is { } current)
        {
            return new TemperatureReading(drive.Id, current, property.Value.Warning, property.Value.Critical, "StorageDeviceTemperatureProperty", now);
        }

        // ATA drives often report temperature only through SMART (attribute 194 / 190).
        var smart = Read(drive);
        return new TemperatureReading(
            drive.Id, smart.TemperatureCelsius, property?.Warning, property?.Critical, smart.Source.ToString(), now);
    }

    internal static SmartResult Interpret(DriveInfo drive, RawSmartData raw, AttributeTable table, DateTimeOffset timestamp)
    {
        var propertyTemperature = raw.TemperatureDescriptor is { } descriptor
            ? TemperatureDescriptor.Parse(descriptor)?.Current
            : null;

        switch (raw.Source)
        {
            case SmartSource.NvmeLog when raw.NvmeHealthLog is { } log:
                {
                    var rows = NvmeHealth.Parse(log);
                    return new SmartResult(
                        drive.Id, raw.Source, SmartEvaluator.Overall(rows, null),
                        NvmeHealth.Temperature(log) ?? propertyTemperature, rows, null, timestamp);
                }

            case SmartSource.AtaPassThrough or SmartSource.Sat or SmartSource.LegacyIoctl or SmartSource.PredictFailure
                when raw.AtaData is { } data:
                {
                    var attributes = AtaSmart.Parse(data, raw.AtaThresholds ?? [], table, drive.Model);
                    return new SmartResult(
                        drive.Id, raw.Source, SmartEvaluator.Overall(attributes, raw.ThresholdExceeded),
                        AtaSmart.Temperature(attributes) ?? propertyTemperature, attributes, raw.Reason, timestamp);
                }

            case SmartSource.PredictFailure:
                return new SmartResult(
                    drive.Id, raw.Source, SmartEvaluator.Overall([], raw.ThresholdExceeded),
                    propertyTemperature, [], raw.Reason, timestamp);

            default:
                return SmartResult.Unsupported(drive.Id, raw.Reason ?? "S.M.A.R.T. nicht verfügbar", propertyTemperature, timestamp);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SMART drive {Drive} (serial {Serial}): source {Source}, status {Status}, {Count} rows")]
    private partial void LogResult(int drive, string serial, SmartSource source, HealthStatus status, int count);
}

/// <summary>Masks identifiers in log output (SPEC 8).</summary>
public static class LogMask
{
    public static string Serial(string? serial) =>
        string.IsNullOrEmpty(serial) ? "–" : serial.Length <= 4 ? new string('*', serial.Length) : new string('*', serial.Length - 4) + serial[^4..];
}
