using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Smart;

public enum SelfTestKind { Quick, Extended }

public enum SelfTestState { NotSupported, Idle, Running, CompletedOk, Aborted, Interrupted, Failed }

/// <param name="RemainingPercent">Remaining work while running (10 % steps).</param>
/// <param name="ShortMinutes">Recommended polling time of the short test, from SMART data byte 372.</param>
/// <param name="ExtendedMinutes">Recommended polling time of the extended test, from byte 373 or words 375–376.</param>
public sealed record SelfTestStatus(SelfTestState State, int? RemainingPercent, string Text, int? ShortMinutes = null, int? ExtendedMinutes = null);

/// <summary>
/// ATA SMART self-tests (SPEC 4.3, P2): start short or extended test, abort, read progress from SMART data byte 363.
/// NVMe self-tests are reported as not supported because Windows offers no documented path for them here.
/// </summary>
public interface ISelfTestService
{
    SelfTestStatus Query(DriveInfo drive);

    SelfTestStatus Start(DriveInfo drive, SelfTestKind kind);

    SelfTestStatus Abort(DriveInfo drive);
}

public sealed partial class SelfTestService(ILogger<SelfTestService>? logger = null) : ISelfTestService
{
    private readonly ILogger _logger = logger ?? NullLogger<SelfTestService>.Instance;

    internal const int ExecutionStatusOffset = 363;
    private const int CapabilityOffset = 367;
    private const int ShortPollingOffset = 372;
    private const int ExtendedPollingOffset = 373;
    private const int ExtendedPollingWordOffset = 375;

    public SelfTestStatus Query(DriveInfo drive)
    {
        if (Unsupported(drive) is { } unsupported)
        {
            return unsupported;
        }

        using var handle = OpenOrNull(drive);
        if (handle is null)
        {
            return new SelfTestStatus(SelfTestState.NotSupported, null, "Selbsttests brauchen Administratorrechte.");
        }

        var data = AtaPassThrough.TryReadData(handle, AtaCommands.SmartCommand(AtaCommands.SmartReadData), SmartProbe.SectorLength);
        return data is null
            ? new SelfTestStatus(SelfTestState.NotSupported, null, "S.M.A.R.T.-Daten nicht lesbar.")
            : Parse(data);
    }

    public SelfTestStatus Start(DriveInfo drive, SelfTestKind kind) =>
        Execute(drive, kind == SelfTestKind.Quick ? AtaCommands.SelfTestShort : AtaCommands.SelfTestExtended);

    public SelfTestStatus Abort(DriveInfo drive) => Execute(drive, AtaCommands.SelfTestAbort);

    /// <summary>Interprets the self-test fields of SMART READ DATA (ATA/ACS).</summary>
    public static SelfTestStatus Parse(ReadOnlySpan<byte> smartData)
    {
        if ((smartData[CapabilityOffset] & 0x01) == 0)
        {
            return new SelfTestStatus(SelfTestState.NotSupported, null, "Das Laufwerk unterstützt keine Selbsttests.");
        }

        var shortMinutes = smartData[ShortPollingOffset];
        int extendedMinutes = smartData[ExtendedPollingOffset];
        if (extendedMinutes == 0xFF)
        {
            extendedMinutes = smartData[ExtendedPollingWordOffset] | (smartData[ExtendedPollingWordOffset + 1] << 8);
        }

        var status = smartData[ExecutionStatusOffset];
        var code = status >> 4;
        var remaining = (status & 0x0F) * 10;
        var (state, text) = code switch
        {
            0x0 => (SelfTestState.CompletedOk, "Letzter Selbsttest ohne Fehler abgeschlossen"),
            0x1 => (SelfTestState.Aborted, "Letzter Selbsttest wurde abgebrochen"),
            0x2 => (SelfTestState.Interrupted, "Letzter Selbsttest wurde durch einen Reset unterbrochen"),
            0xF => (SelfTestState.Running, $"Selbsttest läuft, noch etwa {remaining} %"),
            0x3 => (SelfTestState.Failed, "Selbsttest: schwerer Fehler, Test konnte nicht abgeschlossen werden"),
            0x4 => (SelfTestState.Failed, "Selbsttest fehlgeschlagen (unbekanntes Testelement)"),
            0x5 => (SelfTestState.Failed, "Selbsttest fehlgeschlagen (elektrisches Element)"),
            0x6 => (SelfTestState.Failed, "Selbsttest fehlgeschlagen (Servo-/Suchelement)"),
            0x7 => (SelfTestState.Failed, "Selbsttest fehlgeschlagen (Lesetest)"),
            0x8 => (SelfTestState.Failed, "Selbsttest fehlgeschlagen (vermutlich Transportschaden)"),
            _ => (SelfTestState.Idle, "Kein Selbsttest-Ergebnis"),
        };

        return new SelfTestStatus(state, state == SelfTestState.Running ? remaining : null, text, shortMinutes, extendedMinutes);
    }

    private static SelfTestStatus? Unsupported(DriveInfo drive) => drive.Bus switch
    {
        BusType.Nvme => new SelfTestStatus(SelfTestState.NotSupported, null, "NVMe-Selbsttests werden von DiskScope unter Windows nicht unterstützt."),
        BusType.Sata or BusType.Raid => null,
        _ => new SelfTestStatus(SelfTestState.NotSupported, null, "Selbsttests sind nur für ATA-/SATA-Laufwerke verfügbar."),
    };

    private static Microsoft.Win32.SafeHandles.SafeFileHandle? OpenOrNull(DriveInfo drive)
    {
        try
        {
            return DeviceIo.Open(drive.DevicePath, DeviceAccess.Command);
        }
        catch (DiskScopeException ex) when (ex.IsAccessDenied)
        {
            return null;
        }
    }

    private SelfTestStatus Execute(DriveInfo drive, byte subcommand)
    {
        if (Unsupported(drive) is { } unsupported)
        {
            return unsupported;
        }

        using var handle = OpenOrNull(drive);
        if (handle is null)
        {
            return new SelfTestStatus(SelfTestState.NotSupported, null, "Selbsttests brauchen Administratorrechte.");
        }

        var result = AtaPassThrough.TryExecute(handle, AtaCommands.SmartSelfTest(subcommand), 0);
        LogCommand(drive.Id.Number, subcommand, result is not null);
        return result is null
            ? new SelfTestStatus(SelfTestState.NotSupported, null, "Das Laufwerk hat den Befehl abgelehnt.")
            : Query(drive);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Self-test command {Subcommand} on drive {Drive}: accepted {Accepted}")]
    private partial void LogCommand(int drive, byte subcommand, bool accepted);
}
