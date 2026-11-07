using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Playlists;
using Microsoft.EntityFrameworkCore;

namespace KSubMaker.Infrastructure.Persistence.Repositories;

/// <summary>
/// Playlists and their memberships. Short-lived contexts per call, like the other repositories:
/// the UI thread and the queue pump both reach the database.
/// </summary>
public sealed class PlaylistRepository(IDbContextFactory<KSubMakerDbContext> contextFactory) : IPlaylistRepository
{
    private readonly IDbContextFactory<KSubMakerDbContext> _contextFactory = contextFactory;

    public async Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Playlists
            .AsNoTracking()
            .OrderBy(p => p.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PlaylistEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.PlaylistEntries
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Playlists
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, name), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Entries go by the cascade.
        await db.Playlists.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddEntriesAsync(IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.PlaylistEntries.AddRange(entries);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveEntriesAsync(
        string playlistId,
        IReadOnlyList<string> jobIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        if (jobIds.Count == 0)
        {
            return;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.PlaylistEntries
            .Where(e => e.PlaylistId == playlistId && jobIds.Contains(e.JobId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
