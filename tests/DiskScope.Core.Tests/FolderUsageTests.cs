using System.Diagnostics;
using DiskScope.Core.Folders;

namespace DiskScope.Core.Tests;

/// <summary>Folder usage against a small directory tree in %TEMP%.</summary>
public sealed class FolderUsageTests : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskScopeTests", "folders-" + Guid.NewGuid().ToString("N"));

    public FolderUsageTests()
    {
        Write("a.bin", 1000);
        Write(@"big\b.bin", 50_000);
        Write(@"big\c.bin", 20_000);
        Write(@"big\deep\d.bin", 5_000);
        Write(@"small\e.bin", 300);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(_root, "empty"));
    }

    public void Dispose()
    {
        // Remove the junction first so Delete does not walk into its target.
        var junction = System.IO.Path.Combine(_root, "link");
        if (System.IO.Directory.Exists(junction))
        {
            System.IO.Directory.Delete(junction);
        }

        System.IO.Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Sums_sizes_and_files_recursively_and_sorts_by_size()
    {
        var result = await new FolderUsageScanner().ScanAsync(_root, cancellationToken: TestContext.Current.CancellationToken);

        result.TotalBytes.ShouldBe(76_300);
        result.TotalFiles.ShouldBe(5);
        result.AccessErrors.ShouldBe(0);
        result.Root.Children.Select(c => c.Name).ShouldBe(["big", "small", "empty"]);

        var big = result.Root.Children[0];
        big.Size.ShouldBe(75_000);
        big.Files.ShouldBe(3);
        big.OwnSize.ShouldBe(70_000);
        big.Children.Single().Size.ShouldBe(5_000);
        result.Root.Children[2].Size.ShouldBe(0);
    }

    [Fact]
    public async Task Does_not_follow_junctions()
    {
        var junction = System.IO.Path.Combine(_root, "link");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{System.IO.Path.Combine(_root, "big")}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!)
        {
            await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        System.IO.Directory.Exists(junction).ShouldBeTrue();

        var result = await new FolderUsageScanner().ScanAsync(_root, cancellationToken: TestContext.Current.CancellationToken);

        result.TotalBytes.ShouldBe(76_300);
        result.SkippedReparsePoints.ShouldBe(1);
        result.Root.Children.ShouldNotContain(c => c.Name == "link");
    }

    [Fact]
    public async Task Missing_folder_is_rejected() =>
        await Should.ThrowAsync<ArgumentException>(() => new FolderUsageScanner().ScanAsync(System.IO.Path.Combine(_root, "fehlt")));

    private void Write(string relative, int bytes)
    {
        var path = System.IO.Path.Combine(_root, relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
    }
}
