using System.Globalization;
using System.Text.Json;
using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Destructive;
using DiskScope.Core.Devices;
using DiskScope.Core.Serialization;

namespace DiskScope.Cli;

/// <summary>Options shared by all writing commands (SPEC 9).</summary>
internal sealed record WriteRequest(DriveId Drive, string Range, bool Dismount, bool DryRun, string? Confirmation);

/// <summary>CLI for raw-device write operations; every path goes through <see cref="WriteGuard"/>.</summary>
internal static class DestructiveCommands
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static async Task<int> WriteBenchAsync(
        IDriveService drives, DestructiveOperations operations, WriteRequest request, string block, int points, bool json, CancellationToken cancellationToken)
    {
        if (Prepare(drives, request) is not { } authorization)
        {
            return request.DryRun ? DryRunExit(drives, request) : ExitCodes.Error;
        }

        var result = await operations.WriteBenchmarkAsync(authorization, new BenchmarkOptions(BlockSize.Parse(block), Points: points), null, cancellationToken).ConfigureAwait(false);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.BenchmarkResult));
            return ExitCodes.Ok;
        }

        Console.WriteLine($"Schreib-Benchmark: Ø {Rate(result.AverageMBps)}, min {Rate(result.MinimumMBps)}, max {Rate(result.MaximumMBps)}, Zugriff {result.AccessTimeMs:0.000} ms, Burst {Rate(result.BurstMBps)}");
        return ExitCodes.Ok;
    }

    public static async Task<int> WriteRandomAsync(
        IDriveService drives, DestructiveOperations operations, WriteRequest request, string sizes, bool align4K, string duration, bool json, CancellationToken cancellationToken)
    {
        if (Prepare(drives, request) is not { } authorization)
        {
            return request.DryRun ? DryRunExit(drives, request) : ExitCodes.Error;
        }

        var options = new RandomAccessOptions(BenchCommands.ParseSizes(sizes), align4K, DurationPerSize: SmartCommands.ParseInterval(duration));
        var result = await operations.WriteRandomAccessAsync(authorization, options, null, cancellationToken).ConfigureAwait(false);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.RandomAccessResult));
            return ExitCodes.Ok;
        }

        foreach (var s in result.Sizes)
        {
            Console.WriteLine($"Schreiben {(s.Size == 0 ? "zufällig" : ByteSize.FormatBinary((ulong)s.Size, Culture)),-10} {s.OperationsPerSecond.ToString("N0", Culture),10} IOPS {s.AverageMs.ToString("0.000", Culture),8} ms {s.AverageMBps.ToString("0.0", Culture),8} MB/s");
        }

        return ExitCodes.Ok;
    }

    public static async Task<int> WriteExtraAsync(
        IDriveService drives, DestructiveOperations operations, WriteRequest request, string? tests, bool align4K, bool json, CancellationToken cancellationToken)
    {
        if (Prepare(drives, request) is not { } authorization)
        {
            return request.DryRun ? DryRunExit(drives, request) : ExitCodes.Error;
        }

        var result = await operations.WriteExtraTestsAsync(authorization, new ExtraTestOptions(ExtraCommands.ParseTests(tests), align4K), null, cancellationToken).ConfigureAwait(false);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.ExtraTestResult));
            return ExitCodes.Ok;
        }

        foreach (var row in result.Rows)
        {
            Console.WriteLine($"Schreiben {ExtraCommands.Name(row.Kind, row.TransferSize),-34} {row.Iops.ToString("N0", Culture),10} I/O/s {row.MBps.ToString("0.0", Culture),8} MB/s");
        }

        return ExitCodes.Ok;
    }

    public static async Task<int> EraseAsync(
        IDriveService drives, DestructiveOperations operations, WriteRequest request, string pattern, int passes, bool verify, bool json, CancellationToken cancellationToken)
    {
        var options = new EraseOptions(ParsePattern(pattern, out var fixedByte), fixedByte, Math.Max(1, passes), verify);
        if (Prepare(drives, request) is not { } authorization)
        {
            return request.DryRun ? DryRunExit(drives, request) : ExitCodes.Error;
        }

        var interactive = !json && !Console.IsErrorRedirected;
        var result = await operations.EraseAsync(
            authorization,
            options,
            new Progress<EraseProgress>(p =>
            {
                if (interactive)
                {
                    Console.Error.Write($"\rDurchgang {p.Pass}/{p.Passes}  {(int)(p.Fraction * 100),3} %  Fehler {p.Errors}   ");
                }
            }),
            cancellationToken).ConfigureAwait(false);
        if (interactive)
        {
            Console.Error.Write("\r" + new string(' ', 50) + "\r");
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.EraseResult));
        }
        else
        {
            Console.WriteLine($"Gelöscht: {ByteSize.Format(result.BytesWritten, Culture)} in {result.Passes} Durchgang/Durchgängen, Muster {result.Pattern}, Prüflesen {(result.Verified ? "ja" : "nein")}, Fehler {result.Errors}, Dauer {result.Duration.TotalSeconds:0.0} s");
        }

        return result.Errors > 0 ? ExitCodes.Error : ExitCodes.Ok;
    }

    public static int AcousticPower(IDriveService drives, IAcousticPowerService service, DriveId id, bool aam, string? set, bool confirmed)
    {
        var drive = drives.GetDrive(id);
        var state = set is null
            ? service.Read(drive)
            : aam
                ? service.SetAam(drive, ParseLevel(set), confirmed)
                : service.SetApm(drive, ParseLevel(set), confirmed);

        Console.WriteLine($"Laufwerk {drive.Id.Number}: {drive.Model}");
        Console.WriteLine(aam
            ? state.AamSupported
                ? $"AAM: {(state.AamEnabled ? $"aktiv, Stufe 0x{state.AamLevel:X2} (empfohlen 0x{state.AamRecommended:X2})" : "aus")}"
                : "AAM wird nicht unterstützt"
            : state.ApmSupported
                ? $"APM: {(state.ApmEnabled ? $"aktiv, Stufe 0x{state.ApmLevel:X2}" : "aus")}"
                : "APM wird nicht unterstützt");
        return ExitCodes.Ok;
    }

    internal static ErasePattern ParsePattern(string text, out byte fixedByte)
    {
        fixedByte = 0;
        if (text.Equals("zero", StringComparison.OrdinalIgnoreCase))
        {
            return ErasePattern.Zero;
        }

        if (text.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            return ErasePattern.Random;
        }

        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out fixedByte)
            ? ErasePattern.Fixed
            : throw new ArgumentException($"Unbekanntes Muster „{text}“ (zero, random oder ein Byte wie 0xAA).");
    }

    private static int? ParseLevel(string text) =>
        text.Equals("off", StringComparison.OrdinalIgnoreCase)
            ? null
            : int.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                ? level
                : throw new ArgumentException($"Ungültige Stufe „{text}“ (Zahl, 0x.. oder off).");

    /// <summary>Returns an authorization, or null after printing the dry-run plan.</summary>
    private static WriteAuthorization? Prepare(IDriveService drives, WriteRequest request)
    {
        var guard = new WriteGuard(drives);
        var drive = drives.GetDrive(request.Drive);
        var range = DriveRange.Parse(request.Range, drive.SizeBytes);
        if (request.DryRun)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.Confirmation))
        {
            throw new WriteRefusedException(
                $"Schreibende Operationen löschen Daten. Erst mit --dry-run prüfen, dann mit --i-understand-data-loss <Seriennummer von Laufwerk {drive.Id.Number}> bestätigen.");
        }

        return guard.Authorize(request.Drive, range, request.Dismount, request.Confirmation, ConfirmationKind.Serial);
    }

    private static int DryRunExit(IDriveService drives, WriteRequest request)
    {
        var drive = drives.GetDrive(request.Drive);
        var plan = new WriteGuard(drives).Plan(request.Drive, DriveRange.Parse(request.Range, drive.SizeBytes), request.Dismount);
        Console.WriteLine($"Probelauf – es wird nichts geschrieben.");
        Console.WriteLine($"Laufwerk {plan.Drive.Number}: {plan.Model} (Seriennummer {plan.Serial ?? "–"})");
        Console.WriteLine($"Betroffener Bereich: {ByteSize.Format(plan.Range.Start, Culture)} – {ByteSize.Format(plan.Range.End, Culture)} ({plan.Bytes.ToString("N0", Culture)} Byte, LBA {plan.Range.Start / drive.LogicalSectorSize}–{(plan.Range.End / drive.LogicalSectorSize) - 1})");
        foreach (var volume in plan.Volumes)
        {
            Console.WriteLine($"Betroffenes Volume: {(volume.MountPoint.Length > 0 ? volume.MountPoint : "(ohne Buchstabe)")} {volume.Label} {volume.FileSystem} {ByteSize.Format(volume.Capacity, Culture)}{(plan.DismountVolumes ? " – wird ausgehängt" : string.Empty)}");
        }

        foreach (var refusal in plan.Refusals)
        {
            Console.WriteLine($"Gesperrt: {refusal}");
        }

        Console.WriteLine(plan.Allowed
            ? $"Zulässig. Zum Ausführen: --i-understand-data-loss {WriteGuard.ExpectedConfirmation(drive, ConfirmationKind.Serial)}"
            : "Nicht zulässig.");
        return plan.Allowed ? ExitCodes.Ok : ExitCodes.Error;
    }

    private static string Rate(double? mbps) => mbps is { } v ? v.ToString("0.0", Culture) + " MB/s" : "–";
}
