using System.Globalization;
using System.Text.Json;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;
using DiskScope.Core.Serialization;

namespace DiskScope.Cli;

internal static class ExtraCommands
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static async Task<int> RunAsync(
        IDriveService drives,
        ExtraTestRunner runner,
        DriveId id,
        string? tests,
        bool align4K,
        string range,
        bool cache,
        bool json,
        CancellationToken cancellationToken)
    {
        var drive = drives.GetDrive(id);
        var options = new ExtraTestOptions(ParseTests(tests), align4K, DriveRange.Parse(range, drive.SizeBytes), CacheTest: cache);
        if (!json)
        {
            Console.WriteLine($"Laufwerk {drive.Id.Number}: {drive.Model}, Zusatztests (lesend)");
        }

        var result = await runner.RunAsync(drive, options, null, cancellationToken).ConfigureAwait(false);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.ExtraTestResult));
            return ExitCodes.Ok;
        }

        Console.WriteLine($"{"Test",-34} {"I/O/s",10} {"Zeit ms",10} {"MB/s",10}");
        foreach (var row in result.Rows)
        {
            Console.WriteLine(
                $"{Name(row.Kind, row.TransferSize),-34} {row.Iops.ToString("N0", Culture),10} {row.AverageMs.ToString("0.000", Culture),10} {row.MBps.ToString("0.0", Culture),10}");
        }

        if (result.Cache.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Cache-Test (Durchsatz je Bereichsgröße)");
            foreach (var point in result.Cache)
            {
                Console.WriteLine($"  {ByteSize.FormatBinary((ulong)point.RegionBytes, Culture),10} {point.MBps.ToString("0.0", Culture),10} MB/s");
            }
        }

        Console.WriteLine($"Lesefehler: {result.ReadErrors}");
        return result.ReadErrors > 0 ? ExitCodes.Error : ExitCodes.Ok;
    }

    /// <summary>Names as in the GUI list, e.g. "Zufällige Positionierung (4 KiB)".</summary>
    public static string Name(ExtraTestKind kind, int size) => kind switch
    {
        ExtraTestKind.RandomSector or ExtraTestKind.Random4K or ExtraTestKind.Random64K or ExtraTestKind.Random8M
            => $"Zufällige Positionierung ({ByteSize.FormatBinary((ulong)size, Culture)})",
        ExtraTestKind.Butterfly => "Butterfly-Positionierung",
        ExtraTestKind.SequentialOuter => "Sequenziell außen",
        ExtraTestKind.SequentialMiddle => "Sequenziell Mitte",
        ExtraTestKind.SequentialInner => "Sequenziell innen",
        _ => "Burst-Rate",
    };

    internal static IReadOnlyList<ExtraTestKind>? ParseTests(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => Enum.TryParse<ExtraTestKind>(t, ignoreCase: true, out var kind)
                    ? kind
                    : throw new ArgumentException($"Unbekannter Test „{t}“. Möglich: {string.Join(", ", Enum.GetNames<ExtraTestKind>())}."))
                .ToArray();
}
