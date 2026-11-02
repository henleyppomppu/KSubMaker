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

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    /// <summary>Raised when the user selects a folder in the tree.</summary>
    public event EventHandler<string>? FolderSelected;

    /// <summary>Fills <see cref="Roots"/> with the ready drives. Safe to call once at startup.</summary>
    public async Task LoadDrivesAsync()
    {
        var drives = await Task.Run(() =>
            DriveInfo.GetDrives()
                .Where(IsUsable)
                .Select(d => d.RootDirectory.FullName)
                .ToArray()).ConfigureAwait(true);

        Roots.Clear();
        foreach (var drive in drives)
        {
            Roots.Add(new FolderNodeViewModel(this, drive, drive.TrimEnd('\\', '/')));
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

    internal void OnNodeSelected(FolderNodeViewModel node)
    {
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

    public FolderNodeViewModel(FolderTreeViewModel tree, string fullPath, string name)
    {
        _tree = tree;
        FullPath = fullPath;
        Name = name;
        Children.Add(Placeholder);
    }

    public string FullPath { get; }

    public string Name { get; }

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
