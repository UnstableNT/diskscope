using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;

namespace DiskScope.Core.Tests;

public class BenchmarkTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private static BenchmarkResult Run(FakeBlockDevice device, BenchmarkOptions options, IProgress<BenchmarkProgress>? progress = null) =>
        BenchmarkRunner.Run(device, new DriveId(7), options, progress, CancellationToken.None);

    [Fact]
    public void Spreads_measuring_points_evenly_over_the_range()
    {
        var device = new FakeBlockDevice(10 * GiB);

        var result = Run(device, new BenchmarkOptions(Points: 11, BlocksPerPoint: 4, MeasureAccessTime: false, MeasureBurst: false));

        result.Transfer.Count.ShouldBe(11);
        result.Transfer[0].Position.ShouldBe(0UL);
        result.Transfer[^1].Position.ShouldBe((10 * GiB) - (4 * 65536));
        result.Transfer.Select(t => t.Position).ShouldBeInOrder();
        device.Reads.Count.ShouldBe(44);
        device.Reads.ShouldAllBe(r => r.Length == 65536);
        result.AverageMBps.ShouldNotBeNull();
        result.MinimumMBps!.Value.ShouldBeLessThanOrEqualTo(result.MaximumMBps!.Value);
    }

    [Fact]
    public void Respects_the_range()
    {
        var device = new FakeBlockDevice(10 * GiB);
        var range = DriveRange.FromPercent(10 * GiB, 50, 60);

        var result = Run(device, new BenchmarkOptions(Range: range, Points: 20, AccessSamples: 300));

        result.Range.ShouldBe(range);
        device.Reads.ShouldAllBe(r => r.Offset >= range.Start && r.Offset + (ulong)r.Length <= range.End);
        result.Access.Count.ShouldBe(300);
        result.AccessTimeMs.ShouldNotBeNull();
        result.BurstMBps.ShouldNotBeNull();
    }

    [Fact]
    public void Access_time_reads_single_sectors_at_repeatable_positions()
    {
        var first = new FakeBlockDevice(GiB, 4096);
        var second = new FakeBlockDevice(GiB, 4096);
        var options = new BenchmarkOptions(MeasureTransfer: false, MeasureBurst: false, AccessSamples: 50, Seed: 42);

        Run(first, options);
        Run(second, options);

        first.Reads.ShouldAllBe(r => r.Length == 4096);
        first.Reads.ShouldBe(second.Reads);
    }

    [Fact]
    public void Burst_reads_the_same_block_repeatedly()
    {
        var device = new FakeBlockDevice(GiB);

        Run(device, new BenchmarkOptions(MeasureTransfer: false, MeasureAccessTime: false, BurstRepeats: 8));

        device.Reads.Count.ShouldBe(9);
        device.Reads.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void Counts_read_errors_and_skips_the_point()
    {
        var device = new FakeBlockDevice(GiB);
        device.BadOffsets.Add(0);

        var result = Run(device, new BenchmarkOptions(Points: 5, MeasureAccessTime: false, MeasureBurst: false));

        result.ReadErrors.ShouldBe(1);
        result.Transfer.Count.ShouldBe(4);
    }

    [Fact]
    public void Reports_monotonic_progress_up_to_one()
    {
        var reports = new List<BenchmarkProgress>();
        var progress = new SyncProgress<BenchmarkProgress>(reports.Add);

        Run(new FakeBlockDevice(GiB), new BenchmarkOptions(Points: 10, AccessSamples: 10), progress);

        reports.Select(r => r.Fraction).ShouldBeInOrder();
        reports[^1].Fraction.ShouldBe(1.0, 1e-9);
        reports.Count(r => r.Transfer is not null).ShouldBe(10);
        reports.Count(r => r.Access is not null).ShouldBe(10);
    }

    [Fact]
    public void Cancellation_stops_the_run()
    {
        using var cts = new CancellationTokenSource();
        var device = new FakeBlockDevice(GiB);
        device.OnRead = () =>
        {
            if (device.Reads.Count == 10)
            {
                cts.Cancel();
            }
        };

        Should.Throw<OperationCanceledException>(
            () => BenchmarkRunner.Run(device, new DriveId(0), new BenchmarkOptions(Points: 100, BlocksPerPoint: 1), null, cts.Token));
        device.Reads.Count.ShouldBeLessThanOrEqualTo(11);
    }

    [Fact]
    public void Block_size_must_fit_the_sector_size() =>
        Should.Throw<ArgumentException>(() => Run(new FakeBlockDevice(GiB, 4096), new BenchmarkOptions(BlockSize: 512)));

    /// <summary>Progress&lt;T&gt; posts asynchronously; tests need the reports in order.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
