namespace DiskScope.Core.Benchmarks;

public enum DataPattern { Zero, Random }

/// <summary>Options of the file benchmark (SPEC 4.8).</summary>
/// <param name="Target">Directory on the volume to test, e.g. "D:\".</param>
/// <param name="FileSize">Test file size, default 500 MiB.</param>
/// <param name="QueueDepth">Outstanding requests for the multi-request test, default 32.</param>
/// <param name="RandomDuration">Duration of each random 4 KiB test.</param>
/// <param name="BlockSizeTest">Also measure throughput per block size (0.5 KiB – 8 MiB).</param>
/// <param name="BlockTestFileSize">Bytes written and read per block size.</param>
/// <param name="BlockPause">Pause between block size measurements.</param>
public sealed record FileBenchmarkOptions(
    string Target,
    long FileSize = 500L * 1024 * 1024,
    DataPattern Pattern = DataPattern.Random,
    int QueueDepth = 32,
    TimeSpan? RandomDuration = null,
    bool BlockSizeTest = false,
    long BlockTestFileSize = 64L * 1024 * 1024,
    TimeSpan? BlockPause = null,
    int SequentialBlockSize = 1024 * 1024,
    int CurvePoints = 100,
    int? Seed = null)
{
    public TimeSpan EffectiveRandomDuration => RandomDuration ?? TimeSpan.FromSeconds(3);

    public TimeSpan EffectiveBlockPause => BlockPause ?? TimeSpan.FromMilliseconds(500);
}

public enum FileTestKind { Sequential, Random4KSingle, Random4KMulti }

public enum FilePhase { SequentialWrite, SequentialRead, Random4KSingle, Random4KMulti, BlockSizes }

/// <summary>One row of the result table: read and write for one access pattern.</summary>
public sealed record FileBenchRow(FileTestKind Kind, double ReadMBps, double ReadIops, double WriteMBps, double WriteIops);

public sealed record BlockSizeRow(int BlockSize, double ReadMBps, double WriteMBps);

public sealed record FileBenchmarkProgress(FilePhase Phase, double Fraction, TransferSample? Sample = null, bool IsWrite = false);

/// <summary>File benchmark result. Curve positions are file offsets in bytes.</summary>
public sealed record FileBenchmarkResult(
    string Target,
    long FileSize,
    DataPattern Pattern,
    int QueueDepth,
    IReadOnlyList<TransferSample> WriteCurve,
    IReadOnlyList<TransferSample> ReadCurve,
    IReadOnlyList<FileBenchRow> Rows,
    IReadOnlyList<BlockSizeRow> BlockSizes,
    TimeSpan Duration,
    DateTimeOffset Timestamp);
