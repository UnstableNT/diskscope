using System.Globalization;
using System.Text.Json;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;
using DiskScope.Core.Serialization;

namespace DiskScope.Cli;

internal static class BenchCommands
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static async Task<int> BenchAsync(
        IDriveService drives,
        BenchmarkRunner runner,
        DriveId id,
        string block,
        string range,
        int points,
        bool json,
        CancellationToken cancellationToken)
    {
        var drive = drives.GetDrive(id);
        var options = new BenchmarkOptions(
            BlockSize: BlockSize.Parse(block),
            Range: DriveRange.Parse(range, drive.SizeBytes),
            Points: points);

        PrintHeader(drive, options.Range!.Value, json);
        var progress = new ConsoleProgress(json);
        var result = await runner.RunAsync(
            drive, options, new Progress<BenchmarkProgress>(p => progress.Report(p.Fraction, p.Phase switch
            {
                BenchmarkPhase.Transfer => "Transferrate",
                BenchmarkPhase.AccessTime => "Zugriffszeit",
                _ => "Burst-Rate",
            })), cancellationToken).ConfigureAwait(false);
        progress.Done();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.BenchmarkResult));
            return ExitCodes.Ok;
        }

        Console.WriteLine($"Blockgröße      {ByteSize.FormatBinary((ulong)result.BlockSize, Culture)}");
        Console.WriteLine($"Minimum         {Rate(result.MinimumMBps)}");
        Console.WriteLine($"Maximum         {Rate(result.MaximumMBps)}");
        Console.WriteLine($"Durchschnitt    {Rate(result.AverageMBps)}");
        Console.WriteLine($"Zugriffszeit    {(result.AccessTimeMs is { } ms ? ms.ToString("0.000", Culture) + " ms" : "–")}");
        Console.WriteLine($"Burst-Rate      {Rate(result.BurstMBps)}");
        Console.WriteLine($"CPU-Last        {result.CpuPercent.ToString("0.0", Culture)} %");
        Console.WriteLine($"Lesefehler      {result.ReadErrors}");
        Console.WriteLine($"Dauer           {result.Duration.TotalSeconds.ToString("0.0", Culture)} s");
        return result.ReadErrors > 0 ? ExitCodes.Error : ExitCodes.Ok;
    }

    public static async Task<int> RandomAsync(
        IDriveService drives,
        RandomAccessRunner runner,
        DriveId id,
        string sizes,
        bool align4K,
        string range,
        string duration,
        bool json,
        CancellationToken cancellationToken)
    {
        var drive = drives.GetDrive(id);
        var options = new RandomAccessOptions(
            Sizes: ParseSizes(sizes),
            Align4K: align4K,
            Range: DriveRange.Parse(range, drive.SizeBytes),
            DurationPerSize: SmartCommands.ParseInterval(duration));

        PrintHeader(drive, options.Range!.Value, json);
        var progress = new ConsoleProgress(json);
        var result = await runner.RunAsync(
            drive, options, new Progress<RandomAccessProgress>(p => progress.Report(p.Fraction, SizeText(p.Size))), cancellationToken)
            .ConfigureAwait(false);
        progress.Done();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.RandomAccessResult));
            return ExitCodes.Ok;
        }

        Console.WriteLine($"{"Größe",-10} {"IOPS",10} {"Mittel ms",10} {"Max ms",10} {"MB/s",10}");
        foreach (var s in result.Sizes)
        {
            Console.WriteLine(
                $"{SizeText(s.Size),-10} {s.OperationsPerSecond.ToString("N0", Culture),10} {s.AverageMs.ToString("0.000", Culture),10} " +
                $"{s.MaximumMs.ToString("0.000", Culture),10} {s.AverageMBps.ToString("0.0", Culture),10}");
        }

        Console.WriteLine($"Lesefehler: {result.ReadErrors}");
        return result.ReadErrors > 0 ? ExitCodes.Error : ExitCodes.Ok;
    }

    internal static IReadOnlyList<int> ParseSizes(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Equals("random", StringComparison.OrdinalIgnoreCase) || s.Equals("zufällig", StringComparison.OrdinalIgnoreCase)
                ? RandomAccessOptions.Mixed
                : BlockSize.Parse(s))
            .ToArray();

    private static string SizeText(int size) =>
        size == RandomAccessOptions.Mixed ? "zufällig" : ByteSize.FormatBinary((ulong)size, Culture);

    private static string Rate(double? mbps) => mbps is { } v ? v.ToString("0.0", Culture) + " MB/s" : "–";

    private static void PrintHeader(DriveInfo drive, DriveRange range, bool json)
    {
        if (json)
        {
            return;
        }

        Console.WriteLine($"Laufwerk {drive.Id.Number}: {drive.Model} ({ByteSize.Format(drive.SizeBytes, Culture)})");
        Console.WriteLine($"Bereich         {ByteSize.Format(range.Start, Culture)} – {ByteSize.Format(range.End, Culture)}");
    }

    /// <summary>Single progress line on stderr; silent for JSON output.</summary>
    private sealed class ConsoleProgress(bool silent)
    {
        private int _lastPercent = -1;

        public void Report(double fraction, string phase)
        {
            var percent = (int)(fraction * 100);
            if (silent || percent == _lastPercent || Console.IsErrorRedirected)
            {
                return;
            }

            _lastPercent = percent;
            Console.Error.Write($"\r{phase,-14} {percent,3} %   ");
        }

        public void Done()
        {
            if (!silent && !Console.IsErrorRedirected)
            {
                Console.Error.Write("\r" + new string(' ', 30) + "\r");
            }
        }
    }
}
