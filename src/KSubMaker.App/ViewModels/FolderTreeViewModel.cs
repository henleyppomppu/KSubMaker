using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// The explorer-style folder tree beside the job list. Drives at the top, children loaded the first
/// time a node is expanded — on the thread pool, because a mapped network drive (the real library is
/// on Z:) can take seconds to list or to answer IsReady.
///
/// Selecting a node is reported through <see cref="FolderSelected"/>. <see cref="RevealAsync"/>
/// expands down to a path and selects it without raising that event, so the tree can follow a
/// folder chosen elsewhere (텍스트 상자, 폴더 선택 dialog, the folder restored at startup).
/// </summary>
public sealed class FolderTreeViewModel
{
    private bool _revealing;
    private FolderNodeViewModel? _selected;

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    /// <summary>Raised when the user selects a folder in the tree.</summary>
    public event EventHandler<string>? FolderSelected;

    /// <summary>Fills <see cref="Roots"/> with the ready drives. Safe to call once at startup.</summary>
    public async Task LoadDrivesAsync()
    {
        // VolumeLabel is read here, off the UI thread, for the same reason IsReady is: a network
        // drive can take its time answering either.
        var drives = await Task.Run(() =>
            DriveInfo.GetDrives()
                .Where(IsUsable)
                .Select(d => (Path: d.RootDirectory.FullName, Name: DriveName(d), Kind: DriveKind(d)))
                .ToArray()).ConfigureAwait(true);

        Roots.Clear();
        foreach (var drive in drives)
        {
            Roots.Add(new FolderNodeViewModel(this, drive.Path, drive.Name, drive.Kind));
        }
    }

    /// <summary>
    /// Expands the tree down to <paramref name="path"/> and selects the deepest node found, quietly.
    /// A path the tree cannot reach (a UNC share, a folder since deleted) stops at the last match.
    /// </summary>
    public async Task RevealAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var target = path.Trim().TrimEnd('\\', '/');
        var level = (IEnumerable<FolderNodeViewModel>)Roots;
        FolderNodeViewModel? found = null;

        while (true)
        {
            var next = level.FirstOrDefault(n => IsSameOrAncestor(n.FullPath, target));
            if (next is null)
            {
                break;
            }

            found = next;
            if (string.Equals(next.FullPath.TrimEnd('\\', '/'), target, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await next.EnsureChildrenAsync().ConfigureAwait(true);
            next.IsExpanded = true;
            level = next.Children;
        }

        if (found is null)
        {
            return;
        }

        _revealing = true;
        try
        {
            found.IsSelected = true;
        }
        finally
        {
            _revealing = false;
        }
    }

    /// <summary>
    /// Deselects the current folder (a playlist is now on screen), so picking that same folder
    /// again is a selection change and switches back.
    /// </summary>
    public void ClearSelection()
    {
        if (_selected is { } node)
        {
            _selected = null;
            node.IsSelected = false;
        }
    }

    /// <summary>
    /// Re-reads every folder the tree has already listed, keeping what is expanded and selected:
    /// folders made since appear, deleted ones go. Collapsed folders just forget their children and
    /// list them again when next opened.
    /// </summary>
    public async Task RefreshAsync()
    {
        foreach (var root in Roots.ToArray())
        {
            await root.RefreshAsync().ConfigureAwait(true);
        }
    }

    internal void OnNodeSelected(FolderNodeViewModel node)
    {
        _selected = node;
        if (!_revealing)
        {
            FolderSelected?.Invoke(this, node.FullPath);
        }
    }

    private static bool IsSameOrAncestor(string node, string target)
    {
        var root = node.TrimEnd('\\', '/');
        return string.Equals(root, target, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"볼륨 이름 (Z:)", as Explorer shows it; just "(Z:)" when the label cannot be read.</summary>
    private static string DriveName(DriveInfo drive)
    {
        var letter = drive.Name.TrimEnd('\\', '/');
        try
        {
            var label = drive.VolumeLabel;
            if (!string.IsNullOrWhiteSpace(label))
            {
                return $"{label} ({letter})";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return drive.DriveType == DriveType.Fixed ? $"로컬 디스크 ({letter})" : $"({letter})";
    }

    private static FolderNodeKind DriveKind(DriveInfo drive) =>
        drive.DriveType == DriveType.Network ? FolderNodeKind.NetworkDrive : FolderNodeKind.Drive;

    private static bool IsUsable(DriveInfo drive)
    {
        try
        {
            return drive.IsReady && drive.DriveType is not DriveType.CDRom;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>One folder in <see cref="FolderTreeViewModel"/>. Children load on first expand.</summary>
public sealed partial class FolderNodeViewModel : ObservableObject
{
    /// <summary>Stands in for unloaded children so the expander arrow shows before anything is read.</summary>
    private static readonly FolderNodeViewModel Placeholder = new();

    private readonly FolderTreeViewModel? _tree;
    private Task? _loading;

    private FolderNodeViewModel()
    {
        FullPath = string.Empty;
        Name = string.Empty;
    }

    public FolderNodeViewModel(FolderTreeViewModel tree, string fullPath, string name, FolderNodeKind kind = FolderNodeKind.Folder)
    {
        _tree = tree;
        FullPath = fullPath;
        Name = name;
        Kind = kind;
        Children.Add(Placeholder);
    }

    public string FullPath { get; }

    public string Name { get; }

    public FolderNodeKind Kind { get; }

    /// <summary>Segoe MDL2 Assets glyph for the row's icon.</summary>
    public string Glyph => Kind switch
    {
        FolderNodeKind.Drive => "\uEDA2",
        FolderNodeKind.NetworkDrive => "\uE8CE",
        _ => "\uE8B7"
    };

    public bool IsDrive => Kind != FolderNodeKind.Folder;

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = EnsureChildrenAsync();
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _tree?.OnNodeSelected(this);
        }
    }

    /// <summary>Lists the subfolders once. Hidden and system folders are left out, as in Explorer.</summary>
    public Task EnsureChildrenAsync() => _loading ??= LoadChildrenAsync();

    /// <summary>See <see cref="FolderTreeViewModel.RefreshAsync"/>.</summary>
    internal async Task RefreshAsync()
    {
        if (_loading is null)
        {
            return;
        }

        if (!IsExpanded)
        {
            // Listed before but closed now: drop the stale list, read it again on the next open.
            _loading = null;
            Children.Clear();
            Children.Add(Placeholder);
            return;
        }

        var paths = await Task.Run(() => ListSubfolders(FullPath)).ConfigureAwait(true);
        var keep = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Children[i].FullPath))
            {
                Children.RemoveAt(i);
            }
        }

        // Existing nodes are kept (with their own expansion); new folders are inserted in order.
        for (var i = 0; i < paths.Length; i++)
        {
            if (i < Children.Count && string.Equals(Children[i].FullPath, paths[i], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existing = Children.FirstOrDefault(c => string.Equals(c.FullPath, paths[i], StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Children.Remove(existing);
                Children.Insert(i, existing);
            }
            else
            {
                Children.Insert(i, new FolderNodeViewModel(_tree!, paths[i], Path.GetFileName(paths[i])));
            }
        }

        foreach (var child in Children.ToArray())
        {
            await child.RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadChildrenAsync()
    {
        var names = await Task.Run(() => ListSubfolders(FullPath)).ConfigureAwait(true);

        Children.Clear();
        foreach (var path in names)
        {
            Children.Add(new FolderNodeViewModel(_tree!, path, Path.GetFileName(path)));
        }
    }

    private static string[] ListSubfolders(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(d => d.FullName)
                .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A folder we may not read shows as having no subfolders, the way Explorer shows it empty.
            return [];
        }
    }
}

/// <summary>What a tree row stands for; picks its icon.</summary>
public enum FolderNodeKind
{
    Folder,
    Drive,
    NetworkDrive
}
