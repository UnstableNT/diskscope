using System.Diagnostics;
using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Benchmarks;

/// <summary>Read benchmark: transfer rate over the range, access time and burst rate (SPEC 4.4).</summary>
public sealed partial class BenchmarkRunner(ILogger<BenchmarkRunner>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<BenchmarkRunner>.Instance;

    public async Task<BenchmarkResult> RunAsync(
        DriveInfo drive,
        BenchmarkOptions options,
        IProgress<BenchmarkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BlockSize.Validate(options.BlockSize);
        var result = await MeasurementThread.Run(
            () =>
            {
                using var device = RawDevice.Open(
                    drive.DevicePath, drive.SizeBytes, drive.LogicalSectorSize, BufferSize(options, drive.LogicalSectorSize));
                return Run(device, drive.Id, options, progress, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
        LogResult(drive.Id.Number, result.AverageMBps, result.AccessTimeMs, result.BurstMBps, result.ReadErrors);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Benchmark drive {Drive}: avg {Average} MB/s, access {Access} ms, burst {Burst} MB/s, {Errors} read errors")]
    private partial void LogResult(int drive, double? average, double? access, double? burst, int errors);

    internal static int BufferSize(BenchmarkOptions options, uint sectorSize) =>
        Math.Max(options.BlockSize, (int)sectorSize);

    internal static BenchmarkResult Run(
        IBlockDevice device,
        DriveId drive,
        BenchmarkOptions options,
        IProgress<BenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sector = device.SectorSize;
        if (options.BlockSize % sector != 0)
        {
            throw new ArgumentException($"Die Blockgröße muss ein Vielfaches der Sektorgröße ({sector} B) sein.");
        }

        var range = (options.Range ?? DriveRange.Full(device.Size)).AlignTo(sector, device.Size);
        var random = MeasurementThread.CreateRandom(options.Seed);
        var started = Stopwatch.GetTimestamp();
        var cpuStart = CpuTimes.Sample();
        var errors = 0;

        // Progress weights of the phases.
        double transferWeight = options.MeasureTransfer ? 0.8 : 0;
        double accessWeight = options.MeasureAccessTime ? 0.15 : 0;
        double burstWeight = options.MeasureBurst ? 0.05 : 0;
        var weightSum = Math.Max(transferWeight + accessWeight + burstWeight, 1e-9);
        transferWeight /= weightSum;
        accessWeight /= weightSum;

        var transfer = new List<TransferSample>(options.MeasureTransfer ? options.Points : 0);
        if (options.MeasureTransfer)
        {
            var chunk = (ulong)options.BlockSize * (ulong)Math.Max(1, options.BlocksPerPoint);
            chunk = Math.Min(chunk, range.Length / (ulong)options.BlockSize * (ulong)options.BlockSize);
            if (chunk == 0)
            {
                throw new ArgumentException("Der Bereich ist kleiner als ein Block.");
            }

            var points = Math.Max(1, options.Points);
            var span = range.Length - chunk;
            for (var i = 0; i < points; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = range.Start + (points == 1 ? 0 : (ulong)(span * (double)i / (points - 1)) / sector * sector);
                var sample = MeasureChunk(device, position, chunk, options.BlockSize, ref errors);
                if (sample is { } s)
                {
                    transfer.Add(s);
                }

                progress?.Report(new BenchmarkProgress(BenchmarkPhase.Transfer, transferWeight * (i + 1) / points, sample));
            }
        }

        var access = new List<AccessSample>(options.MeasureAccessTime ? options.AccessSamples : 0);
        if (options.MeasureAccessTime)
        {
            if (!options.MeasureTransfer)
            {
                MeasurementThread.WarmUp(device, random, range, sector);
            }

            var samples = Math.Max(1, options.AccessSamples);
            for (var i = 0; i < samples; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = MeasurementThread.RandomOffset(random, range, (int)sector, sector);
                AccessSample? sample = null;
                try
                {
                    var t0 = Stopwatch.GetTimestamp();
                    device.Read(position, (int)sector);
                    var t1 = Stopwatch.GetTimestamp();
                    sample = new AccessSample(position, MeasurementThread.ElapsedMilliseconds(t0, t1));
                    access.Add(sample.Value);
                }
                catch (System.IO.IOException)
                {
                    errors++;
                }

                progress?.Report(new BenchmarkProgress(
                    BenchmarkPhase.AccessTime, transferWeight + (accessWeight * (i + 1) / samples), Access: sample));
            }
        }

        double? burst = null;
        if (options.MeasureBurst)
        {
            cancellationToken.ThrowIfCancellationRequested();
            burst = MeasureBurst(device, range.Start, (int)Math.Min((ulong)options.BlockSize, range.Length), options.BurstRepeats, ref errors);
            progress?.Report(new BenchmarkProgress(BenchmarkPhase.Burst, 1));
        }

        var cpu = CpuTimes.LoadPercent(cpuStart, CpuTimes.Sample());
        var rates = transfer.Select(t => t.MegabytesPerSecond).ToArray();
        return new BenchmarkResult(
            drive,
            options.BlockSize,
            range,
            transfer,
            access,
            rates.Length > 0 ? rates.Min() : null,
            rates.Length > 0 ? rates.Max() : null,
            rates.Length > 0 ? rates.Average() : null,
            access.Count > 0 ? access.Average(a => a.Milliseconds) : null,
            burst,
            cpu,
            errors,
            Stopwatch.GetElapsedTime(started),
            DateTimeOffset.Now);
    }

    private static TransferSample? MeasureChunk(IBlockDevice device, ulong position, ulong chunk, int blockSize, ref int errors)
    {
        try
        {
            var t0 = Stopwatch.GetTimestamp();
            for (ulong offset = 0; offset < chunk; offset += (ulong)blockSize)
            {
                device.Read(position + offset, blockSize);
            }

            var t1 = Stopwatch.GetTimestamp();
            var seconds = MeasurementThread.ElapsedMilliseconds(t0, t1) / 1000.0;
            return new TransferSample(position, seconds <= 0 ? 0 : chunk / seconds / 1e6);
        }
        catch (System.IO.IOException)
        {
            errors++;
            return null;
        }
    }

    /// <summary>
    /// Reads the same block repeatedly; the first read fills the drive cache and is not counted.
    /// The burst rate is the mean of the faster half of the repeats, which filters out scheduling hiccups.
    /// </summary>
    private static double? MeasureBurst(IBlockDevice device, ulong position, int length, int repeats, ref int errors)
    {
        try
        {
            device.Read(position, length);
            var rates = new double[Math.Max(2, repeats)];
            for (var i = 0; i < rates.Length; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                device.Read(position, length);
                var seconds = MeasurementThread.ElapsedMilliseconds(t0, Stopwatch.GetTimestamp()) / 1000.0;
                rates[i] = seconds <= 0 ? 0 : length / seconds / 1e6;
            }

            Array.Sort(rates);
            return rates.Skip(rates.Length / 2).Average();
        }
        catch (System.IO.IOException)
        {
            errors++;
            return null;
        }
    }
}
