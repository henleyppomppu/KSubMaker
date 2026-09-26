using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// Covers "폴더 재귀 검색" against a fully in-memory file system: nesting, depth limits, hidden
/// entries, an access-denied directory that must not abort the walk, and symlink cycle protection.
/// </summary>
public sealed class VideoScanServiceTests
{
    private static VideoScanService NewService(InMemoryFileSystem fileSystem) =>
        new(fileSystem, NullLogger<VideoScanService>.Instance);

    private static ScanRequest Request(string root = "/videos", bool subfolders = true, bool hidden = false, int maxDepth = 64) =>
        new()
        {
            RootFolder = root,
            IncludeSubfolders = subfolders,
            IncludeHiddenFolders = hidden,
            MaxDepth = maxDepth
        };

    // -----------------------------------------------------------------------
    // basic traversal
    // -----------------------------------------------------------------------

    [Fact]
    public void A_missing_root_folder_produces_an_empty_report_rather_than_an_exception()
    {
        var report = NewService(new InMemoryFileSystem()).Scan(Request("/does/not/exist"));

        report.Files.Should().BeEmpty();
        report.DirectoriesVisited.Should().Be(0);
    }

    [Fact]
    public void Videos_in_the_root_folder_are_found()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/a.mp4")
            .AddFile("/videos/b.mkv")
            .AddFile("/videos/readme.txt");

        var report = NewService(fileSystem).Scan(Request());

        report.Files.Select(f => f.FileName).Should().BeEquivalentTo("a.mp4", "b.mkv");
        report.DirectoriesVisited.Should().Be(1);
    }

    [Fact]
    public void A_deeply_nested_tree_is_walked_completely()
    {
        var fileSystem = new InMemoryFileSystem();
        var path = "/videos";

        for (var depth = 0; depth < 12; depth++)
        {
            fileSystem.AddDirectory(path).AddFile($"{path}/clip{depth}.mp4");
            path += $"/level{depth}";
        }

        fileSystem.AddDirectory(path).AddFile($"{path}/deepest.mkv");

        var report = NewService(fileSystem).Scan(Request());

        report.Files.Should().HaveCount(13);
        report.Files.Select(f => f.FileName).Should().Contain("deepest.mkv");
    }

    [Fact]
    public void A_branching_tree_finds_every_video_exactly_once()
    {
        var fileSystem = new InMemoryFileSystem();

        foreach (var branch in new[] { "a", "b", "c" })
        {
            foreach (var leaf in new[] { "1", "2" })
            {
                fileSystem
                    .AddDirectory($"/videos/{branch}/{leaf}")
                    .AddFile($"/videos/{branch}/{leaf}/clip.mp4");
            }
        }

        var report = NewService(fileSystem).Scan(Request());

        report.Files.Should().HaveCount(6);
        report.Files.Select(f => f.FullPath).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Results_are_ordered_by_directory_then_by_file_name()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos/z")
            .AddDirectory("/videos/a")
            .AddFile("/videos/z/second.mp4")
            .AddFile("/videos/a/beta.mp4")
            .AddFile("/videos/a/alpha.mp4");

        var report = NewService(fileSystem).Scan(Request());

        report.Files.Select(f => f.FileName).Should().Equal("alpha.mp4", "beta.mp4", "second.mp4");
    }

    [Fact]
    public void Subfolders_are_ignored_when_recursion_is_switched_off()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos/sub")
            .AddFile("/videos/root.mp4")
            .AddFile("/videos/sub/nested.mp4");

        var report = NewService(fileSystem).Scan(Request(subfolders: false));

        report.Files.Select(f => f.FileName).Should().Equal("root.mp4");
        report.DirectoriesVisited.Should().Be(1);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(64, 4)]
    public void MaxDepth_limits_how_far_the_walk_descends(int maxDepth, int expectedFiles)
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/d0.mp4")
            .AddDirectory("/videos/a")
            .AddFile("/videos/a/d1.mp4")
            .AddDirectory("/videos/a/b")
            .AddFile("/videos/a/b/d2.mp4")
            .AddDirectory("/videos/a/b/c")
            .AddFile("/videos/a/b/c/d3.mp4");

        var report = NewService(fileSystem).Scan(Request(maxDepth: maxDepth));

        report.Files.Should().HaveCount(expectedFiles);
    }

    // -----------------------------------------------------------------------
    // hidden entries
    // -----------------------------------------------------------------------

    [Fact]
    public void Hidden_folders_are_skipped_by_default()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/visible.mp4")
            .AddDirectory("/videos/.hidden", hidden: true)
            .AddFile("/videos/.hidden/secret.mp4");

        var report = NewService(fileSystem).Scan(Request(hidden: false));

        report.Files.Select(f => f.FileName).Should().Equal("visible.mp4");
        report.SkippedHidden.Should().Be(1);
    }

    [Fact]
    public void Hidden_folders_are_walked_when_the_option_is_on()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/visible.mp4")
            .AddDirectory("/videos/.hidden", hidden: true)
            .AddFile("/videos/.hidden/secret.mp4");

        var report = NewService(fileSystem).Scan(Request(hidden: true));

        report.Files.Select(f => f.FileName).Should().BeEquivalentTo("visible.mp4", "secret.mp4");
        report.SkippedHidden.Should().Be(0);
    }

    [Fact]
    public void Hidden_files_are_skipped_by_default_even_in_a_visible_folder()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/visible.mp4")
            .AddFile("/videos/hidden.mp4", hidden: true);

        var report = NewService(fileSystem).Scan(Request(hidden: false));

        report.Files.Select(f => f.FileName).Should().Equal("visible.mp4");
        report.SkippedHidden.Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // resilience
    // -----------------------------------------------------------------------

    [Fact]
    public void An_access_denied_directory_does_not_abort_the_scan()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/before.mp4")
            .AddDirectory("/videos/denied")
            .AddFile("/videos/denied/unreachable.mp4")
            .AddDirectory("/videos/after")
            .AddFile("/videos/after/reachable.mp4")
            .DenyAccess("/videos/denied");

        var report = NewService(fileSystem).Scan(Request());

        report.Files.Select(f => f.FileName).Should().BeEquivalentTo("before.mp4", "reachable.mp4");
        report.InaccessibleDirectories.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_directory_whose_real_path_cannot_be_resolved_is_counted_and_skipped()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/ok.mp4")
            .AddDirectory("/videos/broken")
            .AddFile("/videos/broken/hidden-behind-a-dead-link.mp4")
            .FailRealPath("/videos/broken");

        var report = NewService(fileSystem).Scan(Request());

        report.Files.Select(f => f.FileName).Should().Equal("ok.mp4");
        report.InaccessibleDirectories.Should().Be(1);
    }

    [Fact]
    public void A_cancelled_scan_throws_OperationCanceledException()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/a.mp4");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => NewService(fileSystem).Scan(Request(), cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task ScanAsync_returns_the_same_report_as_the_synchronous_version()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos/sub")
            .AddFile("/videos/a.mp4")
            .AddFile("/videos/sub/b.mkv");

        var service = NewService(fileSystem);

        var async = await service.ScanAsync(Request());

        async.Files.Select(f => f.FileName).Should().BeEquivalentTo("a.mp4", "b.mkv");
    }

    // -----------------------------------------------------------------------
    // symlink cycles
    // -----------------------------------------------------------------------

    [Fact]
    public void A_symlink_pointing_at_its_own_ancestor_terminates_the_walk()
    {
        // /videos/sub/loop resolves back to /videos, so following it literally would recurse forever.
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos/sub")
            .AddFile("/videos/only.mp4")
            .AddFile("/videos/sub/nested.mp4")
            .AddSymlinkDirectory("/videos/sub/loop", "/videos");

        var scan = () => NewService(fileSystem).Scan(Request());

        scan.Should().NotThrow();

        var report = scan();
        report.SkippedCycles.Should().BeGreaterThan(0);
        report.Files.Select(f => f.FileName).Should().BeEquivalentTo("only.mp4", "nested.mp4");
    }

    [Fact]
    public void A_symlink_that_points_at_its_own_parent_terminates_the_walk()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos/sub")
            .AddFile("/videos/sub/clip.mp4")
            .AddSymlinkDirectory("/videos/sub/self", "/videos/sub");

        var report = NewService(fileSystem).Scan(Request());

        report.Files.Select(f => f.FileName).Should().Equal("clip.mp4");
        report.SkippedCycles.Should().Be(1);
    }

    [Fact]
    public void Two_symlinks_pointing_at_each_other_terminate_the_walk()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos/a")
            .AddDirectory("/videos/b")
            .AddFile("/videos/a/one.mp4")
            .AddFile("/videos/b/two.mp4")
            .AddSymlinkDirectory("/videos/a/toB", "/videos/b")
            .AddSymlinkDirectory("/videos/b/toA", "/videos/a");

        var report = NewService(fileSystem).Scan(Request());

        report.SkippedCycles.Should().BeGreaterThan(0);
        report.Files.Select(f => f.FileName).Should().BeEquivalentTo("one.mp4", "two.mp4");
    }

    [Fact]
    public void A_link_to_a_genuinely_separate_folder_is_still_followed()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddDirectory("/elsewhere")
            .AddFile("/videos/here.mp4")
            .AddFile("/elsewhere/there.mkv")
            .AddSymlinkDirectory("/videos/link", "/elsewhere");

        var report = NewService(fileSystem).Scan(Request());

        report.Files.Select(f => f.FileName).Should().BeEquivalentTo("here.mp4", "there.mkv");
        report.SkippedCycles.Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // sidecar detection
    // -----------------------------------------------------------------------

    [Fact]
    public void Sidecar_subtitles_sharing_the_base_name_are_attached_to_the_video()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/movie.mkv")
            .AddFile("/videos/movie.en.srt")
            .AddFile("/videos/movie.ko.srt")
            .AddFile("/videos/other.ko.srt");

        var report = NewService(fileSystem).Scan(Request());

        var movie = report.Files.Single(f => f.FileName == "movie.mkv");
        movie.ExternalSubtitlePaths.Should().HaveCount(2);
        movie.HasExternalSubtitle.Should().BeTrue();
        movie.HasKoreanExternalSubtitle.Should().BeTrue();
    }

    [Fact]
    public void A_video_with_only_a_foreign_sidecar_is_not_marked_as_having_korean_subtitles()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/movie.mkv")
            .AddFile("/videos/movie.en.srt");

        var movie = NewService(fileSystem).Scan(Request()).Files.Single();

        movie.HasExternalSubtitle.Should().BeTrue();
        movie.HasKoreanExternalSubtitle.Should().BeFalse();
    }

    [Fact]
    public void A_video_with_no_sidecar_reports_none()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/movie.mkv");

        var movie = NewService(fileSystem).Scan(Request()).Files.Single();

        movie.ExternalSubtitlePaths.Should().BeEmpty();
        movie.HasExternalSubtitle.Should().BeFalse();
        movie.HasKoreanExternalSubtitle.Should().BeFalse();
    }

    [Fact]
    public void File_metadata_is_read_from_the_file_system()
    {
        var lastWrite = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/movie.mkv", size: 123_456_789L, lastWriteUtc: lastWrite);

        var movie = NewService(fileSystem).Scan(Request()).Files.Single();

        movie.SizeBytes.Should().Be(123_456_789L);
        movie.LastWriteTimeUtc.Should().Be(lastWrite);
        movie.Extension.Should().Be(".mkv");
        movie.FullPath.Should().Be("/videos/movie.mkv");
        movie.Probed.Should().BeFalse();
    }

    [Fact]
    public void A_custom_extension_set_narrows_the_scan()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/a.mp4")
            .AddFile("/videos/b.mkv");

        var report = NewService(fileSystem).Scan(new ScanRequest
        {
            RootFolder = "/videos",
            Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv" }
        });

        report.Files.Select(f => f.FileName).Should().Equal("b.mkv");
    }

    [Fact]
    public void Korean_folder_and_file_names_are_handled()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/영상 보관함/2026년 자료")
            .AddFile("/영상 보관함/2026년 자료/한국어 제목 (최종).mp4");

        var report = NewService(fileSystem).Scan(Request("/영상 보관함"));

        report.Files.Should().ContainSingle()
            .Which.FileName.Should().Be("한국어 제목 (최종).mp4");
    }

    [Fact]
    public void Scan_rejects_a_null_request()
    {
        var act = () => NewService(new InMemoryFileSystem()).Scan(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_report_records_how_many_directories_were_visited()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos/a")
            .AddDirectory("/videos/b")
            .AddDirectory("/videos/b/c");

        var report = NewService(fileSystem).Scan(Request());

        report.DirectoriesVisited.Should().Be(4);
        report.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    // -----------------------------------------------------------------------
    // drag-and-drop resolution
    // -----------------------------------------------------------------------

    [Fact]
    public void Dropped_video_files_are_taken_and_everything_else_is_counted_as_ignored()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/a.mp4")
            .AddFile("/videos/readme.txt");

        var resolution = NewService(fileSystem).ResolveDropped(
            ["/videos/a.mp4", "/videos/readme.txt", "/videos/missing.mp4"], Request());

        resolution.Files.Select(f => f.FileName).Should().BeEquivalentTo("a.mp4");
        resolution.IgnoredPaths.Should().Be(2, "a non-video and a missing path were dropped");
        resolution.FoldersScanned.Should().Be(0);
    }

    [Fact]
    public void A_dropped_folder_is_scanned_with_the_callers_options()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos/season1")
            .AddFile("/videos/season1/e1.mp4")
            .AddFile("/videos/season1/e2.mkv")
            .AddDirectory("/videos/season1/extras")
            .AddFile("/videos/season1/extras/bonus.mp4");

        var flat = NewService(fileSystem).ResolveDropped(
            ["/videos/season1"], Request(subfolders: false));

        flat.Files.Select(f => f.FileName).Should().BeEquivalentTo("e1.mp4", "e2.mkv");
        flat.FoldersScanned.Should().Be(1);

        var deep = NewService(fileSystem).ResolveDropped(
            ["/videos/season1"], Request(subfolders: true));

        deep.Files.Should().HaveCount(3);
    }

    [Fact]
    public void Dropping_a_folder_and_a_file_inside_it_does_not_enqueue_the_file_twice()
    {
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/a.mp4")
            .AddFile("/videos/b.mp4");

        var resolution = NewService(fileSystem).ResolveDropped(
            ["/videos/a.mp4", "/videos"], Request());

        resolution.Files.Select(f => f.FileName).Should().BeEquivalentTo("a.mp4", "b.mp4");
    }

    [Fact]
    public void An_explicitly_dropped_hidden_file_is_accepted()
    {
        // The hidden filter exists to keep a folder walk from surfacing files the user cannot see.
        // A dropped file was pointed at by the user, which is the opposite situation.
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/secret.mp4", hidden: true);

        var viaDrop = NewService(fileSystem).ResolveDropped(["/videos/secret.mp4"], Request());
        var viaScan = NewService(fileSystem).Scan(Request());

        viaDrop.Files.Should().ContainSingle();
        viaScan.Files.Should().BeEmpty("the walk still honours the hidden filter");
    }

    [Fact]
    public void A_dropped_video_still_gets_its_sidecar_subtitles_detected()
    {
        // The drop must go through the same per-file construction as the scan, or the
        // "이미 한국어 자막 있음" skip stops working for dropped files.
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/movie.mp4")
            .AddFile("/videos/movie.ko.srt");

        var resolution = NewService(fileSystem).ResolveDropped(["/videos/movie.mp4"], Request());

        resolution.Files.Should().ContainSingle()
            .Which.HasKoreanExternalSubtitle.Should().BeTrue();
    }

    [Fact]
    public void A_drop_with_nothing_usable_reports_zero_files_rather_than_throwing()
    {
        var resolution = NewService(new InMemoryFileSystem()).ResolveDropped(
            ["", "/nowhere/x.txt"], Request());

        resolution.Files.Should().BeEmpty();
        resolution.IgnoredPaths.Should().Be(2);
    }

    // -----------------------------------------------------------------------
    // 자막만 있는 폴더
    // -----------------------------------------------------------------------

    [Fact]
    public void A_folder_of_nothing_but_subtitles_produces_jobs()
    {
        // 신고: "폴더에 자막만 있을 경우 자막 번역만 해주면 좋겠습니다."
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/subs")
            .AddFile("/subs/ep1.ja.srt")
            .AddFile("/subs/ep2.srt")
            .AddFile("/subs/readme.txt");

        var report = NewService(fileSystem).Scan(Request("/subs"));

        report.Files.Select(f => f.FileName).Should().BeEquivalentTo("ep1.ja.srt", "ep2.srt");
    }

    [Fact]
    public void A_subtitle_that_belongs_to_a_video_is_not_a_job_of_its_own()
    {
        // 영상 옆의 자막은 그 영상의 원본(또는 산출물)이지 별개 작업이 아니다. 별개로 잡으면
        // 같은 내용이 두 번 처리되고 결과 이름까지 겹친다.
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/videos")
            .AddFile("/videos/movie.mkv")
            .AddFile("/videos/movie.ja.srt")
            .AddFile("/videos/movie.srt");

        var report = NewService(fileSystem).Scan(Request("/videos"));

        report.Files.Select(f => f.FileName).Should().BeEquivalentTo("movie.mkv");
    }

    [Fact]
    public void A_korean_subtitle_is_never_picked_up_on_its_own()
    {
        // 이 파이프라인의 산출물이다. 잡으면 한국어를 한국어로 다시 번역한다.
        var fileSystem = new InMemoryFileSystem()
            .AddDirectory("/subs")
            .AddFile("/subs/ep1.ko.srt");

        NewService(fileSystem).Scan(Request("/subs")).Files.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/subs/ep1.sub")]
    [InlineData("/subs/ep1.idx")]
    [InlineData("/subs/ep1.smi")]
    public void Subtitle_formats_we_cannot_read_are_not_picked_up(string path)
    {
        var fileSystem = new InMemoryFileSystem().AddDirectory("/subs").AddFile(path);

        NewService(fileSystem).Scan(Request("/subs")).Files.Should().BeEmpty();
    }

    [Fact]
    public void The_scan_can_be_told_to_ignore_orphan_subtitles()
    {
        var fileSystem = new InMemoryFileSystem().AddDirectory("/subs").AddFile("/subs/ep1.ja.srt");

        var request = Request("/subs") with { IncludeSubtitleOnlyFiles = false };

        NewService(fileSystem).Scan(request).Files.Should().BeEmpty();
    }
}
