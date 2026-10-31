using System.Diagnostics;
using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Benchmarks;

/// <summary>Random read access with several transfer sizes (SPEC 4.10).</summary>
public sealed partial class RandomAccessRunner(ILogger<RandomAccessRunner>? logger = null)
{
    private const uint Alignment4K = 4096;

    private readonly ILogger _logger = logger ?? NullLogger<RandomAccessRunner>.Instance;

    public async Task<RandomAccessResult> RunAsync(
        DriveInfo drive,
        RandomAccessOptions options,
        IProgress<RandomAccessProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var buffer = MaxTransfer(options);
        var result = await MeasurementThread.Run(
            () =>
            {
                using var device = RawDevice.Open(drive.DevicePath, drive.SizeBytes, drive.LogicalSectorSize, buffer);
                return Run(device, drive.Id, options, progress, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
        LogResult(drive.Id.Number, result.Sizes.Count, result.ReadErrors);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Random access drive {Drive}: {Sizes} sizes measured, {Errors} read errors")]
    private partial void LogResult(int drive, int sizes, int errors);

    internal static int MaxTransfer(RandomAccessOptions options) =>
        options.Sizes.Select(s => s == RandomAccessOptions.Mixed ? RandomAccessOptions.MixedSizes.Max() : s).DefaultIfEmpty(4096).Max();

    internal static RandomAccessResult Run(
        IBlockDevice device,
        DriveId drive,
        RandomAccessOptions options,
        IProgress<RandomAccessProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (options.Sizes.Count == 0)
        {
            throw new ArgumentException("Mindestens eine Transfergröße wählen.");
        }

        var sector = device.SectorSize;
        var alignment = options.Align4K ? Math.Max(Alignment4K, sector) : sector;
        var range = (options.Range ?? DriveRange.Full(device.Size)).AlignTo(sector, device.Size);

        // Sizes below the sector size cannot be read unbuffered (e.g. 512 B on 4Kn drives).
        var sizes = options.Sizes
            .Where(s => s == RandomAccessOptions.Mixed || (s % sector == 0 && (ulong)s <= range.Length))
            .Distinct()
            .ToArray();
        var mixed = RandomAccessOptions.MixedSizes.Where(s => s % sector == 0 && (ulong)s <= range.Length).ToArray();
        if (sizes.Length == 0 || (sizes.Contains(RandomAccessOptions.Mixed) && mixed.Length == 0))
        {
            throw new ArgumentException($"Keine der gewählten Transfergrößen passt zur Sektorgröße {sector} B.");
        }

        var random = MeasurementThread.CreateRandom(options.Seed);
        var started = Stopwatch.GetTimestamp();
        MeasurementThread.WarmUp(device, random, range, alignment);
        var budget = (long)(options.EffectiveDuration.TotalSeconds * Stopwatch.Frequency);
        var errors = 0;
        var results = new List<RandomAccessSizeResult>(sizes.Length);

        for (var index = 0; index < sizes.Length; index++)
        {
            var size = sizes[index];
            var samples = new List<RandomAccessSample>(Math.Min(options.MaxSamplesPerSize, options.MaxOperationsPerSize));
            long operations = 0;
            ulong bytes = 0;
            double totalMs = 0, maxMs = 0;
            var sizeStart = Stopwatch.GetTimestamp();

            while (operations < options.MaxOperationsPerSize && Stopwatch.GetTimestamp() - sizeStart < budget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = size == RandomAccessOptions.Mixed ? mixed[random.Next(mixed.Length)] : size;
                var position = MeasurementThread.RandomOffset(random, range, length, alignment);
                try
                {
                    var t0 = Stopwatch.GetTimestamp();
                    device.Read(position, length);
                    var ms = MeasurementThread.ElapsedMilliseconds(t0, Stopwatch.GetTimestamp());

                    operations++;
                    bytes += (ulong)length;
                    totalMs += ms;
                    maxMs = Math.Max(maxMs, ms);
                    RandomAccessSample? sample = null;
                    if (samples.Count < options.MaxSamplesPerSize)
                    {
                        sample = new RandomAccessSample(position, length, ms);
                        samples.Add(sample.Value);
                    }

                    if (progress is not null && (sample is not null || operations % 64 == 0))
                    {
                        var withinSize = Math.Max(
                            (double)operations / options.MaxOperationsPerSize,
                            (double)(Stopwatch.GetTimestamp() - sizeStart) / budget);
                        progress.Report(new RandomAccessProgress(size, (index + Math.Min(1, withinSize)) / sizes.Length, sample));
                    }
                }
                catch (System.IO.IOException)
                {
                    errors++;
                }
            }

            var seconds = totalMs / 1000.0;
            results.Add(new RandomAccessSizeResult(
                size,
                operations,
                seconds > 0 ? operations / seconds : 0,
                operations > 0 ? totalMs / operations : 0,
                maxMs,
                seconds > 0 ? bytes / seconds / 1e6 : 0,
                samples));
            progress?.Report(new RandomAccessProgress(size, (double)(index + 1) / sizes.Length, null));
        }

        return new RandomAccessResult(
            drive, range, options.Align4K, results, errors, Stopwatch.GetElapsedTime(started), DateTimeOffset.Now);
    }
}
