using DiskScope.Core.Benchmarks;

namespace DiskScope.Integration.Tests;

/// <summary>File benchmark on the temp volume; needs no admin rights (SPEC 4.8).</summary>
[Trait("Category", "Hardware")]
public sealed class FileBenchmarkTests : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskScopeTests", Guid.NewGuid().ToString("N"));

    public FileBenchmarkTests()
    {
        System.IO.Directory.CreateDirectory(_directory);
    }

    public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Measures_all_rows_and_deletes_the_test_file()
    {
        var options = new FileBenchmarkOptions(
            _directory, FileSize: 16 * 1024 * 1024, QueueDepth: 4, RandomDuration: TimeSpan.FromMilliseconds(300), CurvePoints: 8);

        var result = await new FileBenchmarkRunner().RunAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        result.WriteCurve.Count.ShouldBe(8);
        result.ReadCurve.Count.ShouldBe(8);
        result.Rows.Select(r => r.Kind).ShouldBe([FileTestKind.Sequential, FileTestKind.Random4KSingle, FileTestKind.Random4KMulti]);
        result.Rows.ShouldAllBe(r => r.ReadMBps > 0 && r.WriteMBps > 0);
        result.BlockSizes.ShouldBeEmpty();
        System.IO.Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Block_size_sweep_skips_sizes_below_the_sector_size()
    {
        var options = new FileBenchmarkOptions(
            _directory, FileSize: 16 * 1024 * 1024, QueueDepth: 2, RandomDuration: TimeSpan.FromMilliseconds(100),
            BlockSizeTest: true, BlockTestFileSize: 8 * 1024 * 1024, BlockPause: TimeSpan.Zero);

        var result = await new FileBenchmarkRunner().RunAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        result.BlockSizes[^1].BlockSize.ShouldBe(8 * 1024 * 1024);
        result.BlockSizes.ShouldAllBe(b => b.ReadMBps > 0 && b.WriteMBps > 0);
    }

    [Fact]
    public async Task Cancellation_still_deletes_the_test_file()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var options = new FileBenchmarkOptions(_directory, FileSize: 64 * 1024 * 1024, RandomDuration: TimeSpan.FromSeconds(10));

        await Should.ThrowAsync<OperationCanceledException>(() => new FileBenchmarkRunner().RunAsync(options, cancellationToken: cts.Token));

        System.IO.Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Rejects_missing_target() =>
        await Should.ThrowAsync<ArgumentException>(() => new FileBenchmarkRunner().RunAsync(new FileBenchmarkOptions(System.IO.Path.Combine(_directory, "fehlt"))));
}
