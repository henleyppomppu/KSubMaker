using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Playlists;
using KSubMaker.Domain.Settings;

namespace KSubMaker.Application.Abstractions;

public interface IJobRepository
{
    Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Job?> FindAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Looks a job up by source path so a rescan reuses the existing record.</summary>
    Task<Job?> FindByPathAsync(string videoPath, CancellationToken cancellationToken = default);

    Task AddAsync(Job job, CancellationToken cancellationToken = default);

    Task AddRangeAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default);

    Task UpdateAsync(Job job, CancellationToken cancellationToken = default);

    /// <summary>Coalesced write used for high-frequency progress updates.</summary>
    Task UpdateRangeAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default);

    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    Task RemoveRangeAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called at startup: any job left in an active state by a crash is demoted so it can be resumed
    /// rather than appearing to still be running.
    /// </summary>
    Task<int> ResetOrphanedActiveJobsAsync(CancellationToken cancellationToken = default);
}

public interface IPlaylistRepository
{
    Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlaylistEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);

    Task AddAsync(Playlist playlist, CancellationToken cancellationToken = default);

    Task RenameAsync(string id, string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes the playlist and its entries. The jobs themselves are untouched.</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    Task AddEntriesAsync(IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default);

    Task RemoveEntriesAsync(string playlistId, IReadOnlyList<string> jobIds, CancellationToken cancellationToken = default);
}

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IModelRepository
{
    Task<IReadOnlyList<ModelInstallation>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ModelInstallation?> FindAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(ModelInstallation model, CancellationToken cancellationToken = default);
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Applies pending schema migrations / creates the database on first run.</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
