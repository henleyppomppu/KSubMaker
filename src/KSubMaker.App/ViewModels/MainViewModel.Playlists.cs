using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KSubMaker.App.Resources;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Media;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// 재생목록: the left pane's list of buckets and the commands that fill and empty them.
///
/// Viewing a playlist is a view like viewing a folder — the grid shows its jobs, 전체 실행 runs
/// them, 목록 새로고침 re-checks them. What differs is removal: in a playlist the list is the user's
/// own collection, so 선택 항목 제거 and 완료 항목 비우기 only take jobs <i>out of the playlist</i>.
/// The jobs stay in their folders with their caches and subtitles.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The playlists in the left pane, oldest first.</summary>
    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = [];

    /// <summary>The playlist being viewed, or null when a folder is.</summary>
    private string? _viewPlaylistId;

    /// <summary>The playlist highlighted in the left pane. Selecting one switches the view to it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenamePlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePlaylistCommand))]
    private PlaylistItemViewModel? _selectedPlaylist;

    partial void OnSelectedPlaylistChanged(PlaylistItemViewModel? value)
    {
        if (value is not null)
        {
            ShowPlaylist(value.Id);
        }
    }

    /// <summary>What the grid is showing, above it: a folder path or a playlist name.</summary>
    [ObservableProperty]
    private string _viewTitle = string.Empty;

    /// <summary>
    /// The label of 선택 항목 제거, which in a playlist means 재생목록에서 빼기 — the user should
    /// read which of the two a click does before making it.
    /// </summary>
    public string RemoveSelectedCaption =>
        _viewPlaylistId is null ? Strings.RemoveSelectedButton : Strings.RemoveFromPlaylistButton;

    public bool IsPlaylistView => _viewPlaylistId is not null;

    private bool CanEditPlaylist => SelectedPlaylist is not null;

    private void ShowPlaylist(string playlistId)
    {
        if (string.Equals(_viewPlaylistId, playlistId, StringComparison.Ordinal))
        {
            return;
        }

        _viewPlaylistId = playlistId;
        FolderTree.ClearSelection();
        OnViewChanged();
        ApplyView();
    }

    /// <summary>Announces everything that depends on which kind of view is showing.</summary>
    private void OnViewChanged()
    {
        ViewTitle = _viewPlaylistId is { } id
            ? string.Format(
                CultureInfo.CurrentCulture,
                Strings.ViewTitlePlaylistFormat,
                Playlists.FirstOrDefault(p => p.Id == id)?.Name ?? string.Empty)
            : string.Format(CultureInfo.CurrentCulture, Strings.ViewTitleFolderFormat, _listFolder ?? string.Empty);

        OnPropertyChanged(nameof(RemoveSelectedCaption));
        OnPropertyChanged(nameof(IsPlaylistView));
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e) =>
        _ = _dispatcher.InvokeAsync(SyncPlaylists, DispatcherPriority.Background);

    /// <summary>
    /// Brings the left pane in line with <see cref="PlaylistService"/>, updating items in place so
    /// the list keeps its selection. Leaves a deleted playlist's view for the folder.
    /// </summary>
    private void SyncPlaylists()
    {
        var current = _playlists.Playlists;
        var byId = Playlists.ToDictionary(p => p.Id, StringComparer.Ordinal);

        for (var i = Playlists.Count - 1; i >= 0; i--)
        {
            if (current.All(p => p.Id != Playlists[i].Id))
            {
                Playlists.RemoveAt(i);
            }
        }

        foreach (var playlist in current)
        {
            if (!byId.TryGetValue(playlist.Id, out var item))
            {
                item = new PlaylistItemViewModel(playlist.Id);
                Playlists.Add(item);
            }

            item.Name = playlist.Name;
            item.Count = _playlists.Count(playlist.Id);
        }

        if (_viewPlaylistId is { } viewed)
        {
            if (current.All(p => p.Id != viewed))
            {
                _viewPlaylistId = null;
                SelectedPlaylist = null;
                _ = FolderTree.RevealAsync(_listFolder);
            }

            // Membership (or the name) changed: rows may have joined or left the playlist on screen.
            OnViewChanged();
            ApplyView();
        }
    }

    // -----------------------------------------------------------------------
    // Commands: the playlists themselves
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task NewPlaylistAsync()
    {
        if (await CreatePlaylistAsync().ConfigureAwait(true) is { } created)
        {
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == created);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditPlaylist))]
    private async Task RenamePlaylistAsync()
    {
        if (SelectedPlaylist is not { } item)
        {
            return;
        }

        var name = AskPlaylistName(item.Name, item.Id);
        if (name is null)
        {
            return;
        }

        await _playlists.RenameAsync(item.Id, name).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanEditPlaylist))]
    private async Task DeletePlaylistAsync()
    {
        if (SelectedPlaylist is not { } item)
        {
            return;
        }

        var question = string.Format(CultureInfo.CurrentCulture, Strings.DeletePlaylistConfirmFormat, item.Name);
        if (!_dialogs.Confirm(question, Strings.DeletePlaylistButton))
        {
            return;
        }

        await _playlists.DeleteAsync(item.Id).ConfigureAwait(true);
    }

    // -----------------------------------------------------------------------
    // Commands: filling a playlist from the grid
    // -----------------------------------------------------------------------

    /// <summary>재생목록에 넣기 ▸ (playlist): the checked rows, or the highlighted one.</summary>
    [RelayCommand]
    private async Task AddToPlaylistAsync(PlaylistItemViewModel? target)
    {
        if (target is null)
        {
            return;
        }

        var ids = TargetIds();
        if (ids.Count == 0)
        {
            _dialogs.ShowWarning(Strings.NoSelectionMessage);
            return;
        }

        var added = await _playlists.AddAsync(target.Id, ids).ConfigureAwait(true);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.AddedToPlaylistFormat, target.Name, added);
        if (ids.Count > added)
        {
            StatusMessage += string.Format(CultureInfo.CurrentCulture, Strings.AlreadyInPlaylistFormat, ids.Count - added);
        }
    }

    /// <summary>새 재생목록에 넣기…: names a playlist and puts the checked (or highlighted) rows in it.</summary>
    [RelayCommand]
    private async Task AddToNewPlaylistAsync()
    {
        var ids = TargetIds();
        if (ids.Count == 0)
        {
            _dialogs.ShowWarning(Strings.NoSelectionMessage);
            return;
        }

        if (await CreatePlaylistAsync().ConfigureAwait(true) is not { } created)
        {
            return;
        }

        var added = await _playlists.AddAsync(created, ids).ConfigureAwait(true);
        var name = _playlists.Playlists.FirstOrDefault(p => p.Id == created)?.Name ?? string.Empty;
        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.AddedToPlaylistFormat, name, added);
    }

    /// <summary>Asks for a name and creates the playlist. Null when the user backed out.</summary>
    private async Task<string?> CreatePlaylistAsync()
    {
        var name = AskPlaylistName(string.Empty, exceptId: null);
        if (name is null)
        {
            return null;
        }

        var playlist = await _playlists.CreateAsync(name).ConfigureAwait(true);
        SyncPlaylists();
        return playlist.Id;
    }

    /// <summary>Prompts until the name is non-empty and unused. Null when cancelled.</summary>
    private string? AskPlaylistName(string initial, string? exceptId)
    {
        var name = initial;
        while (true)
        {
            name = _dialogs.PromptText(Strings.PlaylistNameDialogTitle, Strings.PlaylistNameDialogMessage, name);
            if (name is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!_playlists.NameExists(name, exceptId))
            {
                return name.Trim();
            }

            _dialogs.ShowWarning(
                string.Format(CultureInfo.CurrentCulture, Strings.PlaylistNameTakenFormat, name.Trim()),
                Strings.PlaylistNameDialogTitle);
        }
    }

    /// <summary>The checked rows, else the highlighted one.</summary>
    private List<string> TargetIds()
    {
        var ids = CheckedIds();
        if (ids.Count == 0 && SelectedJob is { } highlighted)
        {
            ids.Add(highlighted.Id);
        }

        return ids;
    }

    // -----------------------------------------------------------------------
    // Playlist view: removal, refresh, drop
    // -----------------------------------------------------------------------

    /// <summary>
    /// 선택 항목 제거 / 완료 항목 비우기 in a playlist: out of the playlist only. Returns false when
    /// the view is a folder, so the caller does the queue removal it always did.
    /// </summary>
    private async Task<bool> TryRemoveFromPlaylistAsync(IReadOnlyList<string> ids, bool confirm)
    {
        if (_viewPlaylistId is not { } playlistId)
        {
            return false;
        }

        if (ids.Count == 0)
        {
            return true;
        }

        if (confirm && !_dialogs.Confirm(
                string.Format(CultureInfo.CurrentCulture, Strings.RemoveFromPlaylistConfirmFormat, ids.Count),
                Strings.RemoveFromPlaylistButton))
        {
            return true;
        }

        var removed = await _playlists.RemoveAsync(playlistId, ids).ConfigureAwait(true);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.RemovedFromPlaylistFormat, removed);
        return true;
    }

    /// <summary>
    /// 목록 새로고침 on a playlist: re-checks each of its videos where it lives — subtitles that have
    /// appeared mark the job 완료, videos that are gone leave the queue. The playlist's jobs can come
    /// from many folders, so each folder is scanned on its own, without subfolders.
    /// </summary>
    private async Task RefreshPlaylistAsync(string playlistId, CancellationToken cancellationToken)
    {
        var members = _queue.Jobs.Where(j => _playlists.Contains(playlistId, j.Id)).ToArray();
        var wanted = new HashSet<string>(members.Select(j => j.VideoPath), StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        var gone = QueueListReconciler.DropOnRefresh(members, File.Exists);
        if (gone.Count > 0)
        {
            var result = await _queue.RemoveAsync(gone.Select(j => j.Id), cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            RemoveRows(result.Removed);
            removed = result.Removed.Count;
        }

        var found = new List<VideoFile>();
        foreach (var folder in members
                     .Select(j => Path.GetDirectoryName(j.VideoPath))
                     .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var report = await _scanService.ScanAsync(
                new ScanRequest { RootFolder = folder!, IncludeSubfolders = false, IncludeHiddenFolders = true },
                cancellationToken).ConfigureAwait(true);

            found.AddRange(report.Files.Where(f => wanted.Contains(f.FullPath)));
        }

        var settings = _settingsService.Current;
        _settings = settings;
        var results = await _queue.EnqueueAsync(found, settings, cancellationToken).ConfigureAwait(true);
        ShowEnqueueSummary(results, removed);
    }

    /// <summary>Files dropped while a playlist is on screen go into that playlist.</summary>
    private async Task AddDroppedToPlaylistAsync(IReadOnlyList<EnqueueResult> results)
    {
        if (_viewPlaylistId is not { } playlistId)
        {
            return;
        }

        var ids = results.Where(r => r.Job is not null).Select(r => r.Job!.Id).ToArray();
        try
        {
            await _playlists.AddAsync(playlistId, ids).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "끌어다 놓은 항목을 재생목록에 넣지 못했습니다.");
        }
    }
}

/// <summary>One playlist in the left pane.</summary>
public sealed partial class PlaylistItemViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _count;
}
