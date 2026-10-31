using System.Globalization;
using System.Text.Json;
using DiskScope.Core;
using DiskScope.Core.Devices;
using DiskScope.Core.Serialization;
using DiskScope.Core.Smart;

namespace DiskScope.Cli;

internal static class SmartCommands
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static int Smart(IDriveService drives, ISmartService smart, DriveId id, bool json)
    {
        var drive = drives.GetDrive(id);
        var result = smart.Read(drive);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DiskScopeJsonContext.Default.SmartResult));
        }
        else
        {
            PrintTable(drive, result);
        }

        if (result.UnsupportedReason is { } reason)
        {
            Console.Error.WriteLine($"Hinweis: {reason}.");
        }

        return ExitCode(result);
    }

    /// <summary>Starts, aborts or queries a SMART self-test (SPEC 4.3).</summary>
    public static int SelfTest(IDriveService drives, ISelfTestService selfTests, DriveId id, string action)
    {
        var drive = drives.GetDrive(id);
        var status = action.ToUpperInvariant() switch
        {
            "SHORT" => selfTests.Start(drive, SelfTestKind.Quick),
            "EXTENDED" => selfTests.Start(drive, SelfTestKind.Extended),
            "ABORT" => selfTests.Abort(drive),
            "STATUS" => selfTests.Query(drive),
            _ => throw new ArgumentException($"Unbekannte Selbsttest-Aktion „{action}“ (short, extended, abort, status)."),
        };

        Console.WriteLine($"Laufwerk {drive.Id.Number}: {drive.Model}");
        Console.WriteLine(status.Text);
        if (status.ShortMinutes is { } s && status.ExtendedMinutes is { } e)
        {
            Console.WriteLine($"Empfohlene Dauer: kurz ≈ {s} min, erweitert ≈ {e} min");
        }

        return status.State switch
        {
            SelfTestState.Failed => ExitCodes.HealthWarning,
            SelfTestState.NotSupported when !Privileges.IsElevated && drive.Bus is BusType.Sata or BusType.Raid => ExitCodes.MissingRights,
            SelfTestState.NotSupported => ExitCodes.Error,
            _ => ExitCodes.Ok,
        };
    }

    public static async Task<int> TemperatureAsync(
        IDriveService drives, ISmartService smart, string? watch, bool fahrenheit, bool json, CancellationToken cancellationToken)
    {
        var interval = watch is null ? (TimeSpan?)null : ParseInterval(watch);
        var all = drives.GetDrives();
        var history = new Dictionary<int, (int Min, int Max)>();

        try
        {
            while (true)
            {
                var readings = all.Select(smart.ReadTemperature).ToArray();
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize<IReadOnlyList<TemperatureReading>>(
                        readings, DiskScopeJsonContext.Default.IReadOnlyListTemperatureReading));
                }
                else
                {
                    PrintTemperatures(all, readings, history, fahrenheit, interval is not null);
                }

                if (interval is null)
                {
                    return ExitCodes.Ok;
                }

                await Task.Delay(interval.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (interval is not null)
        {
            // Ctrl+C ends --watch normally.
            return ExitCodes.Ok;
        }
    }

    internal static TimeSpan ParseInterval(string text)
    {
        text = text.Trim().ToLowerInvariant();
        var (number, factor) = text switch
        {
            _ when text.EndsWith("ms", StringComparison.Ordinal) => (text[..^2], 0.001),
            _ when text.EndsWith('s') => (text[..^1], 1.0),
            _ when text.EndsWith('m') => (text[..^1], 60.0),
            _ when text.EndsWith('h') => (text[..^1], 3600.0),
            _ => (text, 1.0),
        };

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new ArgumentException($"Ungültiges Intervall „{text}“ (Beispiele: 30s, 5m, 1h).");
        }

        var seconds = Math.Max(1, value * factor);
        return TimeSpan.FromSeconds(seconds);
    }

    internal static int ExitCode(SmartResult result) => result switch
    {
        { Overall: HealthStatus.Warning or HealthStatus.Error } => ExitCodes.HealthWarning,
        { Attributes.Count: 0 } when !Privileges.IsElevated => ExitCodes.MissingRights,
        _ => ExitCodes.Ok,
    };

    private static void PrintTable(DriveInfo drive, SmartResult result)
    {
        Console.WriteLine($"Laufwerk {drive.Id.Number}: {drive.Model}");
        Console.WriteLine();

        if (result.Attributes.Count > 0)
        {
            var nvme = result.Source == SmartSource.NvmeLog;
            Console.WriteLine(
                $"{"ID",-4} {"Name",-38} {"Wert",-14} {"Akt",4} {"Schl",4} {"Schw",4} {"Rohdaten",-14} Status");
            foreach (var a in result.Attributes)
            {
                Console.WriteLine(
                    $"{a.Id.ToString("X2", CultureInfo.InvariantCulture),-4} {Truncate(a.Name, 38),-38} {a.Value,-14} " +
                    $"{Format(a.Current),4} {Format(a.Worst),4} {Format(a.Threshold),4} " +
                    $"{(nvme ? a.RawValue.ToString("N0", Culture) : a.Raw),-14} {StatusText(a.Status)}");
            }

            Console.WriteLine();
        }

        Console.WriteLine($"Gesamtstatus:  {StatusText(result.Overall)}");
        Console.WriteLine($"Quelle:        {SourceText(result)}");
        Console.WriteLine($"Temperatur:    {(result.TemperatureCelsius is { } t ? $"{t} °C" : "–")}");
    }

    private static void PrintTemperatures(
        IReadOnlyList<DriveInfo> drives,
        TemperatureReading[] readings,
        Dictionary<int, (int Min, int Max)> history,
        bool fahrenheit,
        bool watching)
    {
        if (watching)
        {
            Console.WriteLine(DateTime.Now.ToString("T", Culture));
        }

        for (var i = 0; i < drives.Count; i++)
        {
            var drive = drives[i];
            var reading = readings[i];
            var line = $"  {drive.Id.Number,-3} {Truncate(drive.Model, 36),-36} ";
            if (reading.Celsius is not { } celsius)
            {
                Console.WriteLine(line + "–");
                continue;
            }

            var (min, max) = history.TryGetValue(drive.Id.Number, out var h) ? h : (celsius, celsius);
            history[drive.Id.Number] = (Math.Min(min, celsius), Math.Max(max, celsius));
            min = Math.Min(min, celsius);
            max = Math.Max(max, celsius);

            var warning = reading.WarningCelsius ?? TemperatureLimits.DefaultWarningCelsius(drive.Media);
            var flag = celsius >= warning ? $"  ! über Warnschwelle {Degrees(warning, fahrenheit)}" : string.Empty;
            var range = watching ? $"  (min {Degrees(min, fahrenheit)}, max {Degrees(max, fahrenheit)})" : string.Empty;
            Console.WriteLine($"{line}{Degrees(celsius, fahrenheit),8}{range}{flag}");
        }
    }

    private static string Degrees(int celsius, bool fahrenheit) => fahrenheit
        ? string.Create(Culture, $"{TemperatureLimits.ToFahrenheit(celsius):0} °F")
        : string.Create(Culture, $"{celsius} °C");

    private static string Format(byte? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string StatusText(HealthStatus status) => status switch
    {
        HealthStatus.Ok => "OK",
        HealthStatus.Warning => "Warnung",
        HealthStatus.Error => "Fehler",
        _ => "Unbekannt",
    };

    private static string SourceText(SmartResult result) => result.Source switch
    {
        SmartSource.AtaPassThrough => "ATA-Pass-Through",
        SmartSource.Sat => "SAT (SCSI/ATA Translation)",
        SmartSource.LegacyIoctl => "SMART-IOCTL (älterer Treiber)",
        SmartSource.PredictFailure => result.Attributes.Count > 0
            ? "IOCTL_STORAGE_PREDICT_FAILURE (Werte ohne Schwellwerte)"
            : "IOCTL_STORAGE_PREDICT_FAILURE (nur Ja/Nein)",
        SmartSource.NvmeLog => "NVMe SMART/Health-Log",
        _ => "S.M.A.R.T. nicht verfügbar",
    };
}
