using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Destructive;
using DiskScope.Core.Devices;

namespace DiskScope.Integration.Tests;

/// <summary>
/// Writing operations against test VHDs only (SPEC 9, 11, milestone M7). The system disk test never reaches a write:
/// the guard refuses before any handle with write access is opened.
/// </summary>
[Trait("Category", "Admin")]
public sealed class VhdDestructiveTests : IDisposable
{
    private const ulong VhdBytes = 256UL * 1024 * 1024;

    private readonly TestVhd? _vhd;
    private readonly DriveService _drives = new();
    private readonly DestructiveOperations _operations = new();

    public VhdDestructiveTests()
    {
        if (Privileges.IsElevated)
        {
            _vhd = TestVhd.Create(256);
        }
    }

    public void Dispose() => _vhd?.Dispose();

    private WriteGuard Guard => new(_drives);

    private DriveInfo Vhd()
    {
        Assert.SkipUnless(Privileges.IsElevated, "Benötigt Administratorrechte.");
        return _drives.GetDrive(_vhd!.Drive);
    }

    private WriteAuthorization Authorize(DriveInfo drive, DriveRange? range = null, bool dismount = false) =>
        Guard.Authorize(drive.Id, range, dismount, WriteGuard.ExpectedConfirmation(drive, ConfirmationKind.Serial), ConfirmationKind.Serial);

    [Fact]
    public void System_disk_is_refused_before_anything_is_opened()
    {
        Vhd();
        var system = _drives.GetDrives().First(d => d.IsSystemDisk);

        var ex = Should.Throw<WriteRefusedException>(() => Authorize(system, dismount: true));

        ex.Reasons.ShouldContain(r => r.Contains("System", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Erase_overwrites_the_whole_vhd_with_verification()
    {
        var drive = Vhd();

        var result = await _operations.EraseAsync(
            Authorize(drive), new EraseOptions(ErasePattern.Fixed, 0x5A, Passes: 2, Verify: true, BlockCount: 256), cancellationToken: TestContext.Current.CancellationToken);

        result.Errors.ShouldBe(0);
        result.BytesWritten.ShouldBe(2 * VhdBytes);
        result.Blocks.ShouldAllBe(b => b == EraseBlockState.Written);
        ReadSector(drive, 0).ShouldAllBe(b => b == 0x5A);
        ReadSector(drive, VhdBytes - 512).ShouldAllBe(b => b == 0x5A);
    }

    [Fact]
    public async Task Erase_stays_inside_the_range()
    {
        var drive = Vhd();
        await _operations.EraseAsync(Authorize(drive), new EraseOptions(ErasePattern.Fixed, 0x11, BlockCount: 16), cancellationToken: TestContext.Current.CancellationToken);
        var range = new DriveRange(64UL * 1024 * 1024, 128UL * 1024 * 1024);

        await _operations.EraseAsync(Authorize(drive, range), new EraseOptions(ErasePattern.Zero, BlockCount: 16), cancellationToken: TestContext.Current.CancellationToken);

        ReadSector(drive, range.Start - 512).ShouldAllBe(b => b == 0x11);
        ReadSector(drive, range.Start).ShouldAllBe(b => b == 0);
        ReadSector(drive, range.End - 512).ShouldAllBe(b => b == 0);
        ReadSector(drive, range.End).ShouldAllBe(b => b == 0x11);
    }

    [Fact]
    public async Task Write_benchmark_and_random_write_run_on_the_vhd()
    {
        var drive = Vhd();
        var authorization = Authorize(drive);

        var bench = await _operations.WriteBenchmarkAsync(authorization, new BenchmarkOptions(Points: 20), cancellationToken: TestContext.Current.CancellationToken);
        var random = await _operations.WriteRandomAccessAsync(authorization, new RandomAccessOptions([4096], DurationPerSize: TimeSpan.FromMilliseconds(500)), cancellationToken: TestContext.Current.CancellationToken);

        bench.ReadErrors.ShouldBe(0);
        bench.AverageMBps!.Value.ShouldBeGreaterThan(0);
        random.Sizes.Single().Operations.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Mounted_volume_requires_explicit_dismount()
    {
        Vhd();
        _vhd!.CreateMountedVolume();
        var drive = _drives.GetDrive(_vhd.Drive);
        drive.Volumes.ShouldNotBeEmpty();

        Should.Throw<WriteRefusedException>(() => Authorize(drive)).Reasons.ShouldContain(r => r.Contains("Volumes", StringComparison.Ordinal));

        var result = await _operations.EraseAsync(
            Authorize(drive, new DriveRange(0, 16UL * 1024 * 1024), dismount: true),
            new EraseOptions(BlockCount: 4),
            cancellationToken: TestContext.Current.CancellationToken);
        result.Errors.ShouldBe(0);
    }

    [Fact]
    public async Task Erase_can_be_cancelled()
    {
        var drive = Vhd();
        using var cts = new CancellationTokenSource();
        var progress = new Progress<EraseProgress>(p =>
        {
            if (p.Fraction > 0.1)
            {
                cts.Cancel();
            }
        });

        await Should.ThrowAsync<OperationCanceledException>(
            () => _operations.EraseAsync(Authorize(drive), new EraseOptions(Passes: 3, BlockCount: 512), progress, cts.Token));
    }

    /// <summary>Reads one sector with an independent, unbuffered read-only handle (sector-aligned buffer).</summary>
    private static byte[] ReadSector(DriveInfo drive, ulong offset)
    {
        const int NoBuffering = 0x20000000;
        using var handle = System.IO.File.OpenHandle(
            drive.DevicePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite, (FileOptions)NoBuffering);
        var pinned = GC.AllocateArray<byte>(8192, pinned: true);
        var address = (long)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0);
        var shift = (int)((4096 - (address % 4096)) % 4096);
        System.IO.RandomAccess.Read(handle, pinned.AsSpan(shift, 512), (long)offset);
        return pinned.AsSpan(shift, 512).ToArray();
    }
}
