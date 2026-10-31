using System.Globalization;
using DiskScope.Core.Native;
using Microsoft.Win32;

namespace DiskScope.Core.Devices;

public interface IDriveService
{
    /// <summary>Numbers of all present physical drives, ascending.</summary>
    IReadOnlyList<DriveId> ListDrives();

    IReadOnlyList<DriveInfo> GetDrives();

    /// <exception cref="DriveNotFoundException">No drive with this number exists.</exception>
    DriveInfo GetDrive(DriveId id);
}

public sealed class DriveNotFoundException(DriveId id)
    : Exception($"Laufwerk {id.Number} ({id.DevicePath}) wurde nicht gefunden.")
{
    public DriveId Id { get; } = id;
}

/// <summary>Read-only drive enumeration and information (SPEC 4.2).</summary>
public sealed class DriveService : IDriveService
{
    public IReadOnlyList<DriveId> ListDrives() =>
        DeviceTree.EnumerateDisks().Select(d => new DriveId(d.Number)).ToArray();

    public IReadOnlyList<DriveInfo> GetDrives()
    {
        var disks = DeviceTree.EnumerateDisks();
        var volumes = Volumes.Enumerate();
        var systemDisks = FindSystemDisks(volumes);
        return disks.Select(d => Build(d, volumes, systemDisks)).ToArray();
    }

    public DriveInfo GetDrive(DriveId id)
    {
        var disk = DeviceTree.EnumerateDisks().FirstOrDefault(d => d.Number == id.Number);
        if (disk.InterfacePath is null)
        {
            throw new DriveNotFoundException(id);
        }

        var volumes = Volumes.Enumerate();
        return Build(disk, volumes, FindSystemDisks(volumes));
    }

    private static DriveInfo Build(DiskInterface disk, IReadOnlyList<RawVolume> volumes, HashSet<int> systemDisks)
    {
        var id = new DriveId(disk.Number);
        var raw = DiskProbe.Probe(id.DevicePath, disk.DevInst);
        return DriveInfoBuilder.Build(id, raw, VolumesOnDisk(disk.Number, volumes), systemDisks.Contains(disk.Number));
    }

    internal static IReadOnlyList<VolumeInfo> VolumesOnDisk(int diskNumber, IReadOnlyList<RawVolume> volumes)
    {
        var result = new List<VolumeInfo>();
        foreach (var volume in volumes)
        {
            foreach (var extent in volume.Extents.Where(e => e.DiskNumber == diskNumber))
            {
                result.Add(new VolumeInfo(
                    Label: volume.Label,
                    MountPoint: volume.MountPoints.Count > 0 ? volume.MountPoints[0].TrimEnd('\\') : string.Empty,
                    Capacity: volume.TotalBytes ?? extent.Length,
                    Free: volume.FreeBytes ?? 0,
                    FileSystem: volume.FileSystem,
                    Serial: volume.SerialNumber is { } serial ? FormatSerial(serial) : string.Empty,
                    Alignment: extent.StartingOffset));
            }
        }

        result.Sort((a, b) => a.Alignment.CompareTo(b.Alignment));
        return result;
    }

    internal static string FormatSerial(uint serial) =>
        string.Create(CultureInfo.InvariantCulture, $"{serial >> 16:X4}-{serial & 0xFFFF:X4}");

    /// <summary>
    /// Disks holding the Windows volume or the system (boot) partition. These are never written to (SPEC 9.2).
    /// </summary>
    private static HashSet<int> FindSystemDisks(IReadOnlyList<RawVolume> volumes)
    {
        var windowsRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var systemPartition = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\Setup", "SystemPartition", null) as string;

        var disks = new HashSet<int>();
        foreach (var volume in volumes)
        {
            var isWindows = volume.MountPoints.Any(m => m.Equals(windowsRoot, StringComparison.OrdinalIgnoreCase));
            var isBoot = systemPartition is not null
                && string.Equals(volume.DeviceName, systemPartition, StringComparison.OrdinalIgnoreCase);
            if (isWindows || isBoot)
            {
                disks.UnionWith(volume.Extents.Select(e => e.DiskNumber));
            }
        }

        return disks;
    }
}
