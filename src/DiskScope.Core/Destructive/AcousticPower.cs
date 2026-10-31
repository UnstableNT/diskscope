using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Destructive;

/// <summary>Current AAM/APM state of an ATA drive (IDENTIFY words 83/86, 91, 94).</summary>
public sealed record AcousticPowerState(
    bool AamSupported, bool AamEnabled, int AamLevel, int AamRecommended,
    bool ApmSupported, bool ApmEnabled, int ApmLevel)
{
    public static AcousticPowerState None { get; } = new(false, false, 0, 0, false, false, 0);
}

/// <summary>
/// Automatic Acoustic Management and Advanced Power Management (SPEC 4.14, P3) via ATA SET FEATURES.
/// Changing them alters a device setting, so it needs admin rights and an explicit confirmation.
/// AAM: 0x80 (quiet) … 0xFE (fast), off = 0xC2. APM: 0x01 … 0xFE, off = 0x85.
/// </summary>
public interface IAcousticPowerService
{
    AcousticPowerState Read(DriveInfo drive);

    AcousticPowerState SetAam(DriveInfo drive, int? level, bool confirmed);

    AcousticPowerState SetApm(DriveInfo drive, int? level, bool confirmed);
}

public sealed partial class AcousticPowerService(ILogger<AcousticPowerService>? logger = null) : IAcousticPowerService
{
    public const int AamQuiet = 0x80;
    public const int AamFast = 0xFE;
    public const int ApmMinimum = 0x01;
    public const int ApmMaximum = 0xFE;

    private readonly ILogger _logger = logger ?? NullLogger<AcousticPowerService>.Instance;

    public AcousticPowerState Read(DriveInfo drive)
    {
        if (drive.Bus is not (BusType.Sata or BusType.Raid))
        {
            return AcousticPowerState.None;
        }

        var identify = DiskProbe.Probe(drive.DevicePath, 0).AtaIdentify;
        return identify is null ? AcousticPowerState.None : FromIdentify(AtaIdentify.Parse(identify));
    }

    internal static AcousticPowerState FromIdentify(AtaIdentifyData data)
    {
        Feature Get(FeatureKind kind) => data.Features.First(f => f.Kind == kind);
        var aam = Get(FeatureKind.AutomaticAcousticManagement);
        var apm = Get(FeatureKind.AdvancedPowerManagement);
        return new AcousticPowerState(
            aam.Supported, aam.Enabled == true, data.AamLevel, data.AamRecommended,
            apm.Supported, apm.Enabled == true, data.ApmLevel);
    }

    /// <param name="level">New level, or null to switch the feature off.</param>
    /// <param name="confirmed">The user explicitly confirmed the change.</param>
    public AcousticPowerState SetAam(DriveInfo drive, int? level, bool confirmed) =>
        Apply(drive, confirmed, s => s.AamSupported, "AAM",
            level is { } l ? AtaCommands.SetFeature(AtaCommands.EnableAam, CheckLevel(l, AamQuiet, AamFast, "AAM")) : AtaCommands.SetFeature(AtaCommands.DisableAam, 0));

    public AcousticPowerState SetApm(DriveInfo drive, int? level, bool confirmed) =>
        Apply(drive, confirmed, s => s.ApmSupported, "APM",
            level is { } l ? AtaCommands.SetFeature(AtaCommands.EnableApm, CheckLevel(l, ApmMinimum, ApmMaximum, "APM")) : AtaCommands.SetFeature(AtaCommands.DisableApm, 0));

    private static byte CheckLevel(int level, int min, int max, string name) =>
        level >= min && level <= max ? (byte)level : throw new ArgumentException($"{name}-Stufe muss zwischen {min} und {max} liegen.");

    private AcousticPowerState Apply(DriveInfo drive, bool confirmed, Func<AcousticPowerState, bool> supported, string name, AtaTaskFile command)
    {
        if (!confirmed)
        {
            throw new WriteRefusedException($"Die Änderung von {name} muss ausdrücklich bestätigt werden.");
        }

        if (!Privileges.IsElevated)
        {
            throw new WriteRefusedException($"Die Änderung von {name} braucht Administratorrechte.");
        }

        if (!supported(Read(drive)))
        {
            throw new WriteRefusedException($"Das Laufwerk unterstützt {name} nicht.");
        }

        using var handle = DeviceIo.Open(drive.DevicePath, DeviceAccess.Write);
        var accepted = AtaPassThrough.TryApplySetting(handle, command);
        LogSetting(name, drive.Id.Number, command.Features, command.SectorCount, accepted);
        return accepted ? Read(drive) : throw new WriteRefusedException($"Das Laufwerk hat die {name}-Einstellung abgelehnt.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name} on drive {Drive}: feature 0x{Feature:X2} level 0x{Level:X2}, accepted {Accepted}")]
    private partial void LogSetting(string name, int drive, byte feature, byte level, bool accepted);
}
