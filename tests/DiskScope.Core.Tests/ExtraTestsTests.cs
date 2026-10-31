using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;

namespace DiskScope.Core.Tests;

public class ExtraTestsTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private static ExtraTestResult Run(FakeBlockDevice device, ExtraTestOptions options) =>
        ExtraTestRunner.Run(device, new DriveId(2), options, null, CancellationToken.None);

    private static ExtraTestOptions Options(params ExtraTestKind[] tests) =>
        new(tests, DurationPerTest: TimeSpan.FromMilliseconds(50), SequentialBytes: 16 * 1024 * 1024, Seed: 1);

    [Fact]
    public void Runs_all_tests_by_default_in_spec_order()
    {
        var result = Run(new FakeBlockDevice(GiB), Options() with { Tests = null });

        result.Rows.Select(r => r.Kind).ShouldBe(Enum.GetValues<ExtraTestKind>());
        result.Rows.ShouldAllBe(r => r.Operations > 0 && r.Iops > 0);
    }

    [Theory]
    [InlineData(ExtraTestKind.RandomSector, 512)]
    [InlineData(ExtraTestKind.Random4K, 4096)]
    [InlineData(ExtraTestKind.Random64K, 65536)]
    [InlineData(ExtraTestKind.Random8M, 8 * 1024 * 1024)]
    public void Random_tests_use_their_transfer_size(ExtraTestKind kind, int size)
    {
        var device = new FakeBlockDevice(GiB);

        var row = Run(device, Options(kind)).Rows.Single();

        row.TransferSize.ShouldBe(size);
        device.Reads.Skip(32).ShouldAllBe(r => r.Length == size);
    }

    [Fact]
    public void Butterfly_alternates_between_start_and_end_moving_inwards()
    {
        var positions = ExtraTestRunner.Butterfly(new DriveRange(0, 1_000_000 * 512), 512, 512).Take(6).ToArray();

        positions[0].ShouldBe(0UL);
        positions[1].ShouldBe((1_000_000UL * 512) - 512);
        positions[2].ShouldBeGreaterThan(positions[0]);
        positions[3].ShouldBeLessThan(positions[1]);
        (positions[1] - positions[0]).ShouldBeGreaterThan(positions[3] - positions[2]);
    }

    [Fact]
    public void Sequential_tests_read_outer_middle_and_inner_regions()
    {
        var device = new FakeBlockDevice(GiB);

        Run(device, Options(ExtraTestKind.SequentialOuter, ExtraTestKind.SequentialMiddle, ExtraTestKind.SequentialInner));

        var reads = device.Reads.Skip(32).ToArray();
        reads.Length.ShouldBe(48);
        reads[0].Offset.ShouldBe(0UL);
        reads[16].Offset.ShouldBe(GiB / 2);
        reads[^1].Offset.ShouldBe(GiB - (1024 * 1024));
    }

    [Fact]
    public void Burst_reads_one_block_repeatedly_and_ignores_the_first_read()
    {
        var device = new FakeBlockDevice(GiB);

        var row = Run(device, Options(ExtraTestKind.Burst)).Rows.Single();

        row.Operations.ShouldBe(32);
        device.Reads.Skip(32).Select(r => r.Offset).Distinct().ShouldBe([0UL]);
    }

    [Fact]
    public void Cache_test_doubles_the_region_up_to_the_maximum()
    {
        var result = Run(new FakeBlockDevice(GiB), Options(ExtraTestKind.Burst) with { CacheTest = true, CacheMaxBytes = 8 * 1024 * 1024 });

        result.Cache.Select(c => c.RegionBytes).ShouldBe([512 * 1024, 1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024, 8 * 1024 * 1024]);
    }

    [Fact]
    public void Align4K_aligns_random_and_butterfly_offsets()
    {
        var device = new FakeBlockDevice(GiB);

        Run(device, Options(ExtraTestKind.RandomSector, ExtraTestKind.Butterfly) with { Align4K = true });

        device.Reads.ShouldAllBe(r => r.Offset % 4096 == 0);
    }
}
