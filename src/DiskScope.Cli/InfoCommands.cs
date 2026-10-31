using System.Globalization;
using System.Text.Json;
using DiskScope.Core;
using DiskScope.Core.Devices;
using DiskScope.Core.Serialization;

namespace DiskScope.Cli;

internal static class InfoCommands
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static int List(IDriveService drives, bool json)
    {
        var all = drives.GetDrives();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(all, DiskScopeJsonContext.Default.IReadOnlyListDriveInfo));
            return ExitCodes.Ok;
        }

        Console.WriteLine($"{"Nr",-3} {"Modell",-40} {"Größe",10} {"Bus",-6} {"Medium",-9} System");
        foreach (var drive in all)
        {
            Console.WriteLine(
                $"{drive.Id.Number,-3} {Truncate(drive.Model, 40),-40} {ByteSize.Format(drive.SizeBytes, Culture),10} " +
                $"{DisplayNames.Of(drive.Bus),-6} {DisplayNames.Of(drive.Media),-9} {(drive.IsSystemDisk ? "ja" : string.Empty)}");
        }

        return ExitCodes.Ok;
    }

    public static int Info(IDriveService drives, DriveId id, bool json)
    {
        var drive = drives.GetDrive(id);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(drive, DiskScopeJsonContext.Default.DriveInfo));
            WarnIfIncomplete(drive);
            return ExitCodes.Ok;
        }

        Console.WriteLine($"Laufwerk {drive.Id.Number}: {drive.Model}");
        Row("Gerätepfad", drive.DevicePath);
        Row("Hersteller", drive.Vendor);
        Row("Firmware", drive.Firmware);
        Row("Seriennummer", drive.Serial);
        Row("Schnittstelle", drive.Link is null ? DisplayNames.Of(drive.Bus) : $"{DisplayNames.Of(drive.Bus)}, {drive.Link}");
        Row("Medium", DisplayNames.Of(drive.Media));
        Row("Kapazität", $"{ByteSize.Format(drive.SizeBytes, Culture)} ({drive.SizeBytes.ToString("N0", Culture)} Byte)");
        Row("Sektorgröße", $"{drive.LogicalSectorSize} / {drive.PhysicalSectorSize} Byte (logisch / physisch)");
        Row("Puffergröße", drive.BufferSize is { } buffer ? ByteSize.FormatBinary(buffer, Culture) : null);
        Row("Systemlaufwerk", drive.IsSystemDisk ? "ja" : "nein");

        Console.WriteLine();
        Console.WriteLine("Funktionen");
        if (drive.Features.Count == 0)
        {
            Console.WriteLine("  (keine Angaben verfügbar)");
        }

        foreach (var feature in drive.Features)
        {
            var state = feature.Enabled switch
            {
                true => " (aktiv)",
                false when feature.Supported => " (inaktiv)",
                _ => string.Empty,
            };
            Console.WriteLine($"  [{(feature.Supported ? 'x' : ' ')}] {DisplayNames.Of(feature.Kind)}{state}");
        }

        Console.WriteLine();
        Console.WriteLine("Volumes");
        Console.WriteLine(
            $"  {"Bezeichnung",-24} {"Kapazität",10} {"Frei",10} {"Belegt",7} {"Dateisystem",-11} {"Seriennr.",-9} Ausrichtung");
        foreach (var volume in drive.Volumes)
        {
            var label = volume.Label.Length > 0 ? volume.Label : "(ohne Bezeichnung)";
            var name = volume.MountPoint.Length > 0 ? $"{label} ({volume.MountPoint})" : label;
            Console.WriteLine(
                $"  {Truncate(name, 24),-24} {ByteSize.Format(volume.Capacity, Culture),10} {ByteSize.Format(volume.Free, Culture),10} " +
                $"{volume.UsedPercent.ToString("0.0", Culture) + " %",7} {volume.FileSystem,-11} {volume.Serial,-9} " +
                $"{(volume.Alignment / 1024).ToString("N0", Culture)} KiB");
        }

        WarnIfIncomplete(drive);
        return ExitCodes.Ok;
    }

    private static void Row(string label, string? value) =>
        Console.WriteLine($"  {label,-15} {(string.IsNullOrEmpty(value) ? "–" : value)}");

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    /// <summary>Without IDENTIFY data only TRIM is known; on some drivers this needs admin rights.</summary>
    private static void WarnIfIncomplete(DriveInfo drive)
    {
        if (drive.Features.Count <= 1 && !Privileges.IsElevated)
        {
            Console.Error.WriteLine("Hinweis: Ohne Administratorrechte sind nicht alle Angaben verfügbar.");
        }
    }
}
