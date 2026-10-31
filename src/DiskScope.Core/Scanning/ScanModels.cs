using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;

namespace DiskScope.Core.Scanning;

public enum BlockState : byte { Unchecked, Good, Damaged }

/// <summary>Options of the read-only error scan (SPEC 4.5).</summary>
/// <param name="BlockCount">Number of equally sized blocks; the GUI derives it from the grid area.</param>
/// <param name="Quick">Read a sample of each block instead of the whole block.</param>
/// <param name="QuickSampleBytes">Bytes read per block in quick mode.</param>
/// <param name="SpeedMap">Record the read speed of each block.</param>
/// <param name="MaxBadLbas">Upper bound for the list of damaged LBAs.</param>
public sealed record ScanOptions(
    DriveRange? Range = null,
    int BlockCount = 10_000,
    bool Quick = false,
    int QuickSampleBytes = 256 * 1024,
    bool SpeedMap = false,
    int MaxBadLbas = 100_000,
    int? Seed = null);

/// <summary>One block finished. <paramref name="Fraction"/> covers the whole scan (0–1).</summary>
public sealed record ScanProgress(
    int Block, BlockState State, double? MegabytesPerSecond, ulong Position, double Fraction, int DamagedBlocks);

/// <summary>
/// Scan result. <see cref="BlockSize"/> is the average block size; exact bounds come from <see cref="BlockStart"/>.
/// </summary>
public sealed record ScanResult(
    DriveId Drive,
    DriveRange Range,
    ulong BlockSize,
    uint SectorSize,
    bool Quick,
    IReadOnlyList<BlockState> Blocks,
    IReadOnlyList<double>? BlockSpeeds,
    IReadOnlyList<ulong> BadLbas,
    bool BadLbasTruncated,
    double AverageMBps,
    TimeSpan Duration,
    DateTimeOffset Timestamp)
{
    public int DamagedBlocks => Blocks.Count(b => b == BlockState.Damaged);

    public double DamagedPercent => Blocks.Count == 0 ? 0 : Math.Round(DamagedBlocks * 100.0 / Blocks.Count, 3);

    /// <summary>First byte of block <paramref name="block"/>; <c>BlockStart(Blocks.Count)</c> is the range end.</summary>
    public ulong BlockStart(int block) => ErrorScanner.BlockBoundary(Range, Blocks.Count, block, SectorSize);
}
