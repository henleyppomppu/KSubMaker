using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// 목록 새로고침 drops rows whose video is gone (before, the list only ever grew — 2026-11-02: two
/// deleted silent lectures stayed listed), and the grid shows the rows of one folder at a time.
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

    [Theory]
    [InlineData(JobStatus.Probing)]
    [InlineData(JobStatus.ExtractingAudio)]
    [InlineData(JobStatus.Transcribing)]
    [InlineData(JobStatus.Translating)]
    [InlineData(JobStatus.WritingSubtitle)]
    public void A_running_job_is_never_dropped(JobStatus status)
    {
        var running = JobAt(@"D:\a\1.mp4", status);

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
    [InlineData(@"Z:\강의\2021", @"z:\강의\2021\", true)]
    [InlineData(@"Z:\강의\2021", @"Z:\강의\2022", false)]
    [InlineData("", "", false)]
    [InlineData(null, @"Z:\a", false)]
    public void Folder_identity_ignores_case_and_a_trailing_separator(string? a, string? b, bool expected)
    {
        QueueListReconciler.IsSameFolder(a, b).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"Z:\강의\2021\01.mp4", @"Z:\강의\2021", false, true)]
    [InlineData(@"z:\강의\2021\01.mp4", @"Z:\강의\2021\", false, true)]
    [InlineData(@"Z:\강의\2021\1학기\01.mp4", @"Z:\강의\2021", true, true)]
    [InlineData(@"Z:\강의\2021\1학기\01.mp4", @"Z:\강의\2021", false, false)]
    [InlineData(@"Z:\강의\2021-보충\01.mp4", @"Z:\강의\2021", true, false)]
    [InlineData(@"Z:\강의\01.mp4", @"Z:\", true, true)]
    [InlineData(@"Z:\강의\2021\01.mp4", "", true, false)]
    public void A_row_belongs_to_the_folder_its_video_is_in(string path, string folder, bool includeSubfolders, bool expected)
    {
        QueueListReconciler.IsInFolder(path, folder, includeSubfolders).Should().Be(expected);
    }
}
