using DiskScope.Core;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;

namespace DiskScope.Integration.Tests;

/// <summary>Read benchmarks against a test VHD (SPEC 11). Run elevated: --filter-trait "Category=Admin".</summary>
[Trait("Category", "Admin")]
public sealed class VhdBenchmarkTests : IDisposable
{
    private readonly TestVhd? _vhd;
    private readonly DriveService _drives = new();

    public VhdBenchmarkTests()
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
    public void Vhd_is_a_virtual_empty_non_system_disk()
    {
        var drive = Drive();

        drive.Bus.ShouldBe(BusType.Virtual);
        drive.IsSystemDisk.ShouldBeFalse();
        drive.Volumes.ShouldBeEmpty();
        drive.SizeBytes.ShouldBe(256UL * 1024 * 1024);
    }

    [Fact]
    public async Task Benchmark_is_reproducible()
    {
        var drive = Drive();
        var runner = new BenchmarkRunner();
        var options = new BenchmarkOptions(Points: 50, BlocksPerPoint: 16);

        var first = await runner.RunAsync(drive, options, cancellationToken: TestContext.Current.CancellationToken);
        var second = await runner.RunAsync(drive, options, cancellationToken: TestContext.Current.CancellationToken);

        foreach (var result in new[] { first, second })
        {
            result.ReadErrors.ShouldBe(0);
            result.Transfer.Count.ShouldBe(50);
            result.AverageMBps!.Value.ShouldBeGreaterThan(0);
            result.AccessTimeMs!.Value.ShouldBeGreaterThan(0);
            result.BurstMBps!.Value.ShouldBeGreaterThan(0);
        }

        Deviation(first.AverageMBps!.Value, second.AverageMBps!.Value).ShouldBeLessThan(0.5);
    }

    [Fact]
    public async Task Random_access_is_reproducible()
    {
        var drive = Drive();
        var runner = new RandomAccessRunner();
        var options = new RandomAccessOptions([4096, 65536], DurationPerSize: TimeSpan.FromSeconds(1));

        var first = await runner.RunAsync(drive, options, cancellationToken: TestContext.Current.CancellationToken);
        var second = await runner.RunAsync(drive, options, cancellationToken: TestContext.Current.CancellationToken);

        first.ReadErrors.ShouldBe(0);
        for (var i = 0; i < first.Sizes.Count; i++)
        {
            first.Sizes[i].Operations.ShouldBeGreaterThan(0);
            Deviation(first.Sizes[i].OperationsPerSecond, second.Sizes[i].OperationsPerSecond).ShouldBeLessThan(0.5);
        }
    }

    [Fact]
    public async Task Benchmark_can_be_cancelled()
    {
        var drive = Drive();
        using var cts = new CancellationTokenSource();
        var progress = new Progress<BenchmarkProgress>(p =>
        {
            if (p.Fraction > 0.1)
            {
                cts.Cancel();
            }
        });

        await Should.ThrowAsync<OperationCanceledException>(
            () => new BenchmarkRunner().RunAsync(drive, new BenchmarkOptions(Points: 2000, BlocksPerPoint: 64), progress, cts.Token));
    }

    private static double Deviation(double a, double b) => Math.Abs(a - b) / Math.Max(a, b);
}
