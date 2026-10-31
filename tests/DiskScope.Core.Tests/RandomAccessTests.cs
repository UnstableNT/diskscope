using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;

namespace DiskScope.Core.Tests;

public class RandomAccessTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private static RandomAccessResult Run(FakeBlockDevice device, RandomAccessOptions options) =>
        RandomAccessRunner.Run(device, new DriveId(3), options, null, CancellationToken.None);

    private static RandomAccessOptions Options(IReadOnlyList<int> sizes, bool align4K = false, DriveRange? range = null) =>
        new(sizes, align4K, range, TimeSpan.FromSeconds(30), MaxOperationsPerSize: 200, MaxSamplesPerSize: 50, Seed: 7);

    [Fact]
    public void Measures_each_size_with_bounded_samples()
    {
        var device = new FakeBlockDevice(GiB);

        var result = Run(device, Options(RandomAccessOptions.DefaultSizes));

        result.Sizes.Select(s => s.Size).ShouldBe([512, 4096, 65536, 1024 * 1024, RandomAccessOptions.Mixed]);
        result.Sizes.ShouldAllBe(s => s.Operations == 200 && s.Samples.Count == 50);
        result.Sizes.ShouldAllBe(s => s.MaximumMs >= s.AverageMs);
        result.Sizes.Take(4).ShouldAllBe(s => s.Samples.All(x => x.Size == s.Size));
        device.Reads.Count.ShouldBe(1000 + 32); // plus warm-up
    }

    [Fact]
    public void Mixed_size_uses_several_transfer_sizes()
    {
        var device = new FakeBlockDevice(GiB);

        Run(device, Options([RandomAccessOptions.Mixed]));

        device.Reads.Select(r => r.Length).Distinct().Count().ShouldBeGreaterThan(2);
    }

    [Fact]
    public void Align4K_aligns_every_offset()
    {
        var device = new FakeBlockDevice(GiB);

        Run(device, Options([512, 4096], align4K: true));

        device.Reads.ShouldAllBe(r => r.Offset % 4096 == 0);
    }

    [Fact]
    public void Without_alignment_512_byte_offsets_occur()
    {
        var device = new FakeBlockDevice(GiB);

        Run(device, Options([512]));

        device.Reads.ShouldContain(r => r.Offset % 4096 != 0);
    }

    [Fact]
    public void Stays_inside_the_range_and_is_repeatable()
    {
        var range = DriveRange.FromPercent(GiB, 25, 30);
        var a = new FakeBlockDevice(GiB);
        var b = new FakeBlockDevice(GiB);

        Run(a, Options([4096, 1024 * 1024], range: range));
        Run(b, Options([4096, 1024 * 1024], range: range));

        a.Reads.ShouldAllBe(r => r.Offset >= range.Start && r.Offset + (ulong)r.Length <= range.End);
        a.Reads.ShouldBe(b.Reads);
    }

    [Fact]
    public void Skips_sizes_smaller_than_the_sector()
    {
        var result = Run(new FakeBlockDevice(GiB, 4096), Options([512, 4096]));

        result.Sizes.Select(s => s.Size).ShouldBe([4096]);
    }

    [Fact]
    public void Only_invalid_sizes_is_an_error() =>
        Should.Throw<ArgumentException>(() => Run(new FakeBlockDevice(GiB, 4096), Options([512])));

    [Fact]
    public void Counts_read_errors()
    {
        var device = new FakeBlockDevice(8192);
        device.BadOffsets.Add(0);

        var result = Run(device, Options([4096], align4K: true));

        result.ReadErrors.ShouldBeGreaterThan(0);
        result.Sizes.Single().Operations.ShouldBe(200);
    }
}
