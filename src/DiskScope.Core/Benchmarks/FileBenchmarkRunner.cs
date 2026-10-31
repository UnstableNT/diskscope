using System.Diagnostics;
using DiskScope.Core.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DiskScope.Core.Benchmarks;

/// <summary>
/// File benchmark on a volume (SPEC 4.8): unbuffered, write-through test file that is deleted on close,
/// sequential curves, random 4 KiB with one and with n outstanding requests, optional block size sweep.
/// Needs no admin rights.
/// </summary>
public sealed partial class FileBenchmarkRunner(ILogger<FileBenchmarkRunner>? logger = null)
{
    public const int SmallBlock = 4096;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const long Mebibyte = 1024 * 1024;
    private const long FreeSpaceReserve = 64 * Mebibyte;

    private readonly ILogger _logger = logger ?? NullLogger<FileBenchmarkRunner>.Instance;

    public static IReadOnlyList<int> BlockSweep { get; } =
        Enumerable.Range(9, 15).Select(shift => 1 << shift).ToArray(); // 512 B … 8 MiB

    public async Task<FileBenchmarkResult> RunAsync(
        FileBenchmarkOptions options, IProgress<FileBenchmarkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var target = System.IO.Path.GetFullPath(options.Target);
        if (!System.IO.Directory.Exists(target))
        {
            throw new ArgumentException($"Das Ziel „{target}“ existiert nicht.");
        }

        var sector = VolumeGeometry.SectorSize(target);
        var unit = Math.Max(options.SequentialBlockSize, (int)sector);
        var fileSize = Math.Max(unit, options.FileSize / unit * unit);
        var free = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(target)!).AvailableFreeSpace;
        if (free < fileSize + FreeSpaceReserve)
        {
            throw new ArgumentException(
                $"Zu wenig freier Speicher auf {target}: {ByteSize.Format((ulong)free)} frei, {ByteSize.Format((ulong)(fileSize + FreeSpaceReserve))} nötig.");
        }

        var path = System.IO.Path.Combine(target, $"DiskScope-{Guid.NewGuid():N}.tmp");
        var started = Stopwatch.GetTimestamp();
        using var handle = System.IO.File.OpenHandle(
            path,
            System.IO.FileMode.CreateNew,
            System.IO.FileAccess.ReadWrite,
            System.IO.FileShare.None,
            FileOptions.WriteThrough | FileOptions.Asynchronous | FileOptions.DeleteOnClose | NoBuffering,
            preallocationSize: fileSize);
        LogStart(path, fileSize);

        using var sequential = new AlignedBuffer(options.SequentialBlockSize);
        using var small = new AlignedBuffer(SmallBlock * Math.Max(1, options.QueueDepth));
        Fill(sequential.GetSpan(), options.Pattern, options.Seed);
        Fill(small.GetSpan(), options.Pattern, options.Seed);
        var random = MeasurementThread.CreateRandom(options.Seed);
        var weights = options.BlockSizeTest ? 0.8 : 1.0;

        var writeCurve = await SequentialAsync(handle, sequential.Memory, fileSize, options.CurvePoints, write: true, FilePhase.SequentialWrite, 0, 0.3 * weights, progress, cancellationToken).ConfigureAwait(false);
        var readCurve = await SequentialAsync(handle, sequential.Memory, fileSize, options.CurvePoints, write: false, FilePhase.SequentialRead, 0.3 * weights, 0.3 * weights, progress, cancellationToken).ConfigureAwait(false);

        var duration = options.EffectiveRandomDuration;
        var q1Read = await RandomAsync(handle, small.Memory, fileSize, 1, duration, false, random, cancellationToken).ConfigureAwait(false);
        var q1Write = await RandomAsync(handle, small.Memory, fileSize, 1, duration, true, random, cancellationToken).ConfigureAwait(false);
        progress?.Report(new FileBenchmarkProgress(FilePhase.Random4KSingle, 0.8 * weights));
        var qnRead = await RandomAsync(handle, small.Memory, fileSize, options.QueueDepth, duration, false, random, cancellationToken).ConfigureAwait(false);
        var qnWrite = await RandomAsync(handle, small.Memory, fileSize, options.QueueDepth, duration, true, random, cancellationToken).ConfigureAwait(false);
        progress?.Report(new FileBenchmarkProgress(FilePhase.Random4KMulti, weights));

        var blockRows = options.BlockSizeTest
            ? await BlockSizesAsync(handle, sector, Math.Min(fileSize, options.BlockTestFileSize), options, weights, progress, cancellationToken).ConfigureAwait(false)
            : [];

        FileBenchRow[] rows =
        [
            new(FileTestKind.Sequential, Average(readCurve), 0, Average(writeCurve), 0),
            new(FileTestKind.Random4KSingle, q1Read.MBps, q1Read.Iops, q1Write.MBps, q1Write.Iops),
            new(FileTestKind.Random4KMulti, qnRead.MBps, qnRead.Iops, qnWrite.MBps, qnWrite.Iops),
        ];

        return new FileBenchmarkResult(
            target, fileSize, options.Pattern, options.QueueDepth, writeCurve, readCurve, rows, blockRows,
            Stopwatch.GetElapsedTime(started), DateTimeOffset.Now);
    }

    internal static double Average(IReadOnlyList<TransferSample> curve) =>
        curve.Count == 0 ? 0 : curve.Average(s => s.MegabytesPerSecond);

    private static void Fill(Span<byte> buffer, DataPattern pattern, int? seed)
    {
        if (pattern == DataPattern.Random)
        {
            // Incompressible data so that controllers with compression cannot inflate the result.
            MeasurementThread.CreateRandom(seed).NextBytes(buffer);
        }
        else
        {
            buffer.Clear();
        }
    }

    private static async Task<List<TransferSample>> SequentialAsync(
        SafeFileHandle handle,
        Memory<byte> buffer,
        long fileSize,
        int points,
        bool write,
        FilePhase phase,
        double progressStart,
        double progressWeight,
        IProgress<FileBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var block = buffer.Length;
        var blocks = fileSize / block;
        var perSegment = Math.Max(1, blocks / Math.Max(1, points));
        var curve = new List<TransferSample>();
        long index = 0;
        while (index < blocks)
        {
            var segmentStart = index * block;
            var count = Math.Min(perSegment, blocks - index);
            var t0 = Stopwatch.GetTimestamp();
            for (var i = 0; i < count; i++, index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (write)
                {
                    await System.IO.RandomAccess.WriteAsync(handle, buffer, index * block, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await System.IO.RandomAccess.ReadAsync(handle, buffer, index * block, cancellationToken).ConfigureAwait(false);
                }
            }

            var seconds = Stopwatch.GetElapsedTime(t0).TotalSeconds;
            var sample = new TransferSample((ulong)segmentStart, seconds > 0 ? count * block / seconds / 1e6 : 0);
            curve.Add(sample);
            progress?.Report(new FileBenchmarkProgress(phase, progressStart + (progressWeight * index / blocks), sample, write));
        }

        return curve;
    }

    /// <summary>Random 4 KiB requests with <paramref name="depth"/> workers until the duration ends.</summary>
    private static async Task<(double MBps, double Iops)> RandomAsync(
        SafeFileHandle handle, Memory<byte> buffer, long fileSize, int depth, TimeSpan duration, bool write, Random random, CancellationToken cancellationToken)
    {
        var slots = fileSize / SmallBlock;
        var seeds = Enumerable.Range(0, depth).Select(_ => random.Next()).ToArray();
        long operations = 0;
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var started = Stopwatch.GetTimestamp();

        async Task Worker(int worker)
        {
            var local = new Random(seeds[worker]);
            var slice = buffer.Slice(worker * SmallBlock, SmallBlock);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = local.NextInt64(slots) * SmallBlock;
                if (write)
                {
                    await System.IO.RandomAccess.WriteAsync(handle, slice, offset, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await System.IO.RandomAccess.ReadAsync(handle, slice, offset, cancellationToken).ConfigureAwait(false);
                }

                Interlocked.Increment(ref operations);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, depth).Select(Worker)).ConfigureAwait(false);
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var iops = seconds > 0 ? operations / seconds : 0;
        return (iops * SmallBlock / 1e6, iops);
    }

    private static async Task<IReadOnlyList<BlockSizeRow>> BlockSizesAsync(
        SafeFileHandle handle,
        uint sector,
        long region,
        FileBenchmarkOptions options,
        double progressStart,
        IProgress<FileBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sizes = BlockSweep.Where(s => s >= sector).ToArray();
        var rows = new List<BlockSizeRow>(sizes.Length);
        using var buffer = new AlignedBuffer(sizes[^1]);
        Fill(buffer.GetSpan(), options.Pattern, options.Seed);

        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            var length = Math.Max(size, region / size * size);
            var write = await PassAsync(handle, buffer.Memory[..size], length, write: true, cancellationToken).ConfigureAwait(false);
            var read = await PassAsync(handle, buffer.Memory[..size], length, write: false, cancellationToken).ConfigureAwait(false);
            rows.Add(new BlockSizeRow(size, read, write));
            progress?.Report(new FileBenchmarkProgress(FilePhase.BlockSizes, progressStart + ((1 - progressStart) * (i + 1) / sizes.Length)));
            await Task.Delay(options.EffectiveBlockPause, cancellationToken).ConfigureAwait(false);
        }

        return rows;
    }

    private static async Task<double> PassAsync(SafeFileHandle handle, Memory<byte> buffer, long length, bool write, CancellationToken cancellationToken)
    {
        var t0 = Stopwatch.GetTimestamp();
        for (long offset = 0; offset < length; offset += buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (write)
            {
                await System.IO.RandomAccess.WriteAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await System.IO.RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false);
            }
        }

        var seconds = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        return seconds > 0 ? length / seconds / 1e6 : 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "File benchmark: test file {Path}, {Size} bytes")]
    private partial void LogStart(string path, long size);
}
