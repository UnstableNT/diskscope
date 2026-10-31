using System.Diagnostics;

namespace DiskScope.Core.Benchmarks;

/// <summary>Runs a measurement loop on its own thread with raised priority (SPEC 4.4).</summary>
internal static class MeasurementThread
{
    public static Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (OperationCanceledException ex)
            {
                completion.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "DiskScope measurement",
            Priority = ThreadPriority.Highest,
        };
        thread.Start();
        return completion.Task;
    }

    public static double ElapsedMilliseconds(long start, long end) =>
        (end - start) * 1000.0 / Stopwatch.Frequency;

    public const int WarmUpReads = 32;

    public static Random CreateRandom(int? seed) => seed is { } s ? new Random(s) : new Random();

    /// <summary>
    /// A few uncounted random reads so the drive leaves link and device power-saving states
    /// before latencies are measured. Read errors here are ignored; the measurement counts them.
    /// </summary>
    public static void WarmUp(IBlockDevice device, Random random, DriveRange range, uint alignment)
    {
        var length = (int)Math.Min(alignment, range.Length);
        for (var i = 0; i < WarmUpReads; i++)
        {
            try
            {
                device.Read(RandomOffset(random, range, length, alignment), length);
            }
            catch (System.IO.IOException)
            {
            }
        }
    }

    /// <summary>Random sector-aligned offset so that [offset, offset + length) lies inside the range.</summary>
    public static ulong RandomOffset(Random random, DriveRange range, int length, uint alignment)
    {
        var slots = (range.Length - (ulong)length) / alignment;
        var slot = slots == 0 ? 0 : (ulong)random.NextInt64(0, (long)Math.Min(slots + 1, long.MaxValue));
        return range.Start + (slot * alignment);
    }
}
