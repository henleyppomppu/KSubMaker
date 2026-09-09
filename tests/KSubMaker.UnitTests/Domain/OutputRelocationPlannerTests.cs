using FluentAssertions;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// What moves when the output folder setting changes, covering the "결과 폴더를 옮길 때 파일도
/// 같이 옮기자" request: never force a re-run, only relocate what is already sitting where the old
/// setting put it.
/// </summary>
public sealed class OutputRelocationPlannerTests
{
    private static string Combine(params string[] parts) => Path.Combine(parts);

    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void A_job_whose_output_moved_is_planned()
    {
        var oldPath = Combine("videos", "movie.ko.srt");
        var jobs = new[] { ("videos/movie.mkv", (string?)oldPath) };

        var plan = OutputRelocationPlanner.Plan(jobs, "ko", "out", Existing(oldPath));

        plan.Should().HaveCount(1);
        plan[0].OldPath.Should().Be(oldPath);
        plan[0].NewPath.Should().Be(Combine("out", "videos", "movie.ko.srt"));
    }

    [Fact]
    public void A_job_whose_recorded_output_does_not_exist_on_disk_is_skipped()
    {
        // The database can lag reality — a moved or deleted file must not be "found" twice.
        var jobs = new[] { ("videos/movie.mkv", (string?)Combine("videos", "movie.ko.srt")) };

        var plan = OutputRelocationPlanner.Plan(jobs, "ko", "out", Existing());

        plan.Should().BeEmpty();
    }

    [Fact]
    public void A_job_with_no_recorded_output_is_skipped()
    {
        var jobs = new[] { ("videos/movie.mkv", (string?)null) };

        var plan = OutputRelocationPlanner.Plan(jobs, "ko", "out", Existing());

        plan.Should().BeEmpty();
    }

    [Fact]
    public void A_job_already_at_the_new_path_is_left_alone()
    {
        // The suffix changed too, so the new path happens to equal the old one for this file.
        var path = Combine("out", "videos", "movie.ko.srt");
        var jobs = new[] { ("videos/movie.mkv", (string?)path) };

        var plan = OutputRelocationPlanner.Plan(jobs, "ko", "out", Existing(path));

        plan.Should().BeEmpty();
    }

    [Fact]
    public void A_job_is_skipped_when_something_already_sits_at_the_new_path()
    {
        // Moving never overwrites — the caller can fall back to a normal re-run for this one file.
        var oldPath = Combine("videos", "movie.ko.srt");
        var newPath = Combine("out", "videos", "movie.ko.srt");
        var jobs = new[] { ("videos/movie.mkv", (string?)oldPath) };

        var plan = OutputRelocationPlanner.Plan(jobs, "ko", "out", Existing(oldPath, newPath));

        plan.Should().BeEmpty();
    }

    [Fact]
    public void Clearing_the_output_directory_moves_files_back_next_to_the_source()
    {
        var oldPath = Combine("out", "videos", "movie.ko.srt");
        var jobs = new[] { ("videos/movie.mkv", (string?)oldPath) };

        var plan = OutputRelocationPlanner.Plan(jobs, "ko", null, Existing(oldPath));

        plan.Should().HaveCount(1);
        plan[0].NewPath.Should().Be(Combine("videos", "movie.ko.srt"));
    }

    [Fact]
    public void Only_the_jobs_that_actually_moved_are_planned()
    {
        var unchanged = Combine("out", "a", "a.ko.srt");
        var moved = Combine("old-out", "b", "b.ko.srt");

        var jobs = new[]
        {
            ("a/a.mkv", (string?)unchanged),
            ("b/b.mkv", (string?)moved)
        };

        var plan = OutputRelocationPlanner.Plan(jobs, "ko", "out", Existing(unchanged, moved));

        plan.Should().ContainSingle(r => r.OldPath == moved);
    }

    [Fact]
    public void A_null_job_sequence_is_rejected()
    {
        var act = () => OutputRelocationPlanner.Plan(null!, "ko", "out", Existing());

        act.Should().Throw<ArgumentNullException>();
    }
}
