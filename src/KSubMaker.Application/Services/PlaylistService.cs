using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Playlists;

namespace KSubMaker.Application.Services;

/// <summary>
/// 재생목록: named buckets of jobs collected from any folders. The folder view shows what is in a
/// folder; a playlist shows what the user put together, and 전체 실행 on it runs exactly that.
///
/// Membership is a reference. Taking a job out of a playlist, or deleting the playlist, leaves the
/// job, its resume cache and its subtitle alone. Removing a job from the queue removes it from every
/// playlist (the database cascades; <see cref="ForgetJobs"/> keeps memory in step).
///
/// Thread-safe: the UI edits playlists while the queue pump, running a playlist, asks
/// <see cref="Contains"/> for every job it considers.
/// </summary>
public sealed class PlaylistService(IPlaylistRepository repository, TimeProvider? timeProvider = null)
{
    private readonly IPlaylistRepository _repository = repository;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly List<Playlist> _playlists = [];
    private readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.Ordinal);

    /// <summary>Raised after any change to the playlists or their contents.</summary>
    public event EventHandler? Changed;

    /// <summary>Every playlist, oldest first.</summary>
    public IReadOnlyList<Playlist> Playlists
    {
        get
        {
            lock (_gate)
            {
                return _playlists.ToArray();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var playlists = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var entries = await _repository.GetEntriesAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _playlists.Clear();
            _playlists.AddRange(playlists.OrderBy(p => p.CreatedAtUtc));
            _members.Clear();

            foreach (var playlist in _playlists)
            {
                _members[playlist.Id] = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (var entry in entries)
            {
                if (_members.TryGetValue(entry.PlaylistId, out var members))
                {
                    members.Add(entry.JobId);
                }
            }
        }

        RaiseChanged();
    }

    /// <summary>Whether a playlist named <paramref name="name"/> already exists (case-insensitive).</summary>
    public bool NameExists(string name, string? exceptId = null)
    {
        var trimmed = name.Trim();
        lock (_gate)
        {
            return _playlists.Any(p =>
                !string.Equals(p.Id, exceptId, StringComparison.Ordinal)
                && string.Equals(p.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
        }
    }

    public async Task<Playlist> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var playlist = new Playlist
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        };

        await _repository.AddAsync(playlist, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _playlists.Add(playlist);
            _members[playlist.Id] = new HashSet<string>(StringComparer.Ordinal);
        }

        RaiseChanged();
        return playlist;
    }

    public async Task RenameAsync(string playlistId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();
        await _repository.RenameAsync(playlistId, trimmed, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            var playlist = _playlists.FirstOrDefault(p => p.Id == playlistId);
            if (playlist is not null)
            {
                playlist.Name = trimmed;
            }
        }

        RaiseChanged();
    }

    /// <summary>Deletes the playlist. Its jobs stay in the queue.</summary>
    public async Task DeleteAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        await _repository.RemoveAsync(playlistId, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _playlists.RemoveAll(p => p.Id == playlistId);
            _members.Remove(playlistId);
        }

        RaiseChanged();
    }

    /// <summary>Adds jobs to a playlist. Returns how many were not in it already.</summary>
    public async Task<int> AddAsync(string playlistId, IEnumerable<string> jobIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        string[] fresh;
        lock (_gate)
        {
            if (!_members.TryGetValue(playlistId, out var members))
            {
                return 0;
            }

            fresh = jobIds.Distinct(StringComparer.Ordinal).Where(id => !members.Contains(id)).ToArray();
        }

        if (fresh.Length == 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await _repository.AddEntriesAsync(
            fresh.Select(id => new PlaylistEntry { PlaylistId = playlistId, JobId = id, AddedAtUtc = now }).ToArray(),
            cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_members.TryGetValue(playlistId, out var members))
            {
                members.UnionWith(fresh);
            }
        }

        RaiseChanged();
        return fresh.Length;
    }

    /// <summary>Takes jobs out of a playlist. The jobs themselves are untouched. Returns how many left it.</summary>
    public async Task<int> RemoveAsync(string playlistId, IEnumerable<string> jobIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        string[] present;
        lock (_gate)
        {
            if (!_members.TryGetValue(playlistId, out var members))
            {
                return 0;
            }

            present = jobIds.Distinct(StringComparer.Ordinal).Where(members.Contains).ToArray();
        }

        if (present.Length == 0)
        {
            return 0;
        }

        await _repository.RemoveEntriesAsync(playlistId, present, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_members.TryGetValue(playlistId, out var members))
            {
                members.ExceptWith(present);
            }
        }

        RaiseChanged();
        return present.Length;
    }

    /// <summary>
    /// Drops jobs that have left the queue from every playlist, in memory. The database has already
    /// done the same through its cascade.
    /// </summary>
    public void ForgetJobs(IEnumerable<string> jobIds)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        var changed = false;
        lock (_gate)
        {
            var ids = jobIds.ToArray();
            foreach (var members in _members.Values)
            {
                var before = members.Count;
                members.ExceptWith(ids);
                changed |= members.Count != before;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public bool Contains(string playlistId, string jobId)
    {
        lock (_gate)
        {
            return _members.TryGetValue(playlistId, out var members) && members.Contains(jobId);
        }
    }

    public int Count(string playlistId)
    {
        lock (_gate)
        {
            return _members.TryGetValue(playlistId, out var members) ? members.Count : 0;
        }
    }

    /// <summary>A snapshot of a playlist's job ids.</summary>
    public IReadOnlyList<string> JobIdsOf(string playlistId)
    {
        lock (_gate)
        {
            return _members.TryGetValue(playlistId, out var members) ? members.ToArray() : [];
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
