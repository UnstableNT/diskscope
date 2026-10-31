using System.Diagnostics;
using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Benchmarks;

/// <summary>The additional read tests of SPEC 4.11.</summary>
public enum ExtraTestKind
{
    RandomSector,
    Random4K,
    Random64K,
    Random8M,
    Butterfly,
    SequentialOuter,
    SequentialMiddle,
    SequentialInner,
    Burst,
}

/// <param name="Tests">Selected tests; all if empty.</param>
/// <param name="DurationPerTest">Time budget of each random/butterfly test.</param>
/// <param name="SequentialBytes">Bytes read for each sequential test.</param>
/// <param name="CacheTest">Measure throughput over growing region sizes up to <paramref name="CacheMaxBytes"/>.</param>
public sealed record ExtraTestOptions(
    IReadOnlyList<ExtraTestKind>? Tests = null,
    bool Align4K = false,
    DriveRange? Range = null,
    TimeSpan? DurationPerTest = null,
    long SequentialBytes = 256L * 1024 * 1024,
    bool CacheTest = false,
    long CacheMaxBytes = 64L * 1024 * 1024,
    int? Seed = null)
{
    public IReadOnlyList<ExtraTestKind> EffectiveTests => Tests is { Count: > 0 } t ? t : Enum.GetValues<ExtraTestKind>();

    public TimeSpan EffectiveDuration => DurationPerTest ?? TimeSpan.FromSeconds(2);
}

/// <summary>Result columns of SPEC 4.11: I/O per second, time (ms), transfer (MB/s).</summary>
public sealed record ExtraTestRow(ExtraTestKind Kind, int TransferSize, long Operations, double Iops, double AverageMs, double MBps);

/// <summary>Throughput when repeatedly reading a region of <paramref name="RegionBytes"/>.</summary>
public sealed record CachePoint(long RegionBytes, double MBps);

public sealed record ExtraTestProgress(ExtraTestKind? Test, double Fraction, ExtraTestRow? Row, CachePoint? Cache);

public sealed record ExtraTestResult(
    DriveId Drive,
    DriveRange Range,
    IReadOnlyList<ExtraTestRow> Rows,
    IReadOnlyList<CachePoint> Cache,
    int ReadErrors,
    TimeSpan Duration,
    DateTimeOffset Timestamp);

/// <summary>Runs the additional read tests against a raw drive (read-only).</summary>
public sealed partial class ExtraTestRunner(ILogger<ExtraTestRunner>? logger = null)
{
    internal const int MaxTransfer = 8 * 1024 * 1024;
    private const int SequentialBlock = 1024 * 1024;
    private const int BurstRepeats = 32;

    private readonly ILogger _logger = logger ?? NullLogger<ExtraTestRunner>.Instance;

    public async Task<ExtraTestResult> RunAsync(
        DriveInfo drive, ExtraTestOptions options, IProgress<ExtraTestProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await MeasurementThread.Run(
            () =>
            {
                using var device = RawDevice.Open(drive.DevicePath, drive.SizeBytes, drive.LogicalSectorSize, MaxTransfer);
                return Run(device, drive.Id, options, progress, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
        LogResult(drive.Id.Number, result.Rows.Count, result.ReadErrors);
        return result;
    }

    /// <summary>Transfer size of a test; 0 for the sector size of the drive.</summary>
    public static int TransferSize(ExtraTestKind kind, uint sector) => kind switch
    {
        ExtraTestKind.RandomSector or ExtraTestKind.Butterfly => (int)sector,
        ExtraTestKind.Random4K => 4096,
        ExtraTestKind.Random64K or ExtraTestKind.Burst => 65536,
        ExtraTestKind.Random8M => MaxTransfer,
        _ => SequentialBlock,
    };

    internal static ExtraTestResult Run(
        IBlockDevice device, DriveId drive, ExtraTestOptions options, IProgress<ExtraTestProgress>? progress, CancellationToken cancellationToken)
    {
        var sector = device.SectorSize;
        var alignment = options.Align4K ? Math.Max(4096u, sector) : sector;
        var range = (options.Range ?? DriveRange.Full(device.Size)).AlignTo(sector, device.Size);
        var random = MeasurementThread.CreateRandom(options.Seed);
        var budget = (long)(options.EffectiveDuration.TotalSeconds * Stopwatch.Frequency);
        var tests = options.EffectiveTests;
        var steps = tests.Count + (options.CacheTest ? 1 : 0);
        var errors = 0;
        var rows = new List<ExtraTestRow>();
        var started = Stopwatch.GetTimestamp();

        MeasurementThread.WarmUp(device, random, range, alignment);
        for (var i = 0; i < tests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = tests[i];
            var size = (int)Math.Min((ulong)TransferSize(kind, sector), range.Length / sector * sector);
            var offsets = kind switch
            {
                ExtraTestKind.Butterfly => Butterfly(range, size, alignment),
                ExtraTestKind.SequentialOuter => Sequential(range.Start, range, options.SequentialBytes, size),
                ExtraTestKind.SequentialMiddle => Sequential(range.Start + (range.Length / 2), range, options.SequentialBytes, size),
                ExtraTestKind.SequentialInner => Sequential(range.End - (ulong)Math.Min((long)range.Length, options.SequentialBytes), range, options.SequentialBytes, size),
                ExtraTestKind.Burst => Burst(range.Start),
                _ => RandomPositions(random, range, size, alignment),
            };

            var row = Measure(device, kind, size, offsets, kind is ExtraTestKind.RandomSector or ExtraTestKind.Random4K or ExtraTestKind.Random64K or ExtraTestKind.Random8M or ExtraTestKind.Butterfly ? budget : long.MaxValue, ref errors, cancellationToken);
            rows.Add(row);
            progress?.Report(new ExtraTestProgress(kind, (double)(i + 1) / steps, row, null));
        }

        var cache = new List<CachePoint>();
        if (options.CacheTest)
        {
            foreach (var point in CacheCurve(device, range, options.CacheMaxBytes, sector, ref errors, cancellationToken))
            {
                cache.Add(point);
                progress?.Report(new ExtraTestProgress(null, (tests.Count + ((double)cache.Count / CacheSteps(options.CacheMaxBytes, sector).Count)) / steps, null, point));
            }
        }

        return new ExtraTestResult(drive, range, rows, cache, errors, Stopwatch.GetElapsedTime(started), DateTimeOffset.Now);
    }

    /// <summary>Alternating start and end positions whose distance shrinks towards the middle.</summary>
    internal static IEnumerable<ulong> Butterfly(DriveRange range, int size, uint alignment)
    {
        var low = range.Start;
        var high = (range.End - (ulong)size) / alignment * alignment;
        var step = Math.Max(alignment, (range.Length / 2000) / alignment * alignment);
        while (low < high)
        {
            yield return low;
            yield return high;
            low += step;
            high = high > step ? high - step : 0;
        }
    }

    internal static IEnumerable<ulong> Sequential(ulong start, DriveRange range, long bytes, int size)
    {
        start = Math.Min(start, range.End - (ulong)size) / (ulong)size * (ulong)size;
        start = Math.Max(start, range.Start);
        var end = Math.Min(range.End, start + (ulong)bytes);
        for (var offset = start; offset + (ulong)size <= end; offset += (ulong)size)
        {
            yield return offset;
        }
    }

    internal static IReadOnlyList<long> CacheSteps(long max, uint sector)
    {
        var steps = new List<long>();
        for (long size = 512 * 1024; size <= max; size *= 2)
        {
            steps.Add(Math.Max(sector, size / sector * sector));
        }

        return steps;
    }

    private static IEnumerable<ulong> Burst(ulong start)
    {
        for (var i = 0; i <= BurstRepeats; i++)
        {
            yield return start;
        }
    }

    private static IEnumerable<ulong> RandomPositions(Random random, DriveRange range, int size, uint alignment)
    {
        while (true)
        {
            yield return MeasurementThread.RandomOffset(random, range, size, alignment);
        }
    }

    private static ExtraTestRow Measure(
        IBlockDevice device, ExtraTestKind kind, int size, IEnumerable<ulong> offsets, long budget, ref int errors, CancellationToken cancellationToken)
    {
        long operations = 0;
        double totalMs = 0;
        var first = true;
        var start = Stopwatch.GetTimestamp();
        foreach (var offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() - start > budget)
            {
                break;
            }

            try
            {
                var t0 = Stopwatch.GetTimestamp();
                device.Read(offset, size);
                var ms = MeasurementThread.ElapsedMilliseconds(t0, Stopwatch.GetTimestamp());

                // The first burst read only fills the drive cache.
                if (kind == ExtraTestKind.Burst && first)
                {
                    first = false;
                    continue;
                }

                operations++;
                totalMs += ms;
            }
            catch (System.IO.IOException)
            {
                errors++;
            }
        }

        var seconds = totalMs / 1000;
        var iops = seconds > 0 ? operations / seconds : 0;
        return new ExtraTestRow(kind, size, operations, iops, operations > 0 ? totalMs / operations : 0, iops * size / 1e6);
    }

    private static List<CachePoint> CacheCurve(
        IBlockDevice device, DriveRange range, long max, uint sector, ref int errors, CancellationToken cancellationToken)
    {
        var points = new List<CachePoint>();
        foreach (var region in CacheSteps(Math.Min(max, (long)range.Length), sector))
        {
            var chunk = (int)Math.Min(region, SequentialBlock);
            try
            {
                // First pass loads the region into the drive cache, the second is measured.
                for (var pass = 0; pass < 2; pass++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var t0 = Stopwatch.GetTimestamp();
                    for (long offset = 0; offset < region; offset += chunk)
                    {
                        device.Read(range.Start + (ulong)offset, chunk);
                    }

                    if (pass == 1)
                    {
                        var seconds = MeasurementThread.ElapsedMilliseconds(t0, Stopwatch.GetTimestamp()) / 1000;
                        points.Add(new CachePoint(region, seconds > 0 ? region / seconds / 1e6 : 0));
                    }
                }
            }
            catch (System.IO.IOException)
            {
                errors++;
            }
        }

        return points;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Extra tests drive {Drive}: {Rows} tests, {Errors} read errors")]
    private partial void LogResult(int drive, int rows, int errors);
}
