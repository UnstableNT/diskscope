using DiskScope.Core.Benchmarks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Destructive;

/// <summary>
/// All raw-device write operations (SPEC 4.4, 4.7, 4.10, 4.11 "schreiben"). Each one needs a
/// <see cref="WriteAuthorization"/> and stays inside its range; the measurements reuse the read engines
/// through <see cref="WriteAdapter"/>, so the same request pattern is written instead of read.
/// </summary>
public sealed partial class DestructiveOperations(ILogger<DestructiveOperations>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<DestructiveOperations>.Instance;

    public Task<BenchmarkResult> WriteBenchmarkAsync(
        WriteAuthorization authorization, BenchmarkOptions options, IProgress<BenchmarkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        BlockSize.Validate(options.BlockSize);
        return RunAsync(authorization, Math.Max(options.BlockSize, 4096), "Benchmark", device =>
            BenchmarkRunner.Run(device, authorization.Drive.Id, options with { Range = authorization.Range }, progress, cancellationToken), cancellationToken);
    }

    public Task<RandomAccessResult> WriteRandomAccessAsync(
        WriteAuthorization authorization, RandomAccessOptions options, IProgress<RandomAccessProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(authorization, RandomAccessRunner.MaxTransfer(options), "Zufallszugriff", device =>
            RandomAccessRunner.Run(device, authorization.Drive.Id, options with { Range = authorization.Range }, progress, cancellationToken), cancellationToken);

    public Task<ExtraTestResult> WriteExtraTestsAsync(
        WriteAuthorization authorization, ExtraTestOptions options, IProgress<ExtraTestProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(authorization, ExtraTestRunner.MaxTransfer, "Zusatztests", device =>
            ExtraTestRunner.Run(device, authorization.Drive.Id, options with { Range = authorization.Range }, progress, cancellationToken), cancellationToken);

    public async Task<EraseResult> EraseAsync(
        WriteAuthorization authorization, EraseOptions options, IProgress<EraseProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        LogStart("Löschen", authorization.Drive.Id.Number, authorization.Range.Start, authorization.Range.End);
        var result = await MeasurementThread.Run(
            () =>
            {
                using var session = DestructiveSession.Open(authorization, Eraser.ChunkSize);
                return Eraser.Run(session.Device, authorization.Drive.Id, authorization.Range, options, progress, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
        LogDone("Löschen", authorization.Drive.Id.Number, result.Errors);
        return result;
    }

    private async Task<T> RunAsync<T>(WriteAuthorization authorization, int buffer, string name, Func<IBlockDevice, T> run, CancellationToken cancellationToken)
    {
        LogStart(name, authorization.Drive.Id.Number, authorization.Range.Start, authorization.Range.End);
        var result = await MeasurementThread.Run(
            () =>
            {
                using var session = DestructiveSession.Open(authorization, buffer);
                return run(new WriteAdapter(session.Device, buffer));
            },
            cancellationToken).ConfigureAwait(false);
        LogDone(name, authorization.Drive.Id.Number, 0);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Destructive {Operation} on drive {Drive}, bytes {Start}-{End}")]
    private partial void LogStart(string operation, int drive, ulong start, ulong end);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Destructive {Operation} on drive {Drive} finished, {Errors} errors")]
    private partial void LogDone(string operation, int drive, int errors);
}

/// <summary>
/// Turns each "read" of a measurement engine into a write of incompressible data at the same position and length.
/// </summary>
internal sealed class WriteAdapter : IBlockDevice
{
    private readonly IWritableBlockDevice _target;
    private readonly byte[] _pattern;

    public WriteAdapter(IWritableBlockDevice target, int maxLength)
    {
        _target = target;
        _pattern = new byte[maxLength];
        Random.Shared.NextBytes(_pattern);
    }

    public ulong Size => _target.Size;

    public uint SectorSize => _target.SectorSize;

    public int Read(ulong offset, int length)
    {
        _target.Write(offset, _pattern.AsSpan(0, length));
        return length;
    }

    public void Dispose()
    {
    }
}
