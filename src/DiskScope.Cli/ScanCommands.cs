using System.Globalization;
using System.Text;
using System.Text.Json;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;
using DiskScope.Core.Scanning;
using DiskScope.Core.Serialization;

namespace DiskScope.Cli;

internal static class ScanCommands
{
    private const int MapColumns = 64;
    private const int MapRows = 16;

    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static async Task<int> ScanAsync(
        IDriveService drives,
        ErrorScanner scanner,
        DriveId id,
        bool quick,
        string range,
        int blocks,
        string? export,
        bool json,
        CancellationToken cancellationToken)
    {
        var drive = drives.GetDrive(id);
        var options = new ScanOptions(DriveRange.Parse(range, drive.SizeBytes), blocks, quick);
        if (!json)
        {
            Console.WriteLine($"Laufwerk {drive.Id.Number}: {drive.Model} ({ByteSize.Format(drive.SizeBytes, Culture)})");
            Console.WriteLine($"{(quick ? "Schnellscan" : "Vollständiger Scan")}, {blocks} Blöcke, nur lesend");
        }

        var interactive = !json && !Console.IsErrorRedirected;
        var lastPercent = -1;
        var progress = new Progress<ScanProgress>(p =>
        {
            var percent = (int)(p.Fraction * 100);
            if (interactive && percent != lastPercent)
            {
                lastPercent = percent;
                Console.Error.Write(
                    $"\r{percent,3} %  Position {ByteSize.Format(p.Position, Culture),10}  beschädigt {p.DamagedBlocks}   ");
            }
        });

        ScanResult result;
        try
        {
            result = await scanner.ScanAsync(drive, options, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (interactive)
            {
                Console.Error.Write("\r" + new string(' ', 60) + "\r");
            }
        }

        if (export is not null)
        {
            ScanExport.Write(result, export);
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.ScanResult));
        }
        else
        {
            PrintSummary(result, export);
        }

        return result.DamagedBlocks > 0 ? ExitCodes.HealthWarning : ExitCodes.Ok;
    }

    private static void PrintSummary(ScanResult result, string? export)
    {
        Console.WriteLine();
        foreach (var line in RenderMap(result.Blocks, MapColumns, MapRows))
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine("  Legende: . gut   # beschädigt   (leer) ungeprüft");
        Console.WriteLine();
        Console.WriteLine($"Blöcke          {result.Blocks.Count} à {ByteSize.FormatBinary(result.BlockSize, Culture)}");
        Console.WriteLine($"Beschädigt      {result.DamagedBlocks} ({result.DamagedPercent.ToString("0.###", Culture)} %)");
        Console.WriteLine($"Defekte LBAs    {result.BadLbas.Count}{(result.BadLbasTruncated ? " (Liste gekürzt)" : string.Empty)}");
        Console.WriteLine($"Geschwindigkeit {result.AverageMBps.ToString("0.0", Culture)} MB/s");
        Console.WriteLine($"Dauer           {result.Duration.TotalSeconds.ToString("0.0", Culture)} s");
        if (export is not null)
        {
            Console.WriteLine($"Export          {export}");
        }
    }

    /// <summary>Condenses the block states into a character map; a cell is damaged if any of its blocks is.</summary>
    internal static IReadOnlyList<string> RenderMap(IReadOnlyList<BlockState> blocks, int columns, int rows)
    {
        var cells = Math.Min(blocks.Count, columns * rows);
        if (cells == 0)
        {
            return [];
        }

        var builder = new StringBuilder(cells);
        for (var cell = 0; cell < cells; cell++)
        {
            var from = (int)((long)cell * blocks.Count / cells);
            var to = (int)((long)(cell + 1) * blocks.Count / cells);
            var state = BlockState.Unchecked;
            for (var i = from; i < to; i++)
            {
                if (blocks[i] == BlockState.Damaged)
                {
                    state = BlockState.Damaged;
                    break;
                }

                if (blocks[i] == BlockState.Good)
                {
                    state = BlockState.Good;
                }
            }

            builder.Append(state switch { BlockState.Damaged => '#', BlockState.Good => '.', _ => ' ' });
        }

        var text = builder.ToString();
        return Enumerable.Range(0, (text.Length + columns - 1) / columns)
            .Select(r => text.Substring(r * columns, Math.Min(columns, text.Length - (r * columns))))
            .ToArray();
    }
}
