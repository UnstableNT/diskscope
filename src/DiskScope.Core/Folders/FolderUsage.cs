using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Folders;

/// <summary>A folder with the total size and file count of everything below it.</summary>
public sealed class FolderNode(string name, string fullPath)
{
    public string Name { get; } = name;

    public string FullPath { get; } = fullPath;

    public long Size { get; internal set; }

    public long Files { get; internal set; }

    /// <summary>Files directly in this folder.</summary>
    public long OwnSize { get; internal set; }

    public List<FolderNode> Children { get; } = [];
}

public sealed record FolderScanProgress(long Files, long Bytes, string CurrentPath);

public sealed record FolderUsageResult(
    FolderNode Root, long TotalFiles, long TotalBytes, int AccessErrors, int SkippedReparsePoints, TimeSpan Duration);

/// <summary>
/// Recursive folder sizes of a volume or folder (SPEC 4.6). Reparse points (symlinks, junctions) are not followed,
/// access errors are skipped and counted. Needs no admin rights (but sees more with them).
/// </summary>
public sealed partial class FolderUsageScanner(ILogger<FolderUsageScanner>? logger = null)
{
    private const int ProgressInterval = 2000;

    private readonly ILogger _logger = logger ?? NullLogger<FolderUsageScanner>.Instance;

    public async Task<FolderUsageResult> ScanAsync(
        string path, IProgress<FolderScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!System.IO.Directory.Exists(full))
        {
            throw new ArgumentException($"Der Ordner „{full}“ existiert nicht.");
        }

        var state = new ScanState(progress);
        var started = Stopwatch.GetTimestamp();
        var root = new FolderNode(full, full);

        // Top-level folders in parallel, everything below sequentially per worker.
        var top = ReadDirectory(root, state);
        await Parallel.ForEachAsync(
            top,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount },
            (child, ct) =>
            {
                ScanRecursive(child, state, ct);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        Aggregate(root);
        LogResult(full, root.Files, root.Size, state.AccessErrors);
        return new FolderUsageResult(root, root.Files, root.Size, state.AccessErrors, state.ReparsePoints, Stopwatch.GetElapsedTime(started));
    }

    private static void ScanRecursive(FolderNode node, ScanState state, CancellationToken cancellationToken)
    {
        var stack = new Stack<FolderNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var child in ReadDirectory(stack.Pop(), state))
            {
                stack.Push(child);
            }
        }
    }

    /// <summary>Reads one directory: adds its files to the node and returns its subfolders (already attached).</summary>
    private static List<FolderNode> ReadDirectory(FolderNode node, ScanState state)
    {
        var folders = new List<FolderNode>();
        long size = 0, files = 0;
        using var enumerator = new Enumerator(node.FullPath, state);
        while (enumerator.MoveNext())
        {
            var entry = enumerator.Current;
            if ((entry.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
            {
                Interlocked.Increment(ref state.ReparsePoints);
                continue;
            }

            if (entry.IsDirectory)
            {
                folders.Add(new FolderNode(entry.Name, entry.Path));
            }
            else
            {
                size += entry.Length;
                files++;
            }
        }

        node.OwnSize = size;
        node.Files = files;
        lock (node.Children)
        {
            node.Children.AddRange(folders);
        }

        state.Add(files, size, node.FullPath);
        return folders;
    }

    private static void Aggregate(FolderNode root)
    {
        // Post-order without recursion (deep trees).
        var order = new List<FolderNode>();
        var stack = new Stack<FolderNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            node.Size = node.OwnSize + node.Children.Sum(c => c.Size);
            node.Files += node.Children.Sum(c => c.Files);
            node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder usage {Path}: {Files} files, {Bytes} bytes, {Errors} access errors")]
    private partial void LogResult(string path, long files, long bytes, int errors);

    private readonly record struct Entry(string Name, string Path, bool IsDirectory, long Length, System.IO.FileAttributes Attributes);

    private sealed class ScanState(IProgress<FolderScanProgress>? progress)
    {
        public int AccessErrors;
        public int ReparsePoints;
        private long _files;
        private long _bytes;
        private long _nextReport = ProgressInterval;

        public void Add(long files, long bytes, string path)
        {
            var total = Interlocked.Add(ref _files, files);
            var allBytes = Interlocked.Add(ref _bytes, bytes);
            if (progress is not null && total >= Interlocked.Read(ref _nextReport))
            {
                Interlocked.Exchange(ref _nextReport, total + ProgressInterval);
                progress.Report(new FolderScanProgress(total, allBytes, path));
            }
        }
    }

    /// <summary>Directory enumerator that counts access errors instead of throwing.</summary>
    private sealed class Enumerator(string directory, ScanState state)
        : FileSystemEnumerator<Entry>(directory, new System.IO.EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false })
    {
        protected override Entry TransformEntry(ref FileSystemEntry entry) =>
            new(entry.FileName.ToString(), entry.ToFullPath(), entry.IsDirectory, entry.Length, entry.Attributes);

        protected override bool ContinueOnError(int error)
        {
            Interlocked.Increment(ref state.AccessErrors);
            return true;
        }
    }
}
