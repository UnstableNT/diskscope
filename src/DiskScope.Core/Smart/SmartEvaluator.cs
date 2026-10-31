namespace DiskScope.Core.Smart;

/// <summary>
/// DiskScope's health rules (SPEC 4.3 "Bewertung").
/// <list type="bullet">
/// <item>ATA: Error if the current value is at or below a valid threshold (1–253).</item>
/// <item>ATA: Warning if the worst value ever was at or below the threshold.</item>
/// <item>ATA: Warning if the raw value of 5, 196, 197 or 198 (reallocated / pending / uncorrectable sectors) is above 0.</item>
/// <item>ATA: Error if SMART RETURN STATUS or IOCTL_STORAGE_PREDICT_FAILURE reports a failure.</item>
/// <item>NVMe: Error if any Critical Warning bit is set.</item>
/// <item>NVMe: Warning if Available Spare is below its threshold, Percentage Used reaches 100 %,
/// or Media and Data Integrity Errors are above 0.</item>
/// <item>Overall status: the worst status of all rows.</item>
/// </list>
/// </summary>
public static class SmartEvaluator
{
    private static readonly HashSet<int> SectorCounters = [5, 196, 197, 198];

    public static HealthStatus Evaluate(SmartEntry attribute)
    {
        if (attribute.Threshold is { } threshold and > 0 and < 0xFE)
        {
            if (attribute.Current <= threshold)
            {
                return HealthStatus.Error;
            }

            if (attribute.Worst <= threshold)
            {
                return HealthStatus.Warning;
            }
        }

        return SectorCounters.Contains(attribute.Id) && (attribute.RawValue & 0xFFFF_FFFF) > 0
            ? HealthStatus.Warning
            : HealthStatus.Ok;
    }

    public static IReadOnlyList<SmartEntry> EvaluateNvme(IReadOnlyList<SmartEntry> rows)
    {
        var spare = rows.FirstOrDefault(r => r.Id == NvmeHealth.AvailableSpare)?.RawValue;
        var spareThreshold = rows.FirstOrDefault(r => r.Id == NvmeHealth.AvailableSpareThreshold)?.RawValue;

        return rows.Select(row => row with
        {
            Status = row.Id switch
            {
                NvmeHealth.CriticalWarning when row.RawValue != 0 => HealthStatus.Error,
                NvmeHealth.AvailableSpare when spare < spareThreshold => HealthStatus.Warning,
                NvmeHealth.PercentageUsed when row.RawValue >= 100 => HealthStatus.Warning,
                NvmeHealth.MediaErrors when row.RawValue > 0 => HealthStatus.Warning,
                _ => HealthStatus.Ok,
            },
        }).ToArray();
    }

    /// <param name="failurePredicted">From SMART RETURN STATUS or IOCTL_STORAGE_PREDICT_FAILURE, if known.</param>
    public static HealthStatus Overall(IReadOnlyList<SmartEntry> attributes, bool? failurePredicted)
    {
        if (failurePredicted == true)
        {
            return HealthStatus.Error;
        }

        if (attributes.Count == 0)
        {
            return failurePredicted == false ? HealthStatus.Ok : HealthStatus.Unknown;
        }

        return attributes.Max(a => a.Status) switch
        {
            HealthStatus.Error => HealthStatus.Error,
            HealthStatus.Warning => HealthStatus.Warning,
            _ => HealthStatus.Ok,
        };
    }
}
