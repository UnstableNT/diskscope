using System.Globalization;

namespace DiskScope.Core.Smart;

/// <summary>
/// Parses SMART READ DATA and SMART READ THRESHOLDS (512 bytes each): 30 entries of 12 bytes from offset 2.
/// Data entry: ID, flags (2), current, worst, raw (6), reserved. Threshold entry: ID, threshold, reserved (10).
/// </summary>
public static class AtaSmart
{
    public const int Length = 512;
    public const int EntryCount = 30;
    private const int EntrySize = 12;
    private const int TableOffset = 2;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<SmartEntry> Parse(
        ReadOnlySpan<byte> data, ReadOnlySpan<byte> thresholds, AttributeTable table, string? model)
    {
        if (data.Length < Length)
        {
            throw new ArgumentException($"SMART-Daten müssen {Length} Byte lang sein.", nameof(data));
        }

        var thresholdById = ParseThresholds(thresholds);
        var result = new List<SmartEntry>(EntryCount);
        for (var i = 0; i < EntryCount; i++)
        {
            var entry = data.Slice(TableOffset + (i * EntrySize), EntrySize);
            var id = entry[0];
            if (id == 0)
            {
                continue;
            }

            var current = entry[3];
            var worst = entry[4];
            var raw = ReadRaw(entry.Slice(5, 6));
            byte? threshold = thresholdById.TryGetValue(id, out var t) ? t : null;

            var definition = table.Lookup(id, model);
            var attribute = new SmartEntry(
                Id: id,
                Name: definition.Name,
                Kind: definition.Kind,
                Value: Interpret(definition, current, raw),
                Current: current,
                Worst: worst,
                Threshold: threshold,
                Raw: raw.ToString("X12", Invariant),
                RawValue: raw,
                Status: HealthStatus.Ok);
            result.Add(attribute with { Status = SmartEvaluator.Evaluate(attribute) });
        }

        return result;
    }

    /// <summary>Temperature from attribute 194, else 190 (lowest raw byte).</summary>
    public static int? Temperature(IReadOnlyList<SmartEntry> attributes)
    {
        foreach (var id in new[] { 194, 190 })
        {
            var attribute = attributes.FirstOrDefault(a => a.Id == id);
            var celsius = attribute is null ? 0 : (int)(attribute.RawValue & 0xFF);
            if (celsius is > 0 and < 128)
            {
                return celsius;
            }
        }

        return null;
    }

    internal static string Interpret(AttributeDefinition definition, byte current, ulong raw)
    {
        var value = definition.RawBytes is { } bytes and > 0 and < 6 ? raw & ((1UL << (bytes * 8)) - 1) : raw;
        return definition.Kind switch
        {
            ValueKind.Count => value.ToString(Invariant),
            ValueKind.Hours => string.Create(Invariant, $"{value} h"),
            ValueKind.Minutes => string.Create(Invariant, $"{value} min"),
            ValueKind.Temperature => string.Create(Invariant, $"{raw & 0xFF} °C"),
            ValueKind.Percent => string.Create(Invariant, $"{(definition.PercentFromCurrent ? current : raw & 0xFF)} %"),
            ValueKind.DataSize => ByteSize.Format(SaturatingMultiply(value, definition.UnitBytes ?? 1)),
            _ => raw.ToString("X12", Invariant),
        };
    }

    private static Dictionary<byte, byte> ParseThresholds(ReadOnlySpan<byte> thresholds)
    {
        var result = new Dictionary<byte, byte>();
        if (thresholds.Length < Length)
        {
            return result;
        }

        for (var i = 0; i < EntryCount; i++)
        {
            var entry = thresholds.Slice(TableOffset + (i * EntrySize), EntrySize);
            if (entry[0] != 0)
            {
                result[entry[0]] = entry[1];
            }
        }

        return result;
    }

    private static ulong ReadRaw(ReadOnlySpan<byte> raw)
    {
        ulong value = 0;
        for (var i = raw.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | raw[i];
        }

        return value;
    }

    private static ulong SaturatingMultiply(ulong a, ulong b) =>
        b != 0 && a > ulong.MaxValue / b ? ulong.MaxValue : a * b;
}
