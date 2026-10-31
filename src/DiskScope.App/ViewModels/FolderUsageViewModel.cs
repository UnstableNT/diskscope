using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.App.Services;
using DiskScope.Core;
using DiskScope.Core.Folders;

namespace DiskScope.App.ViewModels;

public enum FolderSort { Size, Name, Files }

/// <summary>Tree row; children are created when the row is expanded.</summary>
public sealed partial class FolderItem : ObservableObject
{
    private readonly FolderNode _node;
    private readonly long _total;
    private bool _realized;

    public FolderItem(FolderNode node, long total)
    {
        _node = node;
        _total = total;
        HasUnrealizedChildren = node.Children.Count > 0;
    }

    public string Name => _node.Name;

    public string Size => ByteSize.Format((ulong)_node.Size, CultureInfo.CurrentCulture);

    public double Share => _total == 0 ? 0 : _node.Size * 100.0 / _total;

    public string ShareText => $"{Share:0.0} %";

    public string Files => _node.Files.ToString("N0", CultureInfo.CurrentCulture);

    public ObservableCollection<FolderItem> Children { get; } = [];

    [ObservableProperty]
    public partial bool HasUnrealizedChildren { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public void Realize(FolderSort sort)
    {
        if (_realized)
        {
            return;
        }

        _realized = true;
        foreach (var child in Sorted(_node.Children, sort))
        {
            Children.Add(new FolderItem(child, _total));
        }

        HasUnrealizedChildren = false;
    }

    public void Resort(FolderSort sort)
    {
        if (!_realized)
        {
            return;
        }

        var ordered = Sorted(Children.Select(c => c._node), sort).Select(n => Children.First(c => c._node == n)).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var current = Children.IndexOf(ordered[i]);
            if (current != i)
            {
                Children.Move(current, i);
            }

            ordered[i].Resort(sort);
        }
    }

    internal static IEnumerable<FolderNode> Sorted(IEnumerable<FolderNode> nodes, FolderSort sort) => sort switch
    {
        FolderSort.Name => nodes.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase),
        FolderSort.Files => nodes.OrderByDescending(n => n.Files),
        _ => nodes.OrderByDescending(n => n.Size),
    };
}

/// <summary>Folder usage tab (SPEC 4.6).</summary>
public sealed partial class FolderUsageViewModel : TestViewModelBase
{
    private readonly FolderUsageScanner _scanner;
    private FolderUsageResult? _result;

    public FolderUsageViewModel(FolderUsageScanner scanner, DriveSelection selection)
        : base(selection)
    {
        _scanner = scanner;
        UpdateTargets(selection.Current);
    }

    public ObservableCollection<string> Targets { get; } = [];

    public ObservableCollection<FolderItem> Roots { get; } = [];

    [ObservableProperty]
    public partial string? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public FolderSort Sort => (FolderSort)SortIndex;

    public override string? ResultText
    {
        get
        {
            if (_result is null)
            {
                return null;
            }

            var c = CultureInfo.CurrentCulture;
            var text = new StringBuilder().AppendLine(c, $"Ordnernutzung {_result.Root.FullPath}: {Summary}").AppendLine("Ordner\tGröße\tAnteil\tDateien");
            foreach (var child in _result.Root.Children.Take(50))
            {
                text.AppendLine(c, $"{child.Name}\t{ByteSize.Format((ulong)child.Size, c)}\t{child.Size * 100.0 / Math.Max(1, _result.TotalBytes):0.0} %\t{child.Files:N0}");
            }

            return text.ToString();
        }
    }

    protected override bool RequiresElevation => false;

    protected override bool RequiresDrive => false;

    protected override string StartText => "Scannen";

    public void SetCustomTarget(string path)
    {
        if (!Targets.Contains(path))
        {
            Targets.Add(path);
        }

        SelectedTarget = path;
    }

    protected override async Task RunAsync(Core.Devices.DriveInfo drive, CancellationToken cancellationToken)
    {
        if (SelectedTarget is null)
        {
            throw new ArgumentException("Kein Volume oder Ordner gewählt.");
        }

        Roots.Clear();
        _result = null;
        var progress = new Progress<FolderScanProgress>(p =>
            Summary = $"{p.Files:N0} Dateien, {ByteSize.Format((ulong)p.Bytes, CultureInfo.CurrentCulture)} … {p.CurrentPath}");

        _result = await _scanner.ScanAsync(SelectedTarget, progress, cancellationToken).ConfigureAwait(true);
        var root = new FolderItem(_result.Root, _result.TotalBytes);
        root.Realize(Sort);
        root.IsExpanded = true;
        Roots.Add(root);
        Summary =
            $"{_result.TotalFiles:N0} Dateien, {ByteSize.Format((ulong)_result.TotalBytes, CultureInfo.CurrentCulture)}" +
            (_result.AccessErrors > 0 ? $", {_result.AccessErrors:N0} Zugriffsfehler übersprungen" : string.Empty) +
            (_result.SkippedReparsePoints > 0 ? $", {_result.SkippedReparsePoints:N0} Verknüpfungen nicht verfolgt" : string.Empty);
    }

    protected override void OnDriveChanged(Core.Devices.DriveInfo? drive) => UpdateTargets(drive);

    partial void OnSortIndexChanged(int value)
    {
        foreach (var root in Roots)
        {
            root.Resort(Sort);
        }
    }

    private void UpdateTargets(Core.Devices.DriveInfo? drive)
    {
        Targets.Clear();
        foreach (var volume in drive?.Volumes ?? [])
        {
            if (volume.MountPoint.Length > 0)
            {
                Targets.Add(volume.MountPoint + "\\");
            }
        }

        SelectedTarget = Targets.FirstOrDefault();
    }
}
