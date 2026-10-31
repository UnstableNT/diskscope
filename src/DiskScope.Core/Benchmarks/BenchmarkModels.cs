using DiskScope.Core.Devices;

namespace DiskScope.Core.Benchmarks;

/// <summary>Options of the read benchmark (SPEC 4.4).</summary>
/// <param name="BlockSize">Transfer block size, 512 B – 8 MiB, default 64 KiB.</param>
/// <param name="Range">Part of the drive to measure; whole drive if null.</param>
/// <param name="Points">Measuring points spread evenly over the range.</param>
/// <param name="BlocksPerPoint">Consecutive blocks read at each point.</param>
/// <param name="AccessSamples">Random single-sector reads for the access time.</param>
/// <param name="Seed">Seed for the random positions. Null picks new positions per run, so the drive cache cannot replay a previous run; tests pass a fixed seed.</param>
public sealed record BenchmarkOptions(
    int BlockSize = 64 * 1024,
    DriveRange? Range = null,
    int Points = 100,
    int BlocksPerPoint = 16,
    bool MeasureTransfer = true,
    bool MeasureAccessTime = true,
    bool MeasureBurst = true,
    int AccessSamples = 200,
    int BurstRepeats = 32,
    int? Seed = null);

public readonly record struct TransferSample(ulong Position, double MegabytesPerSecond);

public readonly record struct AccessSample(ulong Position, double Milliseconds);

public enum BenchmarkPhase { Transfer, AccessTime, Burst }

/// <summary>Live progress: <paramref name="Fraction"/> covers the whole run (0–1).</summary>
public sealed record BenchmarkProgress(
    BenchmarkPhase Phase, double Fraction, TransferSample? Transfer = null, AccessSample? Access = null);

/// <summary>Benchmark result. Rates in MB/s (10^6 bytes per second), times in ms.</summary>
public sealed record BenchmarkResult(
    DriveId Drive,
    int BlockSize,
    DriveRange Range,
    IReadOnlyList<TransferSample> Transfer,
    IReadOnlyList<AccessSample> Access,
    double? MinimumMBps,
    double? MaximumMBps,
    double? AverageMBps,
    double? AccessTimeMs,
    double? BurstMBps,
    double CpuPercent,
    int ReadErrors,
    TimeSpan Duration,
    DateTimeOffset Timestamp);

/// <summary>Options of the random access test (SPEC 4.10).</summary>
/// <param name="Sizes">Transfer sizes in bytes; <see cref="RandomAccessOptions.Mixed"/> (0) mixes 512 B, 4 KiB, 64 KiB and 1 MiB.</param>
public sealed record RandomAccessOptions(
    IReadOnlyList<int> Sizes,
    bool Align4K = false,
    DriveRange? Range = null,
    TimeSpan? DurationPerSize = null,
    int MaxOperationsPerSize = 10_000,
    int MaxSamplesPerSize = 1_000,
    int? Seed = null)
{
    public const int Mixed = 0;

    public static IReadOnlyList<int> DefaultSizes { get; } = [512, 4096, 65536, 1024 * 1024, Mixed];

    public static IReadOnlyList<int> MixedSizes { get; } = [512, 4096, 65536, 1024 * 1024];

    public TimeSpan EffectiveDuration => DurationPerSize ?? TimeSpan.FromSeconds(3);
}

public readonly record struct RandomAccessSample(ulong Position, int Size, double Milliseconds);

/// <param name="Size">Transfer size in bytes, or 0 for mixed sizes.</param>
public sealed record RandomAccessSizeResult(
    int Size,
    long Operations,
    double OperationsPerSecond,
    double AverageMs,
    double MaximumMs,
    double AverageMBps,
    IReadOnlyList<RandomAccessSample> Samples);

public sealed record RandomAccessProgress(int Size, double Fraction, RandomAccessSample? Sample);

public sealed record RandomAccessResult(
    DriveId Drive,
    DriveRange Range,
    bool Align4K,
    IReadOnlyList<RandomAccessSizeResult> Sizes,
    int ReadErrors,
    TimeSpan Duration,
    DateTimeOffset Timestamp);
