using DiskScope.Core.Monitoring;

namespace DiskScope.Core.Tests;

public class MonitorStatisticsTests
{
    private static readonly DateTime T0 = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Local);

    private static DiskIoEvent Read(double second, long offset, int size, int pid = 10, string name = "app.exe") =>
        new(T0.AddSeconds(second), false, offset, size, pid, name);

    private static DiskIoEvent Write(double second, long offset, int size, int pid = 10, string name = "app.exe") =>
        new(T0.AddSeconds(second), true, offset, size, pid, name);

    [Fact]
    public void Aggregates_rates_per_second_and_fills_idle_seconds()
    {
        var stats = new MonitorStatistics(1_000_000, TimeSpan.FromMinutes(30), T0);
        stats.Add(Read(0.1, 0, 4096));
        stats.Add(Read(0.5, 0, 4096));
        stats.Add(Write(0.9, 0, 8192));
        stats.Add(Read(3.2, 0, 1024));

        var snapshot = stats.Snapshot(T0.AddSeconds(4));

        snapshot.Rates.Select(r => r.Second).ShouldBe([T0, T0.AddSeconds(1), T0.AddSeconds(2), T0.AddSeconds(3)]);
        snapshot.Rates[0].ShouldBe(new RatePoint(T0, 8192, 8192, 2, 1));
        snapshot.Rates[1].ReadBytes.ShouldBe(0);
        snapshot.Read.Bytes.ShouldBe(9216);
        snapshot.Read.Count.ShouldBe(3);
        snapshot.Read.MaximumKBps.ShouldBe(8);
        snapshot.Write.MaximumKBps.ShouldBe(8);
        snapshot.PeakReadIops.ShouldBe(2);
        snapshot.Elapsed.ShouldBe(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Sliding_window_drops_old_seconds()
    {
        var stats = new MonitorStatistics(1_000_000, TimeSpan.FromSeconds(10), T0);
        stats.Add(Read(0, 0, 512));
        stats.Add(Read(30, 0, 512));

        var rates = stats.Snapshot(T0.AddSeconds(31)).Rates;

        rates[0].Second.ShouldBe(T0.AddSeconds(20));
        rates[^1].Second.ShouldBe(T0.AddSeconds(30));
        stats.Snapshot(T0).Read.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(512, 0)]
    [InlineData(513, 1)]
    [InlineData(4096, 3)]
    [InlineData(65536, 7)]
    [InlineData(2097152, 12)]
    [InlineData(4194304, 13)]
    public void Assigns_size_classes(int size, int index) => MonitorStatistics.SizeIndex(size).ShouldBe(index);

    [Fact]
    public void Splits_small_and_large_requests_and_counts_bytes()
    {
        var stats = new MonitorStatistics(1_000_000, TimeSpan.FromMinutes(1), T0);
        stats.Add(Read(0, 0, 4096));
        stats.Add(Write(0, 0, 65536));
        stats.Add(Read(0, 0, 131072));

        var snapshot = stats.Snapshot(T0);

        snapshot.SmallRequests.ShouldBe(2);
        snapshot.LargeRequests.ShouldBe(1);
        snapshot.Sizes.Single(s => s.MaxBytes == 4096).ReadCount.ShouldBe(1);
        snapshot.Sizes.Single(s => s.MaxBytes == 65536).WriteBytes.ShouldBe(65536);
        snapshot.Sizes.Count.ShouldBe(14);
    }

    [Fact]
    public void Maps_offsets_to_position_buckets()
    {
        var stats = new MonitorStatistics(1000, TimeSpan.FromMinutes(1), T0, positionBuckets: 10);
        stats.Add(Read(0, 0, 512));
        stats.Add(Read(0, 999, 512));
        stats.Add(Write(0, 555, 512));

        var positions = stats.Snapshot(T0).Positions;

        positions[0].ReadCount.ShouldBe(1);
        positions[9].ReadCount.ShouldBe(1);
        positions[5].WriteCount.ShouldBe(1);
    }

    [Fact]
    public void Groups_by_process_sorted_by_volume()
    {
        var stats = new MonitorStatistics(1000, TimeSpan.FromMinutes(1), T0);
        stats.Add(Read(0, 0, 100, 1, "a.exe"));
        stats.Add(Write(0, 0, 5000, 2, "b.exe"));
        stats.Add(Read(0, 0, 300, 1, "a.exe"));

        var processes = stats.Snapshot(T0).Processes;

        processes.Select(p => p.Name).ShouldBe(["b.exe", "a.exe"]);
        processes[1].ShouldBe(new ProcessUsage(1, "a.exe", 400, 0, 2, 0));
    }
}
