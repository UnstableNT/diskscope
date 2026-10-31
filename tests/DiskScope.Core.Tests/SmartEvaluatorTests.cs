using DiskScope.Core.Smart;
using static DiskScope.Core.Tests.SmartBuffers;

namespace DiskScope.Core.Tests;

public class SmartEvaluatorTests
{
    private static SmartEntry Entry(int id, byte current, byte worst, byte? threshold, ulong raw) =>
        new(id, "x", ValueKind.Count, "", current, worst, threshold, "", raw, HealthStatus.Ok);

    [Theory]
    [InlineData(100, 100, 10, 0UL, HealthStatus.Ok)]
    [InlineData(10, 10, 10, 0UL, HealthStatus.Error)]
    [InlineData(50, 5, 10, 0UL, HealthStatus.Warning)]
    [InlineData(100, 100, 0, 0UL, HealthStatus.Ok)]
    [InlineData(1, 1, 0xFE, 0UL, HealthStatus.Ok)]
    public void Threshold_rules(byte current, byte worst, byte threshold, ulong raw, HealthStatus expected) =>
        SmartEvaluator.Evaluate(Entry(9, current, worst, threshold, raw)).ShouldBe(expected);

    [Theory]
    [InlineData(5)]
    [InlineData(196)]
    [InlineData(197)]
    [InlineData(198)]
    public void Sector_counters_above_zero_warn(int id)
    {
        SmartEvaluator.Evaluate(Entry(id, 100, 100, 10, 1)).ShouldBe(HealthStatus.Warning);
        SmartEvaluator.Evaluate(Entry(id, 100, 100, 10, 0)).ShouldBe(HealthStatus.Ok);
    }

    [Fact]
    public void Other_counters_do_not_warn() =>
        SmartEvaluator.Evaluate(Entry(12, 100, 100, 0, 5000)).ShouldBe(HealthStatus.Ok);

    [Fact]
    public void Overall_is_worst_row_status()
    {
        SmartEntry[] rows =
        [
            Entry(9, 100, 100, 0, 0),
            Entry(5, 100, 100, 10, 3) with { Status = HealthStatus.Warning },
        ];

        SmartEvaluator.Overall(rows, null).ShouldBe(HealthStatus.Warning);
        SmartEvaluator.Overall(rows, false).ShouldBe(HealthStatus.Warning);
        SmartEvaluator.Overall([rows[0]], null).ShouldBe(HealthStatus.Ok);
    }

    [Fact]
    public void Predicted_failure_is_an_error()
    {
        SmartEvaluator.Overall([Entry(9, 100, 100, 0, 0)], true).ShouldBe(HealthStatus.Error);
        SmartEvaluator.Overall([], true).ShouldBe(HealthStatus.Error);
        SmartEvaluator.Overall([], false).ShouldBe(HealthStatus.Ok);
        SmartEvaluator.Overall([], null).ShouldBe(HealthStatus.Unknown);
    }

    [Fact]
    public void Nvme_critical_warning_is_an_error()
    {
        var rows = NvmeHealth.Parse(NvmeLog(criticalWarning: 0x04));

        rows.Single(r => r.Id == NvmeHealth.CriticalWarning).Status.ShouldBe(HealthStatus.Error);
        SmartEvaluator.Overall(rows, null).ShouldBe(HealthStatus.Error);
    }

    [Fact]
    public void Nvme_spare_below_threshold_warns()
    {
        var rows = NvmeHealth.Parse(NvmeLog(spare: 5, spareThreshold: 10));

        rows.Single(r => r.Id == NvmeHealth.AvailableSpare).Status.ShouldBe(HealthStatus.Warning);
    }

    [Fact]
    public void Nvme_wear_and_media_errors_warn()
    {
        var rows = NvmeHealth.Parse(NvmeLog(used: 100, mediaErrors: 2));

        rows.Single(r => r.Id == NvmeHealth.PercentageUsed).Status.ShouldBe(HealthStatus.Warning);
        rows.Single(r => r.Id == NvmeHealth.MediaErrors).Status.ShouldBe(HealthStatus.Warning);
    }

    [Fact]
    public void Healthy_nvme_log_is_ok() =>
        SmartEvaluator.Overall(NvmeHealth.Parse(NvmeLog()), null).ShouldBe(HealthStatus.Ok);
}
