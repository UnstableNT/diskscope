using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskScope.Core.Devices;

namespace DiskScope.Core.Smart;

/// <summary>A stored SMART query: attribute id → (normalized current value, raw value).</summary>
public sealed record SmartHistoryEntry(DateTimeOffset Timestamp, HealthStatus Overall, IReadOnlyDictionary<int, SmartHistoryValue> Values);

public sealed record SmartHistoryValue(string Name, byte? Current, ulong Raw, string Display);

/// <summary>One attribute that changed between two stored queries.</summary>
public sealed record SmartChange(DateTimeOffset Timestamp, int Id, string Name, string Before, string After);

/// <summary>
/// "Protokoll" of SMART queries per drive (SPEC 4.3, P2): one JSON line per query, written only when a value changed.
/// Files live in %APPDATA%\DiskScope\smart-history and are keyed by a hash of model and serial number.
/// </summary>
public sealed class SmartHistory(string? directory = null)
{
    private readonly string _directory = directory ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskScope", "smart-history");

    /// <returns>True if the entry was stored (values differ from the last entry).</returns>
    public bool Append(DriveInfo drive, SmartResult result)
    {
        if (result.Attributes.Count == 0)
        {
            return false;
        }

        var entry = new SmartHistoryEntry(
            result.Timestamp,
            result.Overall,
            result.Attributes
                .GroupBy(a => a.Id)
                .ToDictionary(g => g.Key, g => new SmartHistoryValue(g.First().Name, g.First().Current, g.First().RawValue, g.First().Value)));

        var existing = Load(drive);
        var last = existing.Count > 0 ? existing[^1] : null;
        if (last is not null && SameValues(last, entry))
        {
            return false;
        }

        System.IO.Directory.CreateDirectory(_directory);
        System.IO.File.AppendAllText(PathFor(drive), JsonSerializer.Serialize(entry, HistoryJsonContext.Default.SmartHistoryEntry).ReplaceLineEndings(string.Empty) + "\n");
        return true;
    }

    public IReadOnlyList<SmartHistoryEntry> Load(DriveInfo drive)
    {
        var path = PathFor(drive);
        if (!System.IO.File.Exists(path))
        {
            return [];
        }

        var entries = new List<SmartHistoryEntry>();
        foreach (var line in System.IO.File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize(line, HistoryJsonContext.Default.SmartHistoryEntry) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A damaged line (e.g. power loss while writing) is skipped.
            }
        }

        return entries;
    }

    /// <summary>Attribute changes between consecutive entries, newest query first, attributes by id.</summary>
    public static IReadOnlyList<SmartChange> Changes(IReadOnlyList<SmartHistoryEntry> entries)
    {
        var changes = new List<SmartChange>();
        for (var i = entries.Count - 1; i >= 1; i--)
        {
            var before = entries[i - 1];
            var after = entries[i];
            foreach (var (id, value) in after.Values.OrderBy(v => v.Key))
            {
                if (!before.Values.TryGetValue(id, out var old))
                {
                    changes.Add(new SmartChange(after.Timestamp, id, value.Name, "–", value.Display));
                }
                else if (old.Raw != value.Raw || old.Current != value.Current)
                {
                    changes.Add(new SmartChange(after.Timestamp, id, value.Name, old.Display, value.Display));
                }
            }
        }

        return changes;
    }

    internal string PathFor(DriveInfo drive)
    {
        var key = $"{drive.Model}|{drive.Serial}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return System.IO.Path.Combine(_directory, $"{hash}.jsonl");
    }

    private static bool SameValues(SmartHistoryEntry a, SmartHistoryEntry b) =>
        a.Values.Count == b.Values.Count
        && a.Values.All(v => b.Values.TryGetValue(v.Key, out var other) && other.Raw == v.Value.Raw && other.Current == v.Value.Current);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SmartHistoryEntry))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
