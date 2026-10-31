using System.CommandLine;
using System.Text;
using DiskScope.Cli;
using DiskScope.Core.Devices;
using DiskScope.Core.Smart;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = Encoding.UTF8;

var drives = new DriveService();

var verboseOption = new Option<bool>("--verbose") { Description = "Protokoll zusätzlich auf stderr ausgeben", Recursive = true };
var jsonOption = new Option<bool>("--json") { Description = "Ausgabe als JSON" };
var driveArgument = new Argument<int>("drive") { Description = "PhysicalDrive-Nummer" };
var watchOption = new Option<string?>("--watch") { Description = "Fortlaufend abfragen im Intervall, z. B. 60s, 5m" };
var fahrenheitOption = new Option<bool>("--fahrenheit") { Description = "Temperaturen in °F anzeigen" };

// Options of writing operations (SPEC 9): nothing is written without --i-understand-data-loss <seriennummer>.
var writeOption = new Option<bool>("--write") { Description = "Schreibend messen (löscht Daten im Bereich!)" };
var confirmOption = new Option<string?>("--i-understand-data-loss") { Description = "Bestätigung mit der Seriennummer des Laufwerks" };
var dryRunOption = new Option<bool>("--dry-run") { Description = "Nur anzeigen, was betroffen wäre; nichts schreiben" };
var dismountOption = new Option<bool>("--dismount") { Description = "Volumes des Laufwerks vorher sperren und aushängen" };
var destructive = new DiskScope.Core.Destructive.DestructiveOperations();
WriteRequest Request(System.CommandLine.ParseResult r, Argument<int> drive, Option<string> range) =>
    new(new DriveId(r.GetValue(drive)), r.GetValue(range)!, r.GetValue(dismountOption), r.GetValue(dryRunOption), r.GetValue(confirmOption));

var list = new Command("list", "Alle Laufwerke auflisten") { jsonOption };
list.SetAction(result => CommandRunner.Run(result.GetValue(verboseOption), _ =>
    InfoCommands.List(drives, result.GetValue(jsonOption))));

var info = new Command("info", "Geräteinformationen, Funktionen und Volumes eines Laufwerks") { driveArgument, jsonOption };
info.SetAction(result => CommandRunner.Run(result.GetValue(verboseOption), _ =>
    InfoCommands.Info(drives, new DriveId(result.GetValue(driveArgument)), result.GetValue(jsonOption))));

var selfTestOption = new Option<string?>("--selftest") { Description = "Selbsttest: short, extended, abort oder status (ATA/SATA, Admin)" };
var smart = new Command("smart", "S.M.A.R.T.- bzw. NVMe-Gesundheitswerte eines Laufwerks") { driveArgument, selfTestOption, jsonOption };
smart.SetAction(result => CommandRunner.Run(result.GetValue(verboseOption), logging => result.GetValue(selfTestOption) is { } selfTest
    ? SmartCommands.SelfTest(
        drives,
        new SelfTestService(logging.CreateLogger<SelfTestService>()),
        new DriveId(result.GetValue(driveArgument)),
        selfTest)
    : SmartCommands.Smart(
        drives,
        new SmartService(logging.CreateLogger<SmartService>()),
        new DriveId(result.GetValue(driveArgument)),
        result.GetValue(jsonOption))));

var temp = new Command("temp", "Temperaturen aller Laufwerke") { watchOption, fahrenheitOption, jsonOption };
temp.SetAction((result, cancellationToken) => CommandRunner.RunAsync(result.GetValue(verboseOption), logging =>
    SmartCommands.TemperatureAsync(
        drives,
        new SmartService(logging.CreateLogger<SmartService>()),
        result.GetValue(watchOption),
        result.GetValue(fahrenheitOption),
        result.GetValue(jsonOption),
        cancellationToken)));

var blockOption = new Option<string>("--block") { Description = "Blockgröße (512 B bis 8 MiB)", DefaultValueFactory = _ => "64K" };
var rangeOption = new Option<string>("--range") { Description = "Bereich in % oder GB, z. B. 0-100 oder 10GB-50GB", DefaultValueFactory = _ => "0-100" };
var pointsOption = new Option<int>("--points") { Description = "Anzahl Messpunkte", DefaultValueFactory = _ => 100 };
var bench = new Command("bench", "Benchmark: Transferrate, Zugriffszeit, Burst-Rate") { driveArgument, blockOption, rangeOption, pointsOption, writeOption, confirmOption, dryRunOption, dismountOption, jsonOption };
bench.SetAction((result, cancellationToken) => CommandRunner.RunAsync(result.GetValue(verboseOption), logging => result.GetValue(writeOption) || result.GetValue(dryRunOption)
    ? DestructiveCommands.WriteBenchAsync(drives, destructive, Request(result, driveArgument, rangeOption), result.GetValue(blockOption)!, result.GetValue(pointsOption), result.GetValue(jsonOption), cancellationToken)
    : BenchCommands.BenchAsync(
        drives,
        new DiskScope.Core.Benchmarks.BenchmarkRunner(logging.CreateLogger<DiskScope.Core.Benchmarks.BenchmarkRunner>()),
        new DriveId(result.GetValue(driveArgument)),
        result.GetValue(blockOption)!,
        result.GetValue(rangeOption)!,
        result.GetValue(pointsOption),
        result.GetValue(jsonOption),
        cancellationToken)));

var sizesOption = new Option<string>("--sizes") { Description = "Transfergrößen, z. B. 512,4K,64K,1M,random", DefaultValueFactory = _ => "512,4K,64K,1M,random" };
var align4KOption = new Option<bool>("--align4k") { Description = "Zugriffe auf 4 KiB ausrichten" };
var durationOption = new Option<string>("--duration") { Description = "Messdauer je Größe, z. B. 3s", DefaultValueFactory = _ => "3s" };
var random = new Command("random", "Zufallszugriff mit mehreren Transfergrößen") { driveArgument, sizesOption, align4KOption, rangeOption, durationOption, writeOption, confirmOption, dryRunOption, dismountOption, jsonOption };
random.SetAction((result, cancellationToken) => CommandRunner.RunAsync(result.GetValue(verboseOption), logging => result.GetValue(writeOption) || result.GetValue(dryRunOption)
    ? DestructiveCommands.WriteRandomAsync(drives, destructive, Request(result, driveArgument, rangeOption), result.GetValue(sizesOption)!, result.GetValue(align4KOption), result.GetValue(durationOption)!, result.GetValue(jsonOption), cancellationToken)
    : BenchCommands.RandomAsync(
        drives,
        new DiskScope.Core.Benchmarks.RandomAccessRunner(logging.CreateLogger<DiskScope.Core.Benchmarks.RandomAccessRunner>()),
        new DriveId(result.GetValue(driveArgument)),
        result.GetValue(sizesOption)!,
        result.GetValue(align4KOption),
        result.GetValue(rangeOption)!,
        result.GetValue(durationOption)!,
        result.GetValue(jsonOption),
        cancellationToken)));

var quickOption = new Option<bool>("--quick") { Description = "Schnellscan: Stichproben statt vollständig" };
var blocksOption = new Option<int>("--blocks") { Description = "Anzahl Blöcke im Raster", DefaultValueFactory = _ => 10_000 };
var exportOption = new Option<string?>("--export") { Description = "Defekte LBAs exportieren (.csv oder .json)" };
var scan = new Command("scan", "Fehlerscan (nur lesend)") { driveArgument, quickOption, rangeOption, blocksOption, exportOption, jsonOption };
scan.SetAction((result, cancellationToken) => CommandRunner.RunAsync(result.GetValue(verboseOption), logging =>
    ScanCommands.ScanAsync(
        drives,
        new DiskScope.Core.Scanning.ErrorScanner(logging.CreateLogger<DiskScope.Core.Scanning.ErrorScanner>()),
        new DriveId(result.GetValue(driveArgument)),
        result.GetValue(quickOption),
        result.GetValue(rangeOption)!,
        result.GetValue(blocksOption),
        result.GetValue(exportOption),
        result.GetValue(jsonOption),
        cancellationToken)));

var testsOption = new Option<string?>("--tests") { Description = "Tests, z. B. Random4K,Butterfly,Burst (Standard: alle)" };
var cacheOption = new Option<bool>("--cache") { Description = "Cache-Test (Durchsatz bei wachsender Bereichsgröße)" };
var extra = new Command("extra", "Zusatztests") { driveArgument, testsOption, align4KOption, rangeOption, cacheOption, writeOption, confirmOption, dryRunOption, dismountOption, jsonOption };
extra.SetAction((result, cancellationToken) => CommandRunner.RunAsync(result.GetValue(verboseOption), logging => result.GetValue(writeOption) || result.GetValue(dryRunOption)
    ? DestructiveCommands.WriteExtraAsync(drives, destructive, Request(result, driveArgument, rangeOption), result.GetValue(testsOption), result.GetValue(align4KOption), result.GetValue(jsonOption), cancellationToken)
    : ExtraCommands.RunAsync(
        drives,
        new DiskScope.Core.Benchmarks.ExtraTestRunner(logging.CreateLogger<DiskScope.Core.Benchmarks.ExtraTestRunner>()),
        new DriveId(result.GetValue(driveArgument)),
        result.GetValue(testsOption),
        result.GetValue(align4KOption),
        result.GetValue(rangeOption)!,
        result.GetValue(cacheOption),
        result.GetValue(jsonOption),
        cancellationToken)));

var volumeArgument = new Argument<string>("volume") { Description = "Ziel-Volume oder -Ordner, z. B. D:" };
var sizeOption = new Option<string>("--size") { Description = "Größe der Testdatei", DefaultValueFactory = _ => "500M" };
var patternOption = new Option<string>("--pattern") { Description = "Datenmuster: zero oder random", DefaultValueFactory = _ => "random" };
var qdOption = new Option<int>("--qd") { Description = "Warteschlangentiefe für den Mehrfachtest", DefaultValueFactory = _ => 32 };
var blockSweepOption = new Option<bool>("--blocks") { Description = "Zusätzlich Durchsatz je Blockgröße messen" };
var filebench = new Command("filebench", "Datei-Benchmark auf einem Volume (ohne Adminrechte)") { volumeArgument, sizeOption, patternOption, qdOption, blockSweepOption, jsonOption };
filebench.SetAction((result, cancellationToken) => CommandRunner.RunAsync(result.GetValue(verboseOption), logging =>
    FileBenchCommands.RunAsync(
        new DiskScope.Core.Benchmarks.FileBenchmarkRunner(logging.CreateLogger<DiskScope.Core.Benchmarks.FileBenchmarkRunner>()),
        result.GetValue(volumeArgument)!,
        result.GetValue(sizeOption)!,
        result.GetValue(patternOption)!,
        result.GetValue(qdOption),
        result.GetValue(blockSweepOption),
        result.GetValue(jsonOption),
        cancellationToken)));

var monitorDurationOption = new Option<string>("--duration") { Description = "Aufzeichnungsdauer, z. B. 30s", DefaultValueFactory = _ => "10s" };
var monitor = new Command("monitor", "Datenträger-I/O eines Laufwerks über ETW aufzeichnen") { driveArgument, monitorDurationOption, jsonOption };
monitor.SetAction((result, cancellationToken) => CommandRunner.RunAsync(result.GetValue(verboseOption), logging =>
    MonitorCommands.RunAsync(
        drives,
        new DiskScope.Core.Monitoring.DiskMonitor(logging.CreateLogger<DiskScope.Core.Monitoring.DiskMonitor>()),
        new DriveId(result.GetValue(driveArgument)),
        result.GetValue(monitorDurationOption)!,
        result.GetValue(jsonOption),
        cancellationToken)));

var erasePatternOption = new Option<string>("--pattern") { Description = "Füllmuster: zero, random oder ein Byte wie 0xAA", DefaultValueFactory = _ => "zero" };
var passesOption = new Option<int>("--passes") { Description = "Anzahl Durchgänge", DefaultValueFactory = _ => 1 };
var verifyOption = new Option<bool>("--verify") { Description = "Prüflesen nach dem Schreiben" };
var erase = new Command("erase", "Laufwerk oder Bereich überschreiben (löscht Daten!)") { driveArgument, rangeOption, erasePatternOption, passesOption, verifyOption, confirmOption, dryRunOption, dismountOption, jsonOption };
erase.SetAction((result, cancellationToken) => CommandRunner.RunAsync(result.GetValue(verboseOption), logging =>
    DestructiveCommands.EraseAsync(
        drives,
        new DiskScope.Core.Destructive.DestructiveOperations(logging.CreateLogger<DiskScope.Core.Destructive.DestructiveOperations>()),
        Request(result, driveArgument, rangeOption),
        result.GetValue(erasePatternOption)!,
        result.GetValue(passesOption),
        result.GetValue(verifyOption),
        result.GetValue(jsonOption),
        cancellationToken)));

var setOption = new Option<string?>("--set") { Description = "Neue Stufe (Zahl oder 0x..) oder off" };
var confirmSettingOption = new Option<bool>("--confirm") { Description = "Änderung der Laufwerkseinstellung bestätigen" };
Command AcousticCommand(string name, bool aam, string description)
{
    var command = new Command(name, description) { driveArgument, setOption, confirmSettingOption };
    command.SetAction(result => CommandRunner.Run(result.GetValue(verboseOption), logging =>
        DestructiveCommands.AcousticPower(
            drives,
            new DiskScope.Core.Destructive.AcousticPowerService(logging.CreateLogger<DiskScope.Core.Destructive.AcousticPowerService>()),
            new DriveId(result.GetValue(driveArgument)),
            aam,
            result.GetValue(setOption),
            result.GetValue(confirmSettingOption))));
    return command;
}

var root = new RootCommand("DiskScope – Analyse, Bewertung und Überwachung von Laufwerken")
{
    verboseOption, list, info, smart, bench, random, extra, scan, filebench, monitor, temp, erase,
    AcousticCommand("aam", aam: true, "Automatic Acoustic Management anzeigen oder setzen (0x80 leise … 0xFE schnell)"),
    AcousticCommand("apm", aam: false, "Advanced Power Management anzeigen oder setzen (0x01 … 0xFE)"),
};
return await root.Parse(args).InvokeAsync();
