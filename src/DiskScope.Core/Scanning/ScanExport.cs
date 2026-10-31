using System.Globalization;
using System.Text;
using System.Text.Json;
using DiskScope.Core.Serialization;

namespace DiskScope.Core.Scanning;

/// <summary>Exports the damaged LBAs of a scan as CSV or JSON (SPEC 4.5).</summary>
public static class ScanExport
{
    public static string ToCsv(ScanResult result)
    {
        var builder = new StringBuilder("lba,byte_offset\n");
        foreach (var lba in result.BadLbas)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{lba},{lba * result.SectorSize}\n");
        }

        return builder.ToString();
    }

    public static string ToJson(ScanResult result) =>
        JsonSerializer.Serialize(
            new BadLbaExport(result.Drive.Number, result.SectorSize, result.BadLbasTruncated, result.BadLbas),
            DiskScopeJsonContext.Default.BadLbaExport);

    /// <summary>Writes CSV or JSON depending on the file extension (.csv / .json).</summary>
    public static void Write(ScanResult result, string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        var content = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ToCsv(result)
            : extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ? ToJson(result)
            : throw new ArgumentException($"Unbekanntes Exportformat „{extension}“ (erlaubt: .csv, .json).");
        System.IO.File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}

public sealed record BadLbaExport(int Drive, uint SectorSize, bool Truncated, IReadOnlyList<ulong> BadLbas);
