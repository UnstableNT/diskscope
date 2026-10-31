using System.Globalization;
using System.Text.Json;
using DiskScope.Core;
using DiskScope.Core.Devices;
using DiskScope.Core.Monitoring;
using DiskScope.Core.Serialization;

namespace DiskScope.Cli;

internal static class MonitorCommands
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    /// <summary>Records disk I/O for a fixed time and prints the statistics (addition to SPEC 5 for automation).</summary>
    public static async Task<int> RunAsync(
        IDriveService drives, DiskMonitor monitor, DriveId id, string duration, bool json, CancellationToken cancellationToken)
    {
        var drive = drives.GetDrive(id);
        var length = SmartCommands.ParseInterval(duration);
        var statistics = new MonitorStatistics((long)drive.SizeBytes, TimeSpan.FromMinutes(30), DateTime.Now);
        if (!json)
        {
            Console.WriteLine($"Laufwerk {drive.Id.Number}: {drive.Model}, Aufzeichnung {length.TotalSeconds:0} s …");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(length);
        try
        {
            await monitor.RunAsync(drive.Id.Number, statistics, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Regular end of the recording time.
        }

        var snapshot = statistics.Snapshot(DateTime.Now);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot, DiskScopeJsonContext.Default.MonitorSnapshot));
            return ExitCodes.Ok;
        }

        Console.WriteLine($"{"",-10} {"Menge",12} {"I/O",10} {"Max KB/s",12} {"Ø Größe",10}");
        Line("Lesen", snapshot.Read);
        Line("Schreiben", snapshot.Write);
        Console.WriteLine();
        Console.WriteLine("Programme");
        foreach (var p in snapshot.Processes.Take(10))
        {
            Console.WriteLine($"  {Truncate(p.Name, 30),-30} {p.ProcessId,7}  gelesen {ByteSize.Format((ulong)p.ReadBytes, Culture),10}  geschrieben {ByteSize.Format((ulong)p.WriteBytes, Culture),10}");
        }

        return ExitCodes.Ok;
    }

    private static void Line(string name, DirectionTotals totals) =>
        Console.WriteLine(
            $"{name,-10} {ByteSize.Format((ulong)totals.Bytes, Culture),12} {totals.Count.ToString("N0", Culture),10} " +
            $"{totals.MaximumKBps.ToString("N0", Culture),12} {ByteSize.FormatBinary((ulong)totals.AverageRequestBytes, Culture),10}");

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
