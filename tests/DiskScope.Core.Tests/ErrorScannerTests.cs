using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;
using DiskScope.Core.Scanning;

namespace DiskScope.Core.Tests;

public class ErrorScannerTests
{
    private const ulong MiB = 1024 * 1024;

    private static ScanResult Scan(FakeBlockDevice device, ScanOptions options, IProgress<ScanProgress>? progress = null) =>
        ErrorScanner.Scan(device, new DriveId(5), options, progress, CancellationToken.None);

    [Fact]
    public void Full_scan_reads_every_byte_exactly_once()
    {
        var device = new FakeBlockDevice(100 * MiB);

        var result = Scan(device, new ScanOptions(BlockCount: 50));

        result.Blocks.Count.ShouldBe(50);
        result.Blocks.ShouldAllBe(b => b == BlockState.Good);
        result.DamagedBlocks.ShouldBe(0);
        result.BadLbas.ShouldBeEmpty();
        device.Reads.Sum(r => (long)r.Length).ShouldBe((long)(100 * MiB));
        device.Reads.Select(r => r.Offset).ShouldBeUnique();
        device.Reads.ShouldAllBe(r => r.Length <= ErrorScanner.ChunkSize);
    }

    [Fact]
    public void Splits_the_range_into_exactly_the_requested_block_count()
    {
        const ulong size = (256 * MiB) + 4096;
        var device = new FakeBlockDevice(size);

        var result = Scan(device, new ScanOptions(BlockCount: 1000));

        result.Blocks.Count.ShouldBe(1000);
        result.BlockStart(0).ShouldBe(0UL);
        result.BlockStart(1000).ShouldBe(size);
        var sizes = Enumerable.Range(0, 1000).Select(i => result.BlockStart(i + 1) - result.BlockStart(i)).ToArray();
        sizes.ShouldAllBe(s => s % 512 == 0);
        (sizes.Max() - sizes.Min()).ShouldBeLessThanOrEqualTo(512UL);
        device.Reads.Sum(r => (long)r.Length).ShouldBe((long)size);
    }

    [Fact]
    public void Block_count_is_limited_by_the_sector_count()
    {
        var result = Scan(new FakeBlockDevice(8 * 512), new ScanOptions(BlockCount: 100));

        result.Blocks.Count.ShouldBe(8);
    }

    [Fact]
    public void Damaged_sectors_mark_their_block_and_are_refined_to_lbas()
    {
        var device = new FakeBlockDevice(64 * MiB);
        device.BadOffsets.Add((20 * MiB) + (7 * 512));
        device.BadOffsets.Add((20 * MiB) + (9 * 512));
        device.BadOffsets.Add(63 * MiB);

        var result = Scan(device, new ScanOptions(BlockCount: 64));

        result.DamagedBlocks.ShouldBe(2);
        result.Blocks[20].ShouldBe(BlockState.Damaged);
        result.Blocks[63].ShouldBe(BlockState.Damaged);
        result.Blocks.Count(b => b == BlockState.Good).ShouldBe(62);
        result.BadLbas.ShouldBe([(20 * MiB / 512) + 7, (20 * MiB / 512) + 9, 63 * MiB / 512]);
        result.DamagedPercent.ShouldBe(3.125);
    }

    [Fact]
    public void Bad_lba_list_is_capped()
    {
        var device = new FakeBlockDevice(4 * MiB);
        for (ulong lba = 0; lba < 100; lba++)
        {
            device.BadOffsets.Add(lba * 512);
        }

        var result = Scan(device, new ScanOptions(BlockCount: 4, MaxBadLbas: 10));

        result.BadLbas.Count.ShouldBe(10);
        result.BadLbasTruncated.ShouldBeTrue();
    }

    [Fact]
    public void Quick_scan_samples_each_block_inside_its_bounds()
    {
        var device = new FakeBlockDevice(1024 * MiB);

        var result = Scan(device, new ScanOptions(BlockCount: 100, Quick: true, QuickSampleBytes: 64 * 1024, Seed: 3));

        result.Quick.ShouldBeTrue();
        result.Blocks.ShouldAllBe(b => b == BlockState.Good);
        device.Reads.Count.ShouldBe(100);
        for (var i = 0; i < 100; i++)
        {
            device.Reads[i].Offset.ShouldBeGreaterThanOrEqualTo(result.BlockStart(i));
            (device.Reads[i].Offset + (ulong)device.Reads[i].Length).ShouldBeLessThanOrEqualTo(result.BlockStart(i + 1));
        }
    }

    [Fact]
    public void Speed_map_records_one_value_per_block()
    {
        var result = Scan(new FakeBlockDevice(16 * MiB), new ScanOptions(BlockCount: 16, SpeedMap: true));

        result.BlockSpeeds.ShouldNotBeNull();
        result.BlockSpeeds.Count.ShouldBe(16);
        Scan(new FakeBlockDevice(16 * MiB), new ScanOptions(BlockCount: 16)).BlockSpeeds.ShouldBeNull();
    }

    [Fact]
    public void Scans_only_the_range()
    {
        var device = new FakeBlockDevice(100 * MiB);
        var range = new DriveRange(10 * MiB, 20 * MiB);

        var result = Scan(device, new ScanOptions(range, BlockCount: 10));

        result.Range.ShouldBe(range);
        device.Reads.ShouldAllBe(r => r.Offset >= range.Start && r.Offset + (ulong)r.Length <= range.End);
        device.Reads.Sum(r => (long)r.Length).ShouldBe((long)(10 * MiB));
    }

    [Fact]
    public void Reports_progress_for_every_block()
    {
        var reports = new List<ScanProgress>();
        var device = new FakeBlockDevice(8 * MiB);
        device.BadOffsets.Add(3 * MiB);

        Scan(device, new ScanOptions(BlockCount: 8), new SyncProgress<ScanProgress>(reports.Add));

        reports.Select(r => r.Block).ShouldBe(Enumerable.Range(0, 8));
        reports[3].State.ShouldBe(BlockState.Damaged);
        reports[3].MegabytesPerSecond.ShouldBeNull();
        reports[^1].Fraction.ShouldBe(1.0);
        reports[^1].DamagedBlocks.ShouldBe(1);
    }

    [Fact]
    public void Cancellation_stops_the_scan()
    {
        using var cts = new CancellationTokenSource();
        var device = new FakeBlockDevice(64 * MiB);
        var progress = new SyncProgress<ScanProgress>(p =>
        {
            if (p.Block == 9)
            {
                cts.Cancel();
            }
        });

        Should.Throw<OperationCanceledException>(
            () => ErrorScanner.Scan(device, new DriveId(0), new ScanOptions(BlockCount: 64), progress, cts.Token));
        device.Reads.Sum(r => (long)r.Length).ShouldBe((long)(10 * MiB));
    }

    [Fact]
    public void Exports_bad_lbas_as_csv_and_json()
    {
        var device = new FakeBlockDevice(4 * MiB, 4096);
        device.BadOffsets.Add(8192);
        var result = Scan(device, new ScanOptions(BlockCount: 4));

        ScanExport.ToCsv(result).ShouldBe("lba,byte_offset\n2,8192\n");
        ScanExport.ToJson(result).ShouldContain("\"badLbas\": [\n    2\n  ]".Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        Should.Throw<ArgumentException>(() => ScanExport.Write(result, "x.txt"));
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
