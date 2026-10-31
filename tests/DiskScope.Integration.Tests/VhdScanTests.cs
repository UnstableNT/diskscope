using DiskScope.Core;
using DiskScope.Core.Devices;
using DiskScope.Core.Scanning;

namespace DiskScope.Integration.Tests;

/// <summary>Error scan against a test VHD (milestone M4). Run elevated: -trait "Category=Admin".</summary>
[Trait("Category", "Admin")]
public sealed class VhdScanTests : IDisposable
{
    private const ulong VhdBytes = 256UL * 1024 * 1024;

    private readonly TestVhd? _vhd;
    private readonly DriveService _drives = new();

    public VhdScanTests()
    {
        if (Privileges.IsElevated)
        {
            _vhd = TestVhd.Create(256);
        }
    }

    public void Dispose() => _vhd?.Dispose();

    private DriveInfo Drive()
    {
        Assert.SkipUnless(Privileges.IsElevated, "Benötigt Administratorrechte.");
        return _drives.GetDrive(_vhd!.Drive);
    }

    [Fact]
    public async Task Full_scan_covers_the_whole_vhd()
    {
        var drive = Drive();
        var reports = 0;
        ulong lastPosition = 0;
        var progress = new Progress<ScanProgress>(p =>
        {
            Interlocked.Increment(ref reports);
            lastPosition = Math.Max(lastPosition, p.Position);
        });

        var result = await new ErrorScanner().ScanAsync(
            drive, new ScanOptions(BlockCount: 1000, SpeedMap: true), progress, TestContext.Current.CancellationToken);

        result.Range.ShouldBe(new Core.Benchmarks.DriveRange(0, VhdBytes));
        result.Blocks.Count.ShouldBe(1000);
        result.Blocks.ShouldAllBe(b => b == BlockState.Good);
        result.DamagedBlocks.ShouldBe(0);
        result.BadLbas.ShouldBeEmpty();
        result.BlockStart(result.Blocks.Count).ShouldBe(VhdBytes);
        reports.ShouldBeGreaterThan(0);
        result.BlockSpeeds!.ShouldAllBe(s => s > 0);
        result.AverageMBps.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Quick_scan_finishes_faster_than_full_scan()
    {
        var drive = Drive();
        var scanner = new ErrorScanner();

        var quick = await scanner.ScanAsync(drive, new ScanOptions(BlockCount: 500, Quick: true, QuickSampleBytes: 64 * 1024), cancellationToken: TestContext.Current.CancellationToken);

        quick.Quick.ShouldBeTrue();
        quick.Blocks.ShouldAllBe(b => b == BlockState.Good);
    }

    [Fact]
    public async Task Scan_can_be_cancelled()
    {
        var drive = Drive();
        using var cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Fraction >= 0.1)
            {
                cts.Cancel();
            }
        });

        await Should.ThrowAsync<OperationCanceledException>(
            () => new ErrorScanner().ScanAsync(drive, new ScanOptions(BlockCount: 4096), progress, cts.Token));
    }

    [Fact]
    public async Task Exports_empty_bad_lba_list()
    {
        var drive = Drive();
        var result = await new ErrorScanner().ScanAsync(
            drive, new ScanOptions(BlockCount: 64, Quick: true), cancellationToken: TestContext.Current.CancellationToken);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskScopeTests", $"scan-{Guid.NewGuid():N}.csv");

        try
        {
            ScanExport.Write(result, path);
            System.IO.File.ReadAllText(path).ShouldBe("lba,byte_offset\n");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
