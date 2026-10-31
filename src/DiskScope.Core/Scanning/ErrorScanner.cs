using System.Diagnostics;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Scanning;

/// <summary>
/// Read-only error scan (SPEC 4.5): splits the range into equally sized blocks, reads each block in chunks
/// and narrows failing chunks down to single sectors to collect the damaged LBAs.
/// </summary>
public sealed partial class ErrorScanner(ILogger<ErrorScanner>? logger = null)
{
    /// <summary>Read size within a block; also the buffer size.</summary>
    internal const int ChunkSize = 1024 * 1024;

    private readonly ILogger _logger = logger ?? NullLogger<ErrorScanner>.Instance;

    public async Task<ScanResult> ScanAsync(
        DriveInfo drive,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await MeasurementThread.Run(
            () =>
            {
                using var device = RawDevice.Open(drive.DevicePath, drive.SizeBytes, drive.LogicalSectorSize, ChunkSize);
                return Scan(device, drive.Id, options, progress, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
        LogResult(drive.Id.Number, result.Blocks.Count, result.DamagedBlocks, result.BadLbas.Count);
        return result;
    }

    /// <summary>Block count limited to the number of sectors in the range.</summary>
    internal static int EffectiveBlockCount(DriveRange range, int requested, uint sector) =>
        (int)Math.Clamp((ulong)Math.Max(1, requested), 1UL, Math.Max(1, range.Length / sector));

    /// <summary>
    /// Start of block <paramref name="block"/> (or the range end for <c>block == count</c>): the range is split
    /// into exactly <paramref name="count"/> sector-aligned blocks whose sizes differ by at most one sector.
    /// </summary>
    public static ulong BlockBoundary(DriveRange range, int count, int block, uint sector) =>
        block >= count
            ? range.End
            : range.Start + (ulong)((UInt128)range.Length * (uint)block / (uint)count) / sector * sector;

    internal static ScanResult Scan(
        IBlockDevice device,
        DriveId drive,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sector = device.SectorSize;
        var range = (options.Range ?? DriveRange.Full(device.Size)).AlignTo(sector, device.Size);
        var blockCount = EffectiveBlockCount(range, options.BlockCount, sector);
        var chunk = (ulong)Math.Max(sector, ChunkSize / sector * sector);
        var sample = (ulong)Math.Max(sector, options.QuickSampleBytes / (int)sector * sector);

        var states = new BlockState[blockCount];
        var speeds = options.SpeedMap ? new double[blockCount] : null;
        var badLbas = new List<ulong>();
        var truncated = false;
        var random = MeasurementThread.CreateRandom(options.Seed);
        var damaged = 0;
        ulong bytesRead = 0;
        double readSeconds = 0;
        var started = Stopwatch.GetTimestamp();

        for (var block = 0; block < blockCount; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockStart = BlockBoundary(range, blockCount, block, sector);
            var blockEnd = BlockBoundary(range, blockCount, block + 1, sector);

            // Quick scan: one sample at a random sector-aligned position inside the block.
            var (readStart, readEnd) = options.Quick && blockEnd - blockStart > sample
                ? QuickWindow(random, blockStart, blockEnd, sample, sector)
                : (blockStart, blockEnd);

            var ok = true;
            var t0 = Stopwatch.GetTimestamp();
            for (var offset = readStart; offset < readEnd; offset += chunk)
            {
                var length = (int)Math.Min(chunk, readEnd - offset);
                try
                {
                    device.Read(offset, length);
                }
                catch (System.IO.IOException)
                {
                    ok = false;
                    truncated |= Refine(device, offset, length, sector, badLbas, options.MaxBadLbas, cancellationToken);
                }
            }

            var seconds = MeasurementThread.ElapsedMilliseconds(t0, Stopwatch.GetTimestamp()) / 1000.0;
            var mbps = ok && seconds > 0 ? (readEnd - readStart) / seconds / 1e6 : (double?)null;
            if (ok)
            {
                bytesRead += readEnd - readStart;
                readSeconds += seconds;
            }
            else
            {
                damaged++;
            }

            states[block] = ok ? BlockState.Good : BlockState.Damaged;
            if (speeds is not null)
            {
                speeds[block] = mbps ?? 0;
            }

            progress?.Report(new ScanProgress(block, states[block], mbps, blockStart, (double)(block + 1) / blockCount, damaged));
        }

        return new ScanResult(
            drive,
            range,
            range.Length / (ulong)blockCount,
            sector,
            options.Quick,
            states,
            speeds,
            badLbas,
            truncated,
            readSeconds > 0 ? bytesRead / readSeconds / 1e6 : 0,
            Stopwatch.GetElapsedTime(started),
            DateTimeOffset.Now);
    }

    private static (ulong Start, ulong End) QuickWindow(Random random, ulong start, ulong end, ulong sample, uint sector)
    {
        var slots = (end - start - sample) / sector;
        var offset = start + ((ulong)random.NextInt64(0, (long)slots + 1) * sector);
        return (offset, offset + sample);
    }

    /// <summary>Reads a failed chunk sector by sector. Returns true if the LBA list hit its limit.</summary>
    private static bool Refine(
        IBlockDevice device, ulong offset, int length, uint sector, List<ulong> badLbas, int max, CancellationToken cancellationToken)
    {
        var truncated = false;
        for (var position = offset; position < offset + (ulong)length; position += sector)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                device.Read(position, (int)sector);
            }
            catch (System.IO.IOException)
            {
                if (badLbas.Count < max)
                {
                    badLbas.Add(position / sector);
                }
                else
                {
                    truncated = true;
                }
            }
        }

        return truncated;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan drive {Drive}: {Blocks} blocks, {Damaged} damaged, {BadLbas} bad LBAs")]
    private partial void LogResult(int drive, int blocks, int damaged, int badLbas);
}
