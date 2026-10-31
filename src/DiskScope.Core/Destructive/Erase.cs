using System.Diagnostics;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;
using DiskScope.Core.Scanning;

namespace DiskScope.Core.Destructive;

public enum ErasePattern { Zero, Random, Fixed }

public enum EraseBlockState : byte { Open, Written, Error }

/// <summary>Options of "Löschen" (SPEC 4.7).</summary>
/// <param name="FixedByte">Fill byte for <see cref="ErasePattern.Fixed"/>.</param>
/// <param name="Passes">Number of passes, 1–n.</param>
/// <param name="Verify">Read back every chunk after writing it ("Prüflesen").</param>
public sealed record EraseOptions(
    ErasePattern Pattern = ErasePattern.Zero,
    byte FixedByte = 0,
    int Passes = 1,
    bool Verify = false,
    int BlockCount = 2000,
    int? Seed = null);

public sealed record EraseProgress(
    int Pass, int Passes, int Block, EraseBlockState State, double Fraction, double MegabytesPerSecond, ulong Position, int Errors);

public sealed record EraseResult(
    DriveId Drive,
    DriveRange Range,
    ErasePattern Pattern,
    int Passes,
    bool Verified,
    IReadOnlyList<EraseBlockState> Blocks,
    int Errors,
    ulong BytesWritten,
    TimeSpan Duration,
    DateTimeOffset Timestamp);

/// <summary>Overwrites a range block by block with a pattern, optionally verifying each chunk.</summary>
internal static class Eraser
{
    internal const int ChunkSize = 1024 * 1024;

    public static EraseResult Run(
        IWritableBlockDevice device, DriveId drive, DriveRange range, EraseOptions options, IProgress<EraseProgress>? progress, CancellationToken cancellationToken)
    {
        if (options.Passes < 1)
        {
            throw new ArgumentException("Mindestens ein Durchgang.");
        }

        var sector = device.SectorSize;
        var count = ErrorScanner.EffectiveBlockCount(range, options.BlockCount, sector);
        var chunk = Math.Max((int)sector, ChunkSize / (int)sector * (int)sector);
        var data = new byte[chunk];
        var states = new EraseBlockState[count];
        var errors = 0;
        ulong written = 0;
        var started = Stopwatch.GetTimestamp();
        var totalBytes = (double)range.Length * options.Passes;

        for (var pass = 0; pass < options.Passes; pass++)
        {
            Array.Clear(states);
            var random = options.Pattern == ErasePattern.Random ? MeasurementThread.CreateRandom(options.Seed is { } s ? s + pass : null) : null;
            if (options.Pattern == ErasePattern.Fixed)
            {
                data.AsSpan().Fill(options.FixedByte);
            }
            else if (options.Pattern == ErasePattern.Zero)
            {
                Array.Clear(data);
            }

            for (var block = 0; block < count; block++)
            {
                var start = ErrorScanner.BlockBoundary(range, count, block, sector);
                var end = ErrorScanner.BlockBoundary(range, count, block + 1, sector);
                var ok = true;
                var t0 = Stopwatch.GetTimestamp();
                for (var offset = start; offset < end; offset += (ulong)chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = (int)Math.Min((ulong)chunk, end - offset);
                    var slice = data.AsSpan(0, length);
                    random?.NextBytes(slice);
                    try
                    {
                        device.Write(offset, slice);
                        written += (ulong)length;
                        if (options.Verify && !device.ReadBack(offset, length).SequenceEqual(slice))
                        {
                            ok = false;
                            errors++;
                        }
                    }
                    catch (System.IO.IOException)
                    {
                        ok = false;
                        errors++;
                    }
                }

                states[block] = ok ? EraseBlockState.Written : EraseBlockState.Error;
                var seconds = Stopwatch.GetElapsedTime(t0).TotalSeconds;
                var done = ((double)range.Length * pass) + (end - range.Start);
                progress?.Report(new EraseProgress(
                    pass + 1, options.Passes, block, states[block], done / totalBytes, seconds > 0 ? (end - start) / seconds / 1e6 : 0, start, errors));
            }
        }

        return new EraseResult(
            drive, range, options.Pattern, options.Passes, options.Verify, states, errors, written,
            Stopwatch.GetElapsedTime(started), DateTimeOffset.Now);
    }
}
