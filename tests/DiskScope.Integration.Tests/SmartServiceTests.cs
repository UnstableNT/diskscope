using DiskScope.Core;
using DiskScope.Core.Devices;
using DiskScope.Core.Smart;

namespace DiskScope.Integration.Tests;

/// <summary>Read-only SMART queries against the drives of the test machine.</summary>
[Trait("Category", "Hardware")]
public class SmartServiceTests
{
    private readonly DriveService _drives = new();
    private readonly SmartService _smart = new();

    [Fact]
    public void Every_drive_returns_a_result_without_throwing()
    {
        foreach (var drive in _drives.GetDrives())
        {
            var result = _smart.Read(drive);

            result.Drive.ShouldBe(drive.Id);
            if (result.IsSupported && result.Source != SmartSource.PredictFailure)
            {
                result.Attributes.ShouldNotBeEmpty();
            }
        }
    }

    [Fact]
    public void Nvme_drives_deliver_the_health_log()
    {
        foreach (var drive in _drives.GetDrives().Where(d => d.Bus == BusType.Nvme))
        {
            var result = _smart.Read(drive);

            result.Source.ShouldBe(SmartSource.NvmeLog);
            result.TemperatureCelsius.ShouldNotBeNull();
            result.TemperatureCelsius.Value.ShouldBeInRange(1, 100);
        }
    }

    [Fact]
    public void Ata_drives_deliver_full_smart_data_when_elevated()
    {
        Assert.SkipUnless(Privileges.IsElevated, "Benötigt Administratorrechte.");

        foreach (var drive in _drives.GetDrives().Where(d => d.Bus == BusType.Sata))
        {
            var result = _smart.Read(drive);

            result.Source.ShouldBeOneOf(SmartSource.AtaPassThrough, SmartSource.LegacyIoctl);
            result.Attributes.ShouldContain(a => a.Threshold != null);
        }
    }
}
