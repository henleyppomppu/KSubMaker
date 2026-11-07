using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Playlists;
using KSubMaker.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace KSubMaker.Infrastructure.Persistence;

/// <summary>
/// The application's only database context: a single SQLite file under
/// <c>%LOCALAPPDATA%\KSubMaker\database</c>.
///
/// Instances are short lived and created through <see cref="IDbContextFactory{TContext}"/> because
/// the queue pump, the UI thread and the model downloader all touch the repositories concurrently,
/// and a DbContext is explicitly not thread safe.
/// </summary>
public sealed class KSubMakerDbContext(DbContextOptions<KSubMakerDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<SettingRecord> Settings => Set<SettingRecord>();

    public DbSet<ModelInstallation> Models => Set<ModelInstallation>();

    public DbSet<Playlist> Playlists => Set<Playlist>();

    public DbSet<PlaylistEntry> PlaylistEntries => Set<PlaylistEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Registered explicitly rather than via ApplyConfigurationsFromAssembly: the assembly-wide
        // scan reflects over every type here, including DesignTimeDbContextFactory, whose base
        // interface lives in the design-time-only EntityFrameworkCore.Design package that is not
        // deployed with the application.
        modelBuilder.ApplyConfiguration(new JobConfiguration());
        modelBuilder.ApplyConfiguration(new SettingRecordConfiguration());
        modelBuilder.ApplyConfiguration(new ModelInstallationConfiguration());
        modelBuilder.ApplyConfiguration(new PlaylistConfiguration());
        modelBuilder.ApplyConfiguration(new PlaylistEntryConfiguration());
    }
}
