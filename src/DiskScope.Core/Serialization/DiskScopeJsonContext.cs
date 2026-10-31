using System.Text.Json;
using System.Text.Json.Serialization;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;
using DiskScope.Core.Scanning;
using DiskScope.Core.Smart;

namespace DiskScope.Core.Serialization;

/// <summary>Source-generated JSON metadata for all result types (CLI output, export).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    Converters = [typeof(DriveIdJsonConverter)])]
[JsonSerializable(typeof(DriveInfo))]
[JsonSerializable(typeof(IReadOnlyList<DriveInfo>))]
[JsonSerializable(typeof(SmartResult))]
[JsonSerializable(typeof(BenchmarkResult))]
[JsonSerializable(typeof(RandomAccessResult))]
[JsonSerializable(typeof(ScanResult))]
[JsonSerializable(typeof(FileBenchmarkResult))]
[JsonSerializable(typeof(ExtraTestResult))]
[JsonSerializable(typeof(Destructive.EraseResult))]
[JsonSerializable(typeof(Monitoring.MonitorSnapshot))]
[JsonSerializable(typeof(BadLbaExport))]
[JsonSerializable(typeof(IReadOnlyList<TemperatureReading>))]
public sealed partial class DiskScopeJsonContext : JsonSerializerContext;

/// <summary>Writes <see cref="DriveId"/> as its plain drive number.</summary>
public sealed class DriveIdJsonConverter : JsonConverter<DriveId>
{
    public override DriveId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, DriveId value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Number);
}
