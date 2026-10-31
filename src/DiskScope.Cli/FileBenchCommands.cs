using System.Globalization;
using System.Text.Json;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Serialization;

namespace DiskScope.Cli;

internal static class FileBenchCommands
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static async Task<int> RunAsync(
        FileBenchmarkRunner runner,
        string volume,
        string size,
        string pattern,
        int queueDepth,
        bool blockSizes,
        bool json,
        CancellationToken cancellationToken)
    {
        // "D:" means the root of drive D, not the current directory on D.
        var target = volume.Length == 2 && volume[1] == ':' ? volume + "\\" : volume;
        var options = new FileBenchmarkOptions(
            target,
            ByteSize.ParseBinary(size),
            pattern.Equals("zero", StringComparison.OrdinalIgnoreCase) ? DataPattern.Zero
                : pattern.Equals("random", StringComparison.OrdinalIgnoreCase) ? DataPattern.Random
                : throw new ArgumentException($"Unbekanntes Muster „{pattern}“ (zero oder random)."),
            Math.Clamp(queueDepth, 1, 256),
            BlockSizeTest: blockSizes);

        if (!json)
        {
            Console.WriteLine($"Datei-Benchmark auf {target}, Testdatei {ByteSize.FormatBinary((ulong)options.FileSize, Culture)}, Muster {options.Pattern}, QD {options.QueueDepth}");
        }

        var interactive = !json && !Console.IsErrorRedirected;
        var result = await runner.RunAsync(
            options,
            new Progress<FileBenchmarkProgress>(p =>
            {
                if (interactive)
                {
                    Console.Error.Write($"\r{p.Phase,-16} {(int)(p.Fraction * 100),3} %   ");
                }
            }),
            cancellationToken).ConfigureAwait(false);
        if (interactive)
        {
            Console.Error.Write("\r" + new string(' ', 30) + "\r");
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.FileBenchmarkResult));
            return ExitCodes.Ok;
        }

        Console.WriteLine();
        Console.WriteLine($"{"Test",-26} {"Lesen MB/s",12} {"Lesen IOPS",12} {"Schreiben MB/s",15} {"Schreiben IOPS",15}");
        foreach (var row in result.Rows)
        {
            Console.WriteLine(
                $"{RowName(row.Kind, result.QueueDepth),-26} {row.ReadMBps.ToString("0.0", Culture),12} {Iops(row.ReadIops),12} " +
                $"{row.WriteMBps.ToString("0.0", Culture),15} {Iops(row.WriteIops),15}");
        }

        if (result.BlockSizes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{"Blockgröße",-12} {"Lesen MB/s",12} {"Schreiben MB/s",15}");
            foreach (var row in result.BlockSizes)
            {
                Console.WriteLine($"{ByteSize.FormatBinary((ulong)row.BlockSize, Culture),-12} {row.ReadMBps.ToString("0.0", Culture),12} {row.WriteMBps.ToString("0.0", Culture),15}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Dauer {result.Duration.TotalSeconds.ToString("0.0", Culture)} s");
        return ExitCodes.Ok;
    }

    internal static string RowName(FileTestKind kind, int queueDepth) => kind switch
    {
        FileTestKind.Sequential => "Sequenziell",
        FileTestKind.Random4KSingle => "4 KiB zufällig (QD1)",
        _ => $"4 KiB zufällig (QD{queueDepth})",
    };

    private static string Iops(double iops) => iops > 0 ? iops.ToString("N0", Culture) : "–";
}
