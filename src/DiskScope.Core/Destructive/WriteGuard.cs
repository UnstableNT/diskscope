using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;

namespace DiskScope.Core.Destructive;

/// <summary>How the user confirmed a destructive operation (SPEC 9.4).</summary>
public enum ConfirmationKind
{
    /// <summary>GUI: the model name typed by the user.</summary>
    Model,

    /// <summary>CLI: <c>--i-understand-data-loss &lt;seriennummer&gt;</c>; the model if the drive reports no serial number.</summary>
    Serial,
}

/// <summary>What a destructive operation would affect, and why it is refused if so (dry run, SPEC 9.5).</summary>
public sealed record DestructivePlan(
    DriveId Drive,
    string Model,
    string? Serial,
    DriveRange Range,
    IReadOnlyList<VolumeInfo> Volumes,
    bool DismountVolumes,
    IReadOnlyList<string> Refusals)
{
    public bool Allowed => Refusals.Count == 0;

    public ulong Bytes => Range.Length;
}

public sealed class WriteRefusedException : Exception
{
    public WriteRefusedException()
        : this([])
    {
    }

    public WriteRefusedException(string message)
        : this([message])
    {
    }

    public WriteRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reasons = [message];
    }

    public WriteRefusedException(IReadOnlyList<string> reasons)
        : base("Schreibzugriff verweigert: " + string.Join(" ", reasons))
    {
        Reasons = reasons;
    }

    public IReadOnlyList<string> Reasons { get; }
}

/// <summary>
/// Proof that <see cref="WriteGuard"/> checked all rules for one drive and range. Only the guard can create it,
/// and every raw write in <see cref="DestructiveSession"/> requires it.
/// </summary>
public sealed class WriteAuthorization
{
    internal WriteAuthorization(DriveInfo drive, DriveRange range, bool dismountVolumes)
    {
        Drive = drive;
        Range = range;
        DismountVolumes = dismountVolumes;
    }

    public DriveInfo Drive { get; }

    public DriveRange Range { get; }

    public bool DismountVolumes { get; }
}

/// <summary>
/// Enforces the protection rules of SPEC 9 before any raw write:
/// never the system/boot disk, no mounted volumes unless explicitly dismounted, typed confirmation, dry run.
/// The drive is re-read for every decision so that stale UI data cannot bypass a rule.
/// </summary>
public sealed class WriteGuard(IDriveService drives)
{
    public static string ExpectedConfirmation(DriveInfo drive, ConfirmationKind kind) =>
        kind == ConfirmationKind.Serial && !string.IsNullOrWhiteSpace(drive.Serial) ? drive.Serial.Trim() : drive.Model.Trim();

    /// <summary>Dry run: what would be written, and the reasons it would be refused.</summary>
    public DestructivePlan Plan(DriveId id, DriveRange? range, bool dismountVolumes) =>
        Evaluate(drives.GetDrive(id), range, dismountVolumes);

    public WriteAuthorization Authorize(DriveId id, DriveRange? range, bool dismountVolumes, string confirmation, ConfirmationKind kind)
    {
        var drive = drives.GetDrive(id);
        var plan = Evaluate(drive, range, dismountVolumes);
        var refusals = plan.Refusals.ToList();

        var expected = ExpectedConfirmation(drive, kind);
        if (!string.Equals(confirmation?.Trim(), expected, StringComparison.Ordinal))
        {
            refusals.Add(kind == ConfirmationKind.Serial
                ? "Die Bestätigung stimmt nicht mit der Seriennummer des Laufwerks überein."
                : "Die eingegebene Modellbezeichnung stimmt nicht überein.");
        }

        if (!Privileges.IsElevated)
        {
            refusals.Add("Schreibzugriff auf Laufwerke braucht Administratorrechte.");
        }

        return refusals.Count > 0
            ? throw new WriteRefusedException(refusals)
            : new WriteAuthorization(drive, plan.Range, dismountVolumes);
    }

    /// <summary>The rules themselves, free of I/O so they can be unit-tested.</summary>
    internal static DestructivePlan Evaluate(DriveInfo drive, DriveRange? range, bool dismountVolumes)
    {
        var refusals = new List<string>();
        if (drive.IsSystemDisk)
        {
            refusals.Add("Das System-/Boot-Laufwerk wird nie beschrieben.");
        }

        if (drive.Volumes.Count > 0 && !dismountVolumes)
        {
            var names = string.Join(", ", drive.Volumes.Select(v => v.MountPoint.Length > 0 ? v.MountPoint : v.Label.Length > 0 ? v.Label : "ohne Bezeichnung"));
            refusals.Add($"Das Laufwerk hat eingehängte Volumes ({names}); sie müssen ausdrücklich ausgehängt werden.");
        }

        if (drive.SizeBytes == 0)
        {
            refusals.Add("Die Laufwerksgröße ist unbekannt.");
        }

        var effective = DriveRange.Full(drive.SizeBytes);
        if (drive.SizeBytes > 0)
        {
            try
            {
                effective = (range ?? DriveRange.Full(drive.SizeBytes)).AlignTo(drive.LogicalSectorSize, drive.SizeBytes);
            }
            catch (ArgumentException ex)
            {
                refusals.Add(ex.Message);
            }
        }

        return new DestructivePlan(drive.Id, drive.Model, drive.Serial, effective, drive.Volumes, dismountVolumes, refusals);
    }
}
