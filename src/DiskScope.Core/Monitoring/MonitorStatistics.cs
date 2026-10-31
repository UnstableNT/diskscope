namespace DiskScope.Core.Monitoring;

/// <summary>One disk request seen by the monitor.</summary>
public readonly record struct DiskIoEvent(DateTime Time, bool IsWrite, long Offset, int Size, int ProcessId, string ProcessName);

/// <summary>Bytes and requests per second for read and write.</summary>
public readonly record struct RatePoint(DateTime Second, long ReadBytes, long WriteBytes, int ReadCount, int WriteCount);

public sealed record DirectionTotals(long Bytes, long Count, double CurrentKBps, double MaximumKBps)
{
    public double AverageRequestBytes => Count == 0 ? 0 : (double)Bytes / Count;
}

/// <summary>Request size class of the block size histogram (SPEC 4.9).</summary>
public sealed record SizeBucket(string Label, int MaxBytes, long ReadCount, long WriteCount, long ReadBytes, long WriteBytes);

public sealed record PositionBucket(long Start, long End, long ReadCount, long WriteCount);

public sealed record ProcessUsage(int ProcessId, string Name, long ReadBytes, long WriteBytes, long ReadCount, long WriteCount);

/// <summary>Consistent view of all monitor statistics at one moment.</summary>
public sealed record MonitorSnapshot(
    TimeSpan Elapsed,
    IReadOnlyList<RatePoint> Rates,
    DirectionTotals Read,
    DirectionTotals Write,
    IReadOnlyList<SizeBucket> Sizes,
    long SmallRequests,
    long LargeRequests,
    IReadOnlyList<PositionBucket> Positions,
    IReadOnlyList<ProcessUsage> Processes,
    int PeakReadIops,
    int PeakWriteIops);

/// <summary>
/// Thread-safe aggregation of disk events: per-second rates in a sliding window, size histogram,
/// position heatmap, per-process table and totals (SPEC 4.9). Pure logic, unit-testable.
/// </summary>
public sealed class MonitorStatistics
{
    public const int SmallRequestLimit = 64 * 1024;

    private static readonly int[] SizeLimits =
        [512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152];

    private readonly object _gate = new();
    private readonly TimeSpan _window;
    private readonly long _driveSize;
    private readonly LinkedList<RatePoint> _rates = new();
    private readonly long[] _sizeCount = new long[SizeLimits.Length * 2 + 2];
    private readonly long[] _sizeBytes = new long[SizeLimits.Length * 2 + 2];
    private readonly long[] _positionRead;
    private readonly long[] _positionWrite;
    private readonly Dictionary<(int, string), long[]> _processes = [];
    private readonly DateTime _started;
    private long _readBytes, _writeBytes, _readCount, _writeCount;
    private double _maxRead, _maxWrite;
    private int _peakReadIops, _peakWriteIops;

    public MonitorStatistics(long driveSize, TimeSpan window, DateTime started, int positionBuckets = 200)
    {
        _driveSize = Math.Max(1, driveSize);
        _window = window;
        _started = started;
        _positionRead = new long[positionBuckets];
        _positionWrite = new long[positionBuckets];
    }

    public void Add(DiskIoEvent e)
    {
        var second = new DateTime(e.Time.Ticks - (e.Time.Ticks % TimeSpan.TicksPerSecond), e.Time.Kind);
        var sizeIndex = SizeIndex(e.Size) * 2 + (e.IsWrite ? 1 : 0);
        var position = (int)Math.Clamp(e.Offset * _positionRead.Length / _driveSize, 0, _positionRead.Length - 1);

        lock (_gate)
        {
            var last = _rates.Last;
            if (last is null || last.Value.Second < second)
            {
                // Seconds without I/O appear as zero points so the chart shows idle time.
                var next = last is null ? second : last.Value.Second.AddSeconds(1);
                for (; next < second; next = next.AddSeconds(1))
                {
                    AddPoint(new RatePoint(next, 0, 0, 0, 0));
                }

                AddPoint(new RatePoint(second, 0, 0, 0, 0));
                last = _rates.Last!;
            }

            // Late events (out of order) count towards the most recent second.
            var point = last.Value;
            last.Value = e.IsWrite
                ? point with { WriteBytes = point.WriteBytes + e.Size, WriteCount = point.WriteCount + 1 }
                : point with { ReadBytes = point.ReadBytes + e.Size, ReadCount = point.ReadCount + 1 };
            _maxRead = Math.Max(_maxRead, last.Value.ReadBytes / 1024.0);
            _maxWrite = Math.Max(_maxWrite, last.Value.WriteBytes / 1024.0);
            _peakReadIops = Math.Max(_peakReadIops, last.Value.ReadCount);
            _peakWriteIops = Math.Max(_peakWriteIops, last.Value.WriteCount);

            if (e.IsWrite)
            {
                _writeBytes += e.Size;
                _writeCount++;
                _positionWrite[position]++;
            }
            else
            {
                _readBytes += e.Size;
                _readCount++;
                _positionRead[position]++;
            }

            _sizeCount[sizeIndex]++;
            _sizeBytes[sizeIndex] += e.Size;

            var key = (e.ProcessId, e.ProcessName);
            if (!_processes.TryGetValue(key, out var usage))
            {
                _processes[key] = usage = new long[4];
            }

            usage[e.IsWrite ? 1 : 0] += e.Size;
            usage[e.IsWrite ? 3 : 2]++;
        }
    }

    public MonitorSnapshot Snapshot(DateTime now)
    {
        lock (_gate)
        {
            var rates = _rates.ToArray();
            var current = rates.Length >= 2 ? rates[^2] : default;
            var sizes = new List<SizeBucket>(SizeLimits.Length + 1);
            for (var i = 0; i <= SizeLimits.Length; i++)
            {
                var label = i < SizeLimits.Length ? ByteSize.FormatBinary((ulong)SizeLimits[i]) : $"> {ByteSize.FormatBinary((ulong)SizeLimits[^1])}";
                sizes.Add(new SizeBucket(label, i < SizeLimits.Length ? SizeLimits[i] : int.MaxValue, _sizeCount[i * 2], _sizeCount[(i * 2) + 1], _sizeBytes[i * 2], _sizeBytes[(i * 2) + 1]));
            }

            var small = sizes.Where(s => s.MaxBytes <= SmallRequestLimit).Sum(s => s.ReadCount + s.WriteCount);
            var large = sizes.Where(s => s.MaxBytes > SmallRequestLimit).Sum(s => s.ReadCount + s.WriteCount);
            var bucket = _driveSize / _positionRead.Length;
            var positions = Enumerable.Range(0, _positionRead.Length)
                .Select(i => new PositionBucket(i * bucket, (i + 1) * bucket, _positionRead[i], _positionWrite[i]))
                .ToArray();
            var processes = _processes
                .Select(p => new ProcessUsage(p.Key.Item1, p.Key.Item2, p.Value[0], p.Value[1], p.Value[2], p.Value[3]))
                .OrderByDescending(p => p.ReadBytes + p.WriteBytes)
                .ToArray();

            return new MonitorSnapshot(
                now - _started,
                rates,
                new DirectionTotals(_readBytes, _readCount, current.ReadBytes / 1024.0, _maxRead),
                new DirectionTotals(_writeBytes, _writeCount, current.WriteBytes / 1024.0, _maxWrite),
                sizes,
                small,
                large,
                positions,
                processes,
                _peakReadIops,
                _peakWriteIops);
        }
    }

    internal static int SizeIndex(int size)
    {
        for (var i = 0; i < SizeLimits.Length; i++)
        {
            if (size <= SizeLimits[i])
            {
                return i;
            }
        }

        return SizeLimits.Length;
    }

    private void AddPoint(RatePoint point)
    {
        _rates.AddLast(point);
        while (_rates.First is { } first && point.Second - first.Value.Second > _window)
        {
            _rates.RemoveFirst();
        }
    }
}
