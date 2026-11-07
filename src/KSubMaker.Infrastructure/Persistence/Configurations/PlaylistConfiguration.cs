using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KSubMaker.Infrastructure.Persistence.Configurations;

public sealed class PlaylistConfiguration : IEntityTypeConfiguration<Playlist>
{
    public void Configure(EntityTypeBuilder<Playlist> builder)
    {
        builder.ToTable("Playlists");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(256).IsRequired();
    }
}

public sealed class PlaylistEntryConfiguration : IEntityTypeConfiguration<PlaylistEntry>
{
    public void Configure(EntityTypeBuilder<PlaylistEntry> builder)
    {
        builder.ToTable("PlaylistEntries");
        builder.HasKey(e => new { e.PlaylistId, e.JobId });

        builder.Property(e => e.PlaylistId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.JobId).HasMaxLength(64).IsRequired();

        // Both sides cascade in the database: removing a job from the queue (or a playlist) takes its
        // memberships with it, including when the queue deletes rows with ExecuteDelete, which
        // bypasses the change tracker.
        builder.HasOne<Playlist>()
            .WithMany()
            .HasForeignKey(e => e.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Job>()
            .WithMany()
            .HasForeignKey(e => e.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.JobId);
    }
}
