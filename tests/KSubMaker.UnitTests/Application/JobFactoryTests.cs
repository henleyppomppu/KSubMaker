using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Media;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// The full <see cref="JobFactory"/> decision table: every option that can turn a scanned file into
/// (or away from) a queue entry.
/// </summary>
public sealed class JobFactoryTests
{
    private static readonly DateTime LastWrite = new(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

    private static VideoFile File(
        bool koreanSidecar = false,
        bool externalSubtitle = false,
        bool embeddedSubtitle = false,
        long size = 1_000L,
        DateTime? lastWrite = null)
    {
        string[] sidecars = koreanSidecar
            ? ["/videos/movie.ko.srt"]
            : externalSubtitle ? ["/videos/movie.en.srt"] : [];

        return new VideoFile
        {
            FullPath = "/videos/movie.mkv",
            FileName = "movie.mkv",
            Extension = ".mkv",
            SizeBytes = size,
            LastWriteTimeUtc = lastWrite ?? LastWrite,
            DurationSeconds = 120d,
            HasAudioTrack = true,
            Probed = true,
            HasKoreanExternalSubtitle = koreanSidecar,
            ExternalSubtitlePaths = sidecars,
            SubtitleTracks = embeddedSubtitle
                ? [new EmbeddedSubtitleTrackInfo { Index = 0, Language = "eng" }]
                : []
        };
    }

    private static AppSettings Settings(Action<AppSettings>? configure = null)
    {
        var settings = new AppSettings();
        configure?.Invoke(settings);
        return settings;
    }

    private static Job ExistingJob(JobStatus status, long size = 1_000L, DateTime? lastWrite = null) => new()
    {
        Id = "existing",
        VideoPath = "/videos/movie.mkv",
        FileName = "movie.mkv",
        FileSize = size,
        LastWriteTimeUtc = lastWrite ?? LastWrite,
        Status = status,
        CurrentStage = status == JobStatus.Completed ? JobStage.Done : JobStage.None,
        OverallProgress = status == JobStatus.Completed ? 100d : 0d
    };

    private static readonly Func<string, bool> NothingExists = _ => false;
    private static readonly Func<string, bool> OutputExists = path => path.EndsWith(".ko.srt", StringComparison.OrdinalIgnoreCase);

    // -----------------------------------------------------------------------
    // brand new files
    // -----------------------------------------------------------------------

    [Fact]
    public void A_new_file_with_nothing_beside_it_creates_a_pending_job()
    {
        var result = JobFactory.Create(File(), existing: null, Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Created);
        result.Job.Should().NotBeNull();
        result.Job!.Status.Should().Be(JobStatus.Pending);
        result.Job.CurrentStage.Should().Be(JobStage.None);
        result.Job.VideoPath.Should().Be("/videos/movie.mkv");
        result.Job.OutputPath.Should().Be(Path.Combine("/videos", "movie.ko.srt"));
        result.Job.CreatedAtUtc.Should().Be(Clock.GetUtcNow().UtcDateTime);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void A_new_job_carries_the_probe_results_and_the_configured_engine()
    {
        var settings = Settings(s => s.TranslationEngine = TranslationEngineKind.LocalLlm);

        var job = JobFactory.Create(File(embeddedSubtitle: true), null, settings, NothingExists, Clock).Job!;

        job.DurationSeconds.Should().Be(120d);
        job.HasAudioTrack.Should().BeTrue();
        job.HasEmbeddedSubtitle.Should().BeTrue();
        job.TranslationEngine.Should().Be(TranslationEngineKind.LocalLlm);
    }

    [Fact]
    public void An_unprobed_file_is_assumed_to_have_audio()
    {
        var file = File() with { Probed = false, HasAudioTrack = false };

        JobFactory.Create(file, null, Settings(), NothingExists, Clock).Job!
            .HasAudioTrack.Should().BeTrue();
    }

    [Fact]
    public void The_output_suffix_setting_is_honoured()
    {
        var settings = Settings(s => s.OutputSuffix = "kor");

        JobFactory.Create(File(), null, settings, NothingExists, Clock).Job!
            .OutputPath.Should().Be(Path.Combine("/videos", "movie.kor.srt"));
    }

    // -----------------------------------------------------------------------
    // "이미 한국어 자막이 있는 파일 건너뛰기"
    // -----------------------------------------------------------------------

    [Fact]
    public void A_korean_sidecar_marks_a_brand_new_file_as_already_done()
    {
        var result = JobFactory.Create(File(koreanSidecar: true), null, Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.AlreadyDone);
        result.Job!.Status.Should().Be(JobStatus.Completed);
        result.Job.CurrentStage.Should().Be(JobStage.Done);
        result.Job.OverallProgress.Should().Be(100d);
        result.Job.CompletedAtUtc.Should().Be(Clock.GetUtcNow().UtcDateTime);
        result.Reason.Should().Contain("이미 한국어 자막이 있어");
    }

    [Fact]
    public void An_existing_target_srt_on_disk_counts_as_a_korean_subtitle()
    {
        var result = JobFactory.Create(File(), null, Settings(), OutputExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.AlreadyDone);
    }

    [Fact]
    public void An_already_queued_file_with_a_korean_subtitle_is_left_alone()
    {
        var existing = ExistingJob(JobStatus.Pending);

        var result = JobFactory.Create(File(koreanSidecar: true), existing, Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Unchanged);
        result.Job.Should().BeSameAs(existing);
        existing.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public void Choosing_process_anyway_queues_a_file_that_already_has_korean()
    {
        var settings = Settings(s => s.ExistingSubtitleRule = ExistingSubtitleRule.ProcessAnyway);

        var result = JobFactory.Create(File(koreanSidecar: true), null, settings, NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Created);
        result.Job!.Status.Should().Be(JobStatus.Pending);
        result.Job.HasKoreanSubtitle.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // "완료된 파일 다시 처리"
    // -----------------------------------------------------------------------

    [Fact]
    public void ReprocessCompleted_overrides_the_korean_subtitle_skip()
    {
        var settings = Settings(s => s.ReprocessCompleted = true);

        var result = JobFactory.Create(File(koreanSidecar: true), null, settings, NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Created);
    }

    [Fact]
    public void ReprocessCompleted_requeues_a_finished_job()
    {
        var settings = Settings(s => s.ReprocessCompleted = true);
        var existing = ExistingJob(JobStatus.Completed);

        var result = JobFactory.Create(File(), existing, settings, NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Requeued);
        existing.Status.Should().Be(JobStatus.Pending);
        existing.CurrentStage.Should().Be(JobStage.None);
        existing.OverallProgress.Should().Be(0d);
        existing.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Without_ReprocessCompleted_a_finished_job_is_left_alone()
    {
        var result = JobFactory.Create(File(), ExistingJob(JobStatus.Completed), Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Unchanged);
        result.Reason.Should().Be("이미 완료된 작업입니다.");
    }

    // -----------------------------------------------------------------------
    // "실패한 파일만 다시 처리"
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Skipped)]
    [InlineData(JobStatus.Paused)]
    public void RetryFailedOnly_skips_every_job_that_did_not_fail(JobStatus status)
    {
        var settings = Settings(s => s.RetryFailedOnly = true);

        var result = JobFactory.Create(File(), ExistingJob(status), settings, NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Skipped);
        result.Reason.Should().Contain("실패한 작업만");
    }

    [Fact]
    public void RetryFailedOnly_requeues_a_failed_job()
    {
        var settings = Settings(s => s.RetryFailedOnly = true);
        var existing = ExistingJob(JobStatus.Failed);
        existing.ErrorCode = "FFMPEG_FAILED";
        existing.ErrorMessage = "실패";

        var result = JobFactory.Create(File(), existing, settings, NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Requeued);
        existing.Status.Should().Be(JobStatus.Pending);
        existing.ErrorCode.Should().BeNull();
        existing.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void RetryFailedOnly_does_not_stop_a_brand_new_file_from_being_created()
    {
        // The filter only applies to jobs that already exist in the queue.
        var settings = Settings(s => s.RetryFailedOnly = true);

        JobFactory.Create(File(), null, settings, NothingExists, Clock)
            .Decision.Should().Be(EnqueueDecision.Created);
    }

    // -----------------------------------------------------------------------
    // source file changed
    // -----------------------------------------------------------------------

    [Fact]
    public void A_changed_file_size_requeues_a_completed_job()
    {
        var existing = ExistingJob(JobStatus.Completed, size: 1_000L);

        var result = JobFactory.Create(File(size: 2_000L), existing, Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Requeued);
        result.Reason.Should().Be("원본 파일이 변경되어 다시 처리합니다.");
        existing.FileSize.Should().Be(2_000L);
        existing.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public void A_changed_last_write_time_requeues_a_completed_job()
    {
        var existing = ExistingJob(JobStatus.Completed, lastWrite: LastWrite);

        var result = JobFactory.Create(File(lastWrite: LastWrite.AddMinutes(1)), existing, Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Requeued);
        existing.LastWriteTimeUtc.Should().Be(LastWrite.AddMinutes(1));
    }

    [Fact]
    public void A_changed_file_drops_the_per_file_subtitle_source_override()
    {
        var existing = ExistingJob(JobStatus.Completed, size: 1_000L);
        existing.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        existing.SelectedSubtitleTrackIndex = 3;
        existing.SelectedSubtitleLanguage = "ja";

        JobFactory.Create(File(size: 2_000L), existing, Settings(), NothingExists, Clock)
            .Decision.Should().Be(EnqueueDecision.Requeued);

        // A re-encode can renumber (or remove) the stream the user picked.
        existing.HasSourceOverride.Should().BeFalse();
        existing.SelectedSubtitleTrackIndex.Should().BeNull();
        existing.SelectedSubtitleLanguage.Should().BeNull();
    }

    [Fact]
    public void A_requeue_of_an_unchanged_file_keeps_the_subtitle_source_override()
    {
        var existing = ExistingJob(JobStatus.Failed);
        existing.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        existing.SelectedSubtitleTrackIndex = 3;

        JobFactory.Create(File(), existing, Settings(), NothingExists, Clock)
            .Decision.Should().Be(EnqueueDecision.Requeued);

        existing.SourceOverride.Should().Be(JobSourceOverride.EmbeddedSubtitle);
        existing.SelectedSubtitleTrackIndex.Should().Be(3);
    }

    [Fact]
    public void A_new_job_starts_on_the_core_path()
    {
        var job = JobFactory.Create(File(embeddedSubtitle: true), null, Settings(), NothingExists, Clock).Job!;

        job.SourceOverride.Should().Be(JobSourceOverride.None);
        job.HasSourceOverride.Should().BeFalse();
    }

    [Fact]
    public void A_changed_file_still_loses_to_the_korean_subtitle_skip()
    {
        // The skip filter runs first: a Korean subtitle beside the file wins over "source changed".
        var existing = ExistingJob(JobStatus.Completed, size: 1_000L);

        var result = JobFactory.Create(File(koreanSidecar: true, size: 2_000L), existing, Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Unchanged);
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void A_failed_or_cancelled_job_is_requeued_by_a_plain_rescan(JobStatus status)
    {
        var result = JobFactory.Create(File(), ExistingJob(status), Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Requeued);
        result.Job!.Status.Should().Be(JobStatus.Pending);
    }

    /// <summary>
    /// 건너뜀 is a deliberate choice, not an interrupted run — unlike Failed/Cancelled, a plain rescan
    /// must not silently undo it. Retrying a skipped job stays an explicit [다시 넣기].
    /// </summary>
    [Fact]
    public void A_skipped_job_is_left_alone_by_a_plain_rescan()
    {
        var result = JobFactory.Create(File(), ExistingJob(JobStatus.Skipped), Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Unchanged);
        result.Job!.Status.Should().Be(JobStatus.Skipped);
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Paused)]
    public void An_unfinished_job_is_left_exactly_as_it_was(JobStatus status)
    {
        var existing = ExistingJob(status);

        var result = JobFactory.Create(File(), existing, Settings(), NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Unchanged);
        existing.Status.Should().Be(status);
    }

    // -----------------------------------------------------------------------
    // ExistingSubtitleRule
    // -----------------------------------------------------------------------

    [Fact]
    public void AlwaysTranscribe_ignores_a_foreign_sidecar()
    {
        var settings = Settings(s => s.ExistingSubtitleRule = ExistingSubtitleRule.ProcessAnyway);

        JobFactory.Create(File(externalSubtitle: true), null, settings, NothingExists, Clock)
            .Decision.Should().Be(EnqueueDecision.Created);
    }

    [Fact]
    public void SkipIfExternalSubtitleExists_skips_a_new_file_with_any_sidecar()
    {
        var settings = Settings(s => s.ExistingSubtitleRule = ExistingSubtitleRule.SkipIfAnySubtitleExists);

        var result = JobFactory.Create(File(externalSubtitle: true), null, settings, NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Skipped);
        result.Job.Should().BeNull();
        result.Reason.Should().Contain("외부 자막");
    }

    [Fact]
    public void SkipIfExternalSubtitleExists_leaves_an_existing_job_unchanged()
    {
        var settings = Settings(s => s.ExistingSubtitleRule = ExistingSubtitleRule.SkipIfAnySubtitleExists);
        var existing = ExistingJob(JobStatus.Failed);

        var result = JobFactory.Create(File(externalSubtitle: true), existing, settings, NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Unchanged);
        existing.Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public void SkipIfExternalSubtitleExists_is_overridden_by_ReprocessCompleted()
    {
        var settings = Settings(s =>
        {
            s.ExistingSubtitleRule = ExistingSubtitleRule.SkipIfAnySubtitleExists;
            s.ReprocessCompleted = true;
        });

        JobFactory.Create(File(externalSubtitle: true), null, settings, NothingExists, Clock)
            .Decision.Should().Be(EnqueueDecision.Created);
    }

    [Fact]
    public void SkipIfExternalSubtitleExists_still_queues_a_file_with_no_sidecar()
    {
        var settings = Settings(s => s.ExistingSubtitleRule = ExistingSubtitleRule.SkipIfAnySubtitleExists);

        JobFactory.Create(File(), null, settings, NothingExists, Clock)
            .Decision.Should().Be(EnqueueDecision.Created);
    }

    [Fact]
    public void CompleteIfKoreanExists_marks_a_new_file_as_done()
    {
        var settings = Settings(s =>
        {
            s.ExistingSubtitleRule = ExistingSubtitleRule.CompleteIfKoreanExists;
        });

        var result = JobFactory.Create(File(koreanSidecar: true), null, settings, NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.AlreadyDone);
        result.Job!.Status.Should().Be(JobStatus.Completed);
        result.Job.OutputPath.Should().Be(Path.Combine("/videos", "movie.ko.srt"));
    }

    [Fact]
    public void CompleteIfKoreanExists_leaves_an_existing_job_unchanged()
    {
        var settings = Settings(s =>
        {
            s.ExistingSubtitleRule = ExistingSubtitleRule.CompleteIfKoreanExists;
        });

        var existing = ExistingJob(JobStatus.Failed);

        var result = JobFactory.Create(File(koreanSidecar: true), existing, settings, NothingExists, Clock);

        result.Decision.Should().Be(EnqueueDecision.Unchanged);
        existing.Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public void CompleteIfKoreanExists_does_nothing_without_a_korean_subtitle()
    {
        var settings = Settings(s =>
        {
            s.ExistingSubtitleRule = ExistingSubtitleRule.CompleteIfKoreanExists;
        });

        JobFactory.Create(File(), null, settings, NothingExists, Clock)
            .Decision.Should().Be(EnqueueDecision.Created);
    }

    [Theory]
    [InlineData(SubtitleSourcePreference.PreferEmbeddedTrack)]
    [InlineData(SubtitleSourcePreference.PreferExternalFile)]
    [InlineData(SubtitleSourcePreference.PreferAnySubtitle)]
    [InlineData(SubtitleSourcePreference.AskPerFile)]
    public void Choosing_a_subtitle_source_never_filters_the_file_out(SubtitleSourcePreference source)
    {
        // The source preference answers "translate what", not "process whether" — that is the whole
        // point of the two being separate settings.
        var settings = Settings(s =>
        {
            s.SubtitleSource = source;
            s.ExistingSubtitleRule = ExistingSubtitleRule.ProcessAnyway;
        });

        JobFactory.Create(File(embeddedSubtitle: true, externalSubtitle: true), null, settings, NothingExists, Clock)
            .Decision.Should().Be(EnqueueDecision.Created);
    }

    // -----------------------------------------------------------------------
    // guards
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_rejects_null_arguments()
    {
        var nullFile = () => JobFactory.Create(null!, null, Settings(), NothingExists, Clock);
        var nullSettings = () => JobFactory.Create(File(), null, null!, NothingExists, Clock);

        nullFile.Should().Throw<ArgumentNullException>();
        nullSettings.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Every_created_job_gets_a_distinct_id()
    {
        var first = JobFactory.Create(File(), null, Settings(), NothingExists, Clock).Job!;
        var second = JobFactory.Create(File(), null, Settings(), NothingExists, Clock).Job!;

        first.Id.Should().NotBe(second.Id);
        first.Id.Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
