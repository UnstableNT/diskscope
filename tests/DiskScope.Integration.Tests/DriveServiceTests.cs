using DiskScope.Core.Devices;

namespace DiskScope.Integration.Tests;

/// <summary>Read-only queries against the drives of the test machine; no admin rights needed.</summary>
[Trait("Category", "Hardware")]
public class DriveServiceTests
{
    private readonly DriveService _service = new();

    [Fact]
    public void Lists_drives_consistently()
    {
        var ids = _service.ListDrives();
        var drives = _service.GetDrives();

        ids.ShouldNotBeEmpty();
        drives.Select(d => d.Id).ShouldBe(ids);
    }

    [Fact]
    public void System_disk_holds_the_windows_volume()
    {
        var windowsRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');

        var system = _service.GetDrives().Where(d => d.IsSystemDisk).ToArray();

        system.ShouldNotBeEmpty();
        system.SelectMany(d => d.Volumes).ShouldContain(v => v.MountPoint.Equals(windowsRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Drive_details_are_plausible()
    {
        foreach (var drive in _service.GetDrives())
        {
            drive.Model.ShouldNotBeNullOrWhiteSpace();
            drive.DevicePath.ShouldBe(drive.Id.DevicePath);
            drive.LogicalSectorSize.ShouldBeOneOf(512u, 4096u);
            drive.PhysicalSectorSize.ShouldBeGreaterThanOrEqualTo(drive.LogicalSectorSize);
            drive.SizeBytes.ShouldBeGreaterThan(0UL);
            drive.Volumes.ShouldAllBe(v => v.Free <= v.Capacity);
        }
    }

    [Fact]
    public void Unknown_drive_number_throws() =>
        Should.Throw<DriveNotFoundException>(() => _service.GetDrive(new DriveId(999)));
}
