using System.Text.RegularExpressions;

namespace DiskScope.Core.Tests;

/// <summary>
/// Source-level checks of SPEC 2 and 9.1: raw writes only in DiskScope.Core.Destructive, unsafe only in Native.
/// </summary>
public partial class ArchitectureTests
{
    private static readonly string CoreSource = System.IO.Path.Combine(FindRepositoryRoot(), "src", "DiskScope.Core");

    [Theory]
    [InlineData("DeviceAccess.Write")]
    [InlineData("TryApplySetting(")]
    [InlineData("IWritableBlockDevice")]
    [InlineData("RandomAccess.Write")]
    public void Write_paths_are_used_only_in_the_destructive_namespace(string token)
    {
        var offenders = SourceFiles()
            .Where(f => System.IO.File.ReadAllText(f).Contains(token, StringComparison.Ordinal))
            .Where(f => !IsIn(f, "Destructive"))
            .Where(f => !IsDefinition(f, token))
            .Select(f => System.IO.Path.GetRelativePath(CoreSource, f))
            .ToArray();

        offenders.ShouldBeEmpty($"'{token}' außerhalb von DiskScope.Core.Destructive");
    }

    [Fact]
    public void Unsafe_code_is_only_in_the_native_folder()
    {
        var offenders = SourceFiles()
            .Where(f => UnsafeKeyword().IsMatch(System.IO.File.ReadAllText(f)))
            .Where(f => !IsIn(f, "Native"))
            .Select(f => System.IO.Path.GetRelativePath(CoreSource, f))
            .ToArray();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Destructive_namespace_matches_its_folder()
    {
        foreach (var file in SourceFiles().Where(f => IsIn(f, "Destructive")))
        {
            System.IO.File.ReadAllText(file).ShouldContain("namespace DiskScope.Core.Destructive;");
        }
    }

    // The FileBenchmark writes a regular test file (no raw device) and is the one allowed exception for RandomAccess.Write.
    private static bool IsDefinition(string file, string token) =>
        (token == "DeviceAccess.Write" && file.EndsWith("DeviceIo.cs", StringComparison.Ordinal))
        || (token == "TryApplySetting(" && file.EndsWith("AtaPassThrough.cs", StringComparison.Ordinal))
        || (token == "RandomAccess.Write" && file.EndsWith("FileBenchmarkRunner.cs", StringComparison.Ordinal));

    private static bool IsIn(string file, string folder) =>
        System.IO.Path.GetRelativePath(CoreSource, file).StartsWith(folder + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static IEnumerable<string> SourceFiles() =>
        System.IO.Directory.EnumerateFiles(CoreSource, "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "DiskScope.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("DiskScope.sln nicht gefunden.");
    }

    [GeneratedRegex(@"\bunsafe\b")]
    private static partial Regex UnsafeKeyword();
}
