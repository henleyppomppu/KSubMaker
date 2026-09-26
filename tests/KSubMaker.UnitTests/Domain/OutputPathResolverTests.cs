using FluentAssertions;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Covers "출력 파일 충돌 처리" and the Korean-sidecar detector.</summary>
public sealed class OutputPathResolverTests
{
    private static string Combine(params string[] parts) => Path.Combine(parts);

    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    // -----------------------------------------------------------------------
    // BuildDefaultPath
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildDefaultPath_inserts_the_suffix_before_srt()
    {
        var path = OutputPathResolver.BuildDefaultPath(Combine("videos", "movie.mkv"));

        path.Should().Be(Combine("videos", "movie.ko.srt"));
    }

    [Fact]
    public void BuildDefaultPath_honours_a_custom_suffix()
    {
        OutputPathResolver.BuildDefaultPath(Combine("videos", "movie.mkv"), "kor")
            .Should().Be(Combine("videos", "movie.kor.srt"));
    }

    [Theory]
    [InlineData(".ko")]
    [InlineData("ko.")]
    [InlineData("  ko  ")]
    public void BuildDefaultPath_trims_dots_and_whitespace_from_the_suffix(string suffix)
    {
        OutputPathResolver.BuildDefaultPath(Combine("videos", "movie.mkv"), suffix)
            .Should().Be(Combine("videos", "movie.ko.srt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BuildDefaultPath_writes_a_bare_name_for_a_blank_suffix(string? suffix)
    {
        // Blank means "no language tag" — {video}.srt — a deliberate choice, not an error to fix.
        OutputPathResolver.BuildDefaultPath(Combine("videos", "movie.mkv"), suffix!)
            .Should().Be(Combine("videos", "movie.srt"));
    }

    [Fact]
    public void BuildDefaultPath_keeps_dots_inside_the_base_name()
    {
        OutputPathResolver.BuildDefaultPath(Combine("videos", "show.S01E02.1080p.mkv"))
            .Should().Be(Combine("videos", "show.S01E02.1080p.ko.srt"));
    }

    [Fact]
    public void BuildDefaultPath_supports_korean_names_and_spaces()
    {
        OutputPathResolver.BuildDefaultPath(Combine("영상 폴더", "한국어 제목.mp4"))
            .Should().Be(Combine("영상 폴더", "한국어 제목.ko.srt"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildDefaultPath_rejects_a_blank_video_path(string? videoPath)
    {
        var act = () => OutputPathResolver.BuildDefaultPath(videoPath!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildDefaultPath_blank_output_directory_writes_next_to_the_source(string? outputDirectory)
    {
        OutputPathResolver.BuildDefaultPath(Combine("videos", "showA", "ep1.mkv"), "ko", outputDirectory)
            .Should().Be(Combine("videos", "showA", "ep1.ko.srt"));
    }

    [Fact]
    public void BuildDefaultPath_mirrors_the_source_folder_tree_under_the_output_directory()
    {
        OutputPathResolver.BuildDefaultPath(Combine("videos", "showA", "ep1.mkv"), "ko", Combine("subs"))
            .Should().Be(Combine("subs", "videos", "showA", "ep1.ko.srt"));
    }

    [Fact]
    public void BuildDefaultPath_same_name_in_two_source_folders_never_collides()
    {
        var a = OutputPathResolver.BuildDefaultPath(Combine("videos", "showA", "ep1.mkv"), "ko", Combine("subs"));
        var b = OutputPathResolver.BuildDefaultPath(Combine("videos", "showB", "ep1.mkv"), "ko", Combine("subs"));

        a.Should().NotBe(b);
    }

    [Fact]
    public void BuildDefaultPath_mirror_keeps_a_custom_suffix()
    {
        OutputPathResolver.BuildDefaultPath(Combine("videos", "ep1.mkv"), "kor", Combine("subs"))
            .Should().Be(Combine("subs", "videos", "ep1.kor.srt"));
    }

    // -----------------------------------------------------------------------
    // Resolve
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(OutputConflictPolicy.Skip)]
    [InlineData(OutputConflictPolicy.Overwrite)]
    [InlineData(OutputConflictPolicy.CreateNumberedCopy)]
    public void Resolve_writes_the_desired_path_when_nothing_exists(OutputConflictPolicy policy)
    {
        var target = Combine("videos", "movie.ko.srt");

        var resolution = OutputPathResolver.Resolve(target, policy, Existing());

        resolution.Path.Should().Be(target);
        resolution.ShouldWrite.Should().BeTrue();
        resolution.WasRenamed.Should().BeFalse();
        resolution.Reason.Should().BeNull();
    }

    [Fact]
    public void Resolve_skip_refuses_to_write_over_an_existing_file()
    {
        var target = Combine("videos", "movie.ko.srt");

        var resolution = OutputPathResolver.Resolve(target, OutputConflictPolicy.Skip, Existing(target));

        resolution.ShouldWrite.Should().BeFalse();
        resolution.Path.Should().Be(target);
        resolution.Reason.Should().Be("이미 자막 파일이 있어 건너뜁니다.");
    }

    [Fact]
    public void Resolve_overwrite_keeps_the_same_path_and_writes()
    {
        var target = Combine("videos", "movie.ko.srt");

        var resolution = OutputPathResolver.Resolve(target, OutputConflictPolicy.Overwrite, Existing(target));

        resolution.ShouldWrite.Should().BeTrue();
        resolution.Path.Should().Be(target);
        resolution.WasRenamed.Should().BeFalse();
        resolution.Reason.Should().Be("기존 자막 파일을 덮어씁니다.");
    }

    [Fact]
    public void Resolve_numbered_copy_uses_2_when_only_the_original_exists()
    {
        var target = Combine("videos", "movie.ko.srt");

        var resolution = OutputPathResolver.Resolve(target, OutputConflictPolicy.CreateNumberedCopy, Existing(target));

        resolution.ShouldWrite.Should().BeTrue();
        resolution.WasRenamed.Should().BeTrue();
        resolution.Path.Should().Be(Combine("videos", "movie.ko (2).srt"));
    }

    [Fact]
    public void Resolve_numbered_copy_skips_past_an_existing_2()
    {
        var target = Combine("videos", "movie.ko.srt");
        var second = Combine("videos", "movie.ko (2).srt");

        var resolution = OutputPathResolver.Resolve(
            target, OutputConflictPolicy.CreateNumberedCopy, Existing(target, second));

        resolution.Path.Should().Be(Combine("videos", "movie.ko (3).srt"));
        resolution.WasRenamed.Should().BeTrue();
    }

    [Fact]
    public void Resolve_numbered_copy_gives_up_after_999_attempts()
    {
        var target = Combine("videos", "movie.ko.srt");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };

        for (var i = 2; i < 1000; i++)
        {
            taken.Add(Combine("videos", $"movie.ko ({i}).srt"));
        }

        var resolution = OutputPathResolver.Resolve(
            target, OutputConflictPolicy.CreateNumberedCopy, taken.Contains);

        resolution.ShouldWrite.Should().BeFalse();
        resolution.Reason.Should().Be("번호를 붙일 수 있는 파일명을 찾지 못했습니다.");
    }

    [Fact]
    public void Resolve_rejects_a_null_existence_probe()
    {
        var act = () => OutputPathResolver.Resolve("x.srt", OutputConflictPolicy.Skip, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------
    // LooksKorean
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("movie.ko.srt")]
    [InlineData("movie.KO.srt")]
    [InlineData("movie.kor.srt")]
    [InlineData("movie.Korean.srt")]
    [InlineData("movie.ko-KR.srt")]
    [InlineData("movie.ko_kr.srt")]
    [InlineData("movie.kr.srt")]
    [InlineData("movie.ko.ass")]
    [InlineData("한국어 영화.ko.srt")]
    public void LooksKorean_accepts_every_recognised_korean_tag(string fileName)
    {
        OutputPathResolver.LooksKorean(Path.Combine("videos", fileName)).Should().BeTrue();
    }

    [Theory]
    [InlineData("movie.en.srt")]
    [InlineData("movie.jp.srt")]
    [InlineData("movie.korean-forced.srt")]
    [InlineData("movie.srt")]                 // no language tag at all
    [InlineData("movie.ko.forced.srt")]       // last tag is "forced", not the language
    [InlineData("ko.srt")]                    // "ko" is the base name, not a tag
    [InlineData("")]
    public void LooksKorean_rejects_everything_else(string fileName)
    {
        OutputPathResolver.LooksKorean(fileName.Length == 0 ? string.Empty : Path.Combine("videos", fileName))
            .Should().BeFalse();
    }

    [Fact]
    public void A_unc_share_is_kept_so_two_shares_do_not_collide()
    {
        // GetPathRoot swallows server and share (\nas\media\ for \nas\media\showA). Dropping the
        // root whole mapped \nas\media1\showA and \nas\media2\showA onto one output path — the
        // collision the mirroring exists to prevent.
        var first = OutputPathResolver.BuildDefaultPath(@"\nas\media1\showA\ep1.mkv", "ko", @"D:\out");
        var second = OutputPathResolver.BuildDefaultPath(@"\nas\media2\showA\ep1.mkv", "ko", @"D:\out");

        first.Should().NotBe(second);
        first.Should().Contain("media1");
        second.Should().Contain("media2");
    }

    [Theory]
    [InlineData(@"D:\subs\ep1.ja.srt", @"D:\subs\ep1.ko.srt")]
    [InlineData(@"D:\subs\ep1.srt", @"D:\subs\ep1.ko.srt")]
    // 마지막 한 조각만 본다. sdh 는 언어가 아니므로 그대로 남고, 그래서 eng 도 남는다 —
    // 결과가 장황해지지만 정보를 잃지는 않는다. 여러 꼬리를 걷어내려 들면 언젠가 제목의
    // 일부를 먹는다.
    [InlineData(@"D:\subs\ep1.eng.sdh.srt", @"D:\subs\ep1.eng.sdh.ko.srt")]
    [InlineData(@"D:\subs\ep1.ass", @"D:\subs\ep1.ko.srt")]
    public void A_subtitle_source_loses_its_language_tag_in_the_output_name(string source, string expected)
    {
        // movie.ja.srt 의 "확장자 없는 이름" 은 movie.ja 라서, 그대로 쓰면 movie.ja.ko.srt 가
        // 나온다. 언어처럼 보이는 꼬리만 뗀다 — sdh 는 언어가 아니므로 eng 만 떨어진다.
        OutputPathResolver.BuildDefaultPath(source).Should().Be(expected);
    }

    [Fact]
    public void A_video_keeps_every_dot_in_its_name()
    {
        // 영상 이름의 점은 언어 태그가 아니다. 자막에만 적용되는 규칙이라는 것을 고정한다.
        OutputPathResolver.BuildDefaultPath(@"D:\videos\S01.E02.mkv")
            .Should().Be(@"D:\videos\S01.E02.ko.srt");
    }
}
