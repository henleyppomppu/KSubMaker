using System.Text;
using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Media;
using KSubMaker.Domain.Settings;
using KSubMaker.IntegrationTests.Fixtures;
using KSubMaker.IntegrationTests.Infrastructure;
using Xunit;

namespace KSubMaker.IntegrationTests.Pipeline;

/// <summary>Retrying a failed job, and the output-conflict policy applied end to end on real files.</summary>
[Collection(MediaCollection.Name)]
public sealed class RetryAndConflictPolicyTests(MediaFixture media) : IAsyncLifetime
{
    private const string ExistingSubtitle =
        "1\r\n00:00:00,000 --> 00:00:02,000\r\n손대지 말아야 할 기존 자막\r\n\r\n";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly TempWorkspace _workspace = new("ksubmaker-policy");

    private PipelineHarness? _harness;
    private string _folder = string.Empty;
    private string _video = string.Empty;

    private PipelineHarness Harness => _harness ?? throw new InvalidOperationException("StageAsync를 먼저 호출하세요.");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }

        _workspace.Dispose();
    }

    private async Task StageAsync(string fileName = "clip.mp4")
    {
        _folder = _workspace.CreateSubdirectory("영상");
        _video = media.CopyTo(media.SampleVideo, Path.Combine(_folder, fileName));

        _harness = new PipelineHarness(_workspace);
        await _harness.InitializeDatabaseAsync();
    }

    private async Task EnqueueAsync(AppSettings settings)
    {
        var scan = await Harness.ScanService.ScanAsync(new ScanRequest { RootFolder = _folder });

        var probed = new List<VideoFile>();
        foreach (var file in scan.Files)
        {
            probed.Add(await Harness.MediaProbe.ProbeAsync(file));
        }

        await Harness.Queue.EnqueueAsync(probed, settings);
    }

    private async Task RunWithPolicyAsync(OutputConflictPolicy policy)
    {
        var settings = PipelineHarness.DeterministicSettings(s =>
        {
            s.OutputConflictPolicy = policy;

            // Otherwise the "이미 한국어 자막이 있으면 완료로 표시" rule would stop the file before
            // it ever reaches the writer, and the conflict policy would never be exercised.
            s.ExistingSubtitleRule = ExistingSubtitleRule.ProcessAnyway;
        });

        await EnqueueAsync(settings);
        await Harness.RunQueueToCompletionAsync(settings, Timeout);
    }

    // -----------------------------------------------------------------------
    // retry
    // -----------------------------------------------------------------------

    [RequiresFfmpegFact]
    public async Task A_job_pointing_at_a_deleted_file_fails_with_VIDEO_NOT_FOUND_and_can_be_retried()
    {
        await StageAsync();

        var settings = PipelineHarness.DeterministicSettings();
        await EnqueueAsync(settings);

        // The user moved the file away between the scan and the run.
        File.Delete(_video);

        await Harness.RunQueueToCompletionAsync(settings, Timeout);

        var job = Harness.Queue.Jobs.Single();

        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorCode.Should().Be(ErrorCodes.VideoNotFound);
        job.ErrorMessage.Should().Contain("영상 파일을 찾을 수 없습니다");

        // ---- retry -----------------------------------------------------------
        await Harness.Queue.RetryAsync([job.Id]);

        job.Status.Should().Be(JobStatus.Pending);
        job.RetryCount.Should().Be(1);
        job.OverallProgress.Should().Be(0d);
        job.CurrentStage.Should().Be(JobStage.None);
        job.CompletedAtUtc.Should().BeNull();

        var persisted = await Harness.JobRepository.FindAsync(job.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task Retrying_a_failed_job_clears_its_error()
    {
        await StageAsync();

        var settings = PipelineHarness.DeterministicSettings();
        await EnqueueAsync(settings);

        File.Delete(_video);
        await Harness.RunQueueToCompletionAsync(settings, Timeout);

        var job = Harness.Queue.Jobs.Single();
        job.Status.Should().Be(JobStatus.Failed);

        await Harness.Queue.RetryAsync([job.Id]);

        job.ErrorCode.Should().BeNull();
        job.ErrorMessage.Should().BeNull();
    }

    [RequiresFfmpegFact]
    public async Task A_retried_job_succeeds_once_the_file_is_back()
    {
        await StageAsync();

        var settings = PipelineHarness.DeterministicSettings();
        await EnqueueAsync(settings);

        var backup = Path.Combine(_workspace.Root, "backup.mp4");
        File.Move(_video, backup);

        await Harness.RunQueueToCompletionAsync(settings, Timeout);
        Harness.Queue.Jobs.Single().Status.Should().Be(JobStatus.Failed);

        File.Move(backup, _video);

        await Harness.Queue.RetryAsync([Harness.Queue.Jobs.Single().Id]);
        await Harness.RunQueueToCompletionAsync(settings, Timeout);

        Harness.Queue.Jobs.Single().Status.Should().Be(JobStatus.Completed);
        SrtAssertions.AssertIsWellFormedKoreanSrt(Path.Combine(_folder, "clip.ko.srt"));
    }

    [RequiresFfmpegFact]
    public async Task Retrying_a_job_that_is_not_finished_does_nothing()
    {
        await StageAsync();

        await EnqueueAsync(PipelineHarness.DeterministicSettings());

        var job = Harness.Queue.Jobs.Single();
        job.Status.Should().Be(JobStatus.Pending);

        await Harness.Queue.RetryAsync([job.Id]);

        job.RetryCount.Should().Be(0, "a pending job is not in a retryable state");
    }

    /// <summary>
    /// A job that never left Pending has no in-progress work to abandon, so 건너뛰기 on it lands on
    /// Skipped rather than Cancelled — see <see cref="JobQueueService.CancelAsync"/>.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task Cancelling_a_pending_job_marks_it_skipped()
    {
        await StageAsync();

        await EnqueueAsync(PipelineHarness.DeterministicSettings());

        var job = Harness.Queue.Jobs.Single();
        await Harness.Queue.CancelAsync([job.Id]);

        job.Status.Should().Be(JobStatus.Skipped);
        (await Harness.JobRepository.FindAsync(job.Id))!.Status.Should().Be(JobStatus.Skipped);
    }

    // -----------------------------------------------------------------------
    // conflict policy
    // -----------------------------------------------------------------------

    [RequiresFfmpegFact]
    public async Task Skip_leaves_an_existing_subtitle_untouched()
    {
        await StageAsync();

        var target = Path.Combine(_folder, "clip.ko.srt");
        await File.WriteAllTextAsync(target, ExistingSubtitle, new UTF8Encoding(true));
        var before = await File.ReadAllBytesAsync(target);

        await RunWithPolicyAsync(OutputConflictPolicy.Skip);

        (await File.ReadAllBytesAsync(target)).Should().Equal(before, "Skip must not touch the existing file");
        Directory.GetFiles(_folder, "*.srt").Should().ContainSingle();
        Harness.Queue.Jobs.Single().Status.Should().Be(JobStatus.Completed);
    }

    [RequiresFfmpegFact]
    public async Task Overwrite_replaces_an_existing_subtitle()
    {
        await StageAsync();

        var target = Path.Combine(_folder, "clip.ko.srt");
        await File.WriteAllTextAsync(target, ExistingSubtitle, new UTF8Encoding(true));

        await RunWithPolicyAsync(OutputConflictPolicy.Overwrite);

        var text = await File.ReadAllTextAsync(target);

        text.Should().NotContain("손대지 말아야 할 기존 자막");
        text.Should().Contain("[테스트]");
        Directory.GetFiles(_folder, "*.srt").Should().ContainSingle();
        SrtAssertions.AssertIsWellFormedKoreanSrt(target);
    }

    [RequiresFfmpegFact]
    public async Task CreateNumberedCopy_writes_a_2_file_and_keeps_the_original()
    {
        await StageAsync();

        var target = Path.Combine(_folder, "clip.ko.srt");
        await File.WriteAllTextAsync(target, ExistingSubtitle, new UTF8Encoding(true));
        var before = await File.ReadAllBytesAsync(target);

        await RunWithPolicyAsync(OutputConflictPolicy.CreateNumberedCopy);

        var numbered = Path.Combine(_folder, "clip.ko (2).srt");

        File.Exists(numbered).Should().BeTrue();
        (await File.ReadAllBytesAsync(target)).Should().Equal(before);
        SrtAssertions.AssertIsWellFormedKoreanSrt(numbered);

        Harness.Queue.Jobs.Single().OutputPath.Should().Be(numbered);
    }

    [RequiresFfmpegFact]
    public async Task An_existing_korean_subtitle_marks_the_file_as_already_done_by_default()
    {
        await StageAsync();

        await File.WriteAllTextAsync(Path.Combine(_folder, "clip.ko.srt"), ExistingSubtitle, new UTF8Encoding(true));

        var settings = PipelineHarness.DeterministicSettings();      // SkipIfKoreanSubtitleExists = true
        var scan = await Harness.ScanService.ScanAsync(new ScanRequest { RootFolder = _folder });

        scan.Files.Single().HasKoreanExternalSubtitle.Should().BeTrue();

        var results = await Harness.Queue.EnqueueAsync([scan.Files.Single()], settings);

        results.Single().Decision.Should().Be(EnqueueDecision.AlreadyDone);
        Harness.Queue.Jobs.Single().Status.Should().Be(JobStatus.Completed);
    }

    [RequiresFfmpegFact]
    public async Task No_temporary_files_are_left_beside_the_written_subtitle()
    {
        await StageAsync();

        await RunWithPolicyAsync(OutputConflictPolicy.Overwrite);

        Directory.GetFiles(_folder, "*.tmp").Should().BeEmpty();
        Directory.GetFiles(_folder, ".*").Should().BeEmpty("the temp file is hidden by a leading dot");
    }
}
