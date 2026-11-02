using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// 폴더 선택 replaces the list; 목록 새로고침 re-checks it and drops rows whose video is gone.
/// Before this the list only ever grew (2026-11-02: two deleted silent lectures stayed listed).
/// </summary>
public sealed class QueueListReconcilerTests
{
    private static Job JobAt(string path, JobStatus status = JobStatus.Pending, double progress = 0d) => new()
    {
        Id = path,
        VideoPath = path,
        FileName = Path.GetFileName(path),
        Status = status,
        OverallProgress = progress
    };

    [Fact]
    public void Picking_a_folder_drops_every_row_the_new_scan_did_not_find()
    {
        var jobs = new[] { JobAt(@"D:\a\1.mp4"), JobAt(@"D:\b\2.mp4", JobStatus.Completed), JobAt(@"D:\b\3.mp4") };

        var dropped = QueueListReconciler.DropOnFolderChange(jobs, [@"d:\B\3.MP4"]);

        dropped.Select(j => j.VideoPath).Should().BeEquivalentTo(@"D:\a\1.mp4", @"D:\b\2.mp4");
    }

    [Theory]
    [InlineData(JobStatus.Probing)]
    [InlineData(JobStatus.ExtractingAudio)]
    [InlineData(JobStatus.Transcribing)]
    [InlineData(JobStatus.Translating)]
    [InlineData(JobStatus.WritingSubtitle)]
    public void A_running_job_is_never_dropped(JobStatus status)
    {
        var running = JobAt(@"D:\a\1.mp4", status);

        QueueListReconciler.DropOnFolderChange([running], []).Should().BeEmpty();
        QueueListReconciler.DropOnRefresh([running], _ => false).Should().BeEmpty();
    }

    [Fact]
    public void Refreshing_drops_only_rows_whose_video_is_gone()
    {
        var jobs = new[] { JobAt(@"D:\a\here.mp4"), JobAt(@"D:\a\deleted.mp4", JobStatus.Skipped) };

        var dropped = QueueListReconciler.DropOnRefresh(jobs, p => p.EndsWith("here.mp4", StringComparison.Ordinal));

        dropped.Should().ContainSingle().Which.VideoPath.Should().Be(@"D:\a\deleted.mp4");
    }

    [Theory]
    [InlineData(JobStatus.Paused, 0d, true)]
    [InlineData(JobStatus.Failed, 10d, true)]
    [InlineData(JobStatus.Cancelled, 40d, true)]
    [InlineData(JobStatus.Pending, 25d, true)]
    [InlineData(JobStatus.Pending, 0d, false)]
    [InlineData(JobStatus.Failed, 0d, false)]
    [InlineData(JobStatus.Completed, 100d, false)]
    [InlineData(JobStatus.Skipped, 100d, false)]
    public void Only_part_way_unfinished_work_counts_as_something_to_lose(JobStatus status, double progress, bool expected)
    {
        QueueListReconciler.HasUnfinishedProgress(JobAt(@"D:\a\1.mp4", status, progress)).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"Z:\강의\2021", @"z:\강의\2021\", true)]
    [InlineData(@"Z:\강의\2021", @"Z:\강의\2022", false)]
    [InlineData("", "", false)]
    [InlineData(null, @"Z:\a", false)]
    public void Folder_identity_ignores_case_and_a_trailing_separator(string? a, string? b, bool expected)
    {
        QueueListReconciler.IsSameFolder(a, b).Should().Be(expected);
    }
}
