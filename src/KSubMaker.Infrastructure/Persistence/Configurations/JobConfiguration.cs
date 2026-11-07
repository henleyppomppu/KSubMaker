using KSubMaker.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KSubMaker.Infrastructure.Persistence.Configurations;

public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("Jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.Id).HasMaxLength(64).IsRequired();

        // Paths are compared case-insensitively everywhere else in the application (Windows file
        // system semantics), so the column carries NOCASE to keep FindByPathAsync consistent with
        // the in-memory OrdinalIgnoreCase lookups in JobQueueService.
        builder.Property(j => j.VideoPath).HasMaxLength(1024).IsRequired().UseCollation("NOCASE");
        builder.Property(j => j.FileName).HasMaxLength(512).IsRequired();
        builder.Property(j => j.OutputPath).HasMaxLength(1024);

        // Enums are persisted by name. Reordering or inserting a member must never silently
        // re-interpret existing rows, which is exactly what ordinal storage would do.
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.CurrentStage).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.TranslationEngine).HasConversion<string>().HasMaxLength(32);
        builder.Property(j => j.SourceOverride).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(j => j.SelectedSubtitleLanguage).HasMaxLength(16);

        builder.Property(j => j.DetectedLanguage).HasMaxLength(16);
        builder.Property(j => j.WhisperModel).HasMaxLength(64);
        builder.Property(j => j.TranslationModel).HasMaxLength(64);
        builder.Property(j => j.ErrorCode).HasMaxLength(64);
        builder.Property(j => j.ErrorMessage).HasMaxLength(2048);
        builder.Property(j => j.Note).HasMaxLength(2048);

        // EstimatedTimeRemaining is transient UI state: it is recomputed from the live stopwatch on
        // every progress tick and is meaningless after a restart (a job that resumes has a different
        // elapsed time). Persisting it would surface a stale "3분 남음" on a job that is not running,
        // so it is deliberately not mapped rather than stored as ticks.
        builder.Ignore(j => j.EstimatedTimeRemaining);

        // Rebuilt on every run from what the run actually does; meaningless after a restart.
        builder.Ignore(j => j.SkippedStages);

        // Derived from SourceOverride; a stored copy could disagree with it after a manual edit.
        builder.Ignore(j => j.HasSourceOverride);

        builder.HasIndex(j => j.VideoPath).HasDatabaseName("IX_Jobs_VideoPath");
        builder.HasIndex(j => j.Status).HasDatabaseName("IX_Jobs_Status");
        builder.HasIndex(j => j.QueueOrder).HasDatabaseName("IX_Jobs_QueueOrder");
    }
}
