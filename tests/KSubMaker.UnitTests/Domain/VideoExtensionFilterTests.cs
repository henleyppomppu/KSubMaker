using FluentAssertions;
using KSubMaker.Domain.Media;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Covers the "확장자 필터" requirement, case-insensitively.</summary>
public sealed class VideoExtensionFilterTests
{
    public static TheoryData<string> AllVideoExtensions
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var extension in VideoExtensions.Default)
            {
                data.Add(extension);
            }

            return data;
        }
    }

    [Fact]
    public void The_original_ten_extensions_are_still_there()
    {
        // 목록은 늘어나도 되지만 줄어들면 안 된다 — 여기 있던 것이 빠지면 누군가의 라이브러리가
        // 통째로 목록에서 사라진다.
        VideoExtensions.Default.Should().Contain(
            [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".mts", ".m2ts"]);
    }

    [Theory]
    [InlineData(".mpg")]
    [InlineData(".mpeg")]
    [InlineData(".vob")]
    [InlineData(".flv")]
    [InlineData(".asf")]
    [InlineData(".rmvb")]
    [InlineData(".3gp")]
    [InlineData(".ogv")]
    public void The_containers_added_after_the_2026_09_report_are_accepted(string extension)
    {
        // "재생은 되는데 KSubMaker 에서는 목록에 뜨지 않는다" 신고로 늘린 것들. 전부 번들한
        // ffmpeg 가 디먹스할 수 있는 것만 골랐다 — 읽지 못하는 컨테이너를 넣으면 목록에는 뜨고
        // 처리에서 실패하므로 지금보다 나쁜 경험이 된다.
        VideoExtensions.IsVideo("/videos/movie" + extension).Should().BeTrue();
    }

    [Theory]
    [InlineData(".srt")]
    [InlineData(".txt")]
    [InlineData(".jpg")]
    [InlineData(".iso")]
    public void Things_that_are_not_a_video_container_stay_out(string extension)
    {
        VideoExtensions.IsVideo("/videos/movie" + extension).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(AllVideoExtensions))]
    public void Every_supported_extension_is_accepted_in_lower_case(string extension)
    {
        VideoExtensions.IsVideo("/videos/movie" + extension).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(AllVideoExtensions))]
    public void Every_supported_extension_is_accepted_in_upper_case(string extension)
    {
        VideoExtensions.IsVideo("/videos/MOVIE" + extension.ToUpperInvariant()).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(AllVideoExtensions))]
    public void Every_supported_extension_is_accepted_in_mixed_case(string extension)
    {
        var mixed = string.Concat(extension.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : c));
        VideoExtensions.IsVideo("/videos/movie" + mixed).Should().BeTrue();
    }

    [Theory]
    [InlineData("/videos/readme.txt")]
    [InlineData("/videos/poster.jpg")]
    [InlineData("/videos/subtitle.srt")]
    [InlineData("/videos/archive.mp4.zip")]
    [InlineData("/videos/movie.mp")]
    [InlineData("/videos/movie.mp42")]
    public void Non_video_extensions_are_rejected(string path)
    {
        VideoExtensions.IsVideo(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("/videos/no-extension")]
    [InlineData("noextension")]
    public void Files_without_an_extension_are_rejected(string path)
    {
        VideoExtensions.IsVideo(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_paths_are_rejected(string path)
    {
        VideoExtensions.IsVideo(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("/videos/.mp4", true)]                      // dotfile whose whole name is the extension
    [InlineData("/videos/영화 (2026) [1080p].MKV", true)]     // Korean, spaces, brackets
    [InlineData("/videos/movie.part1.mp4", true)]           // multiple dots
    [InlineData("/videos/movie.mp4.", false)]               // trailing dot: no extension
    [InlineData("/videos/tricky.mkv.txt", false)]
    [InlineData("/videos/..", false)]
    public void Weird_names_are_classified_by_the_final_extension_only(string path, bool expected)
    {
        VideoExtensions.IsVideo(path).Should().Be(expected);
    }

    [Fact]
    public void A_custom_extension_set_overrides_the_default()
    {
        var onlyMkv = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv" };

        VideoExtensions.IsVideo("/videos/movie.MKV", onlyMkv).Should().BeTrue();
        VideoExtensions.IsVideo("/videos/movie.mp4", onlyMkv).Should().BeFalse();
    }

    [Fact]
    public void Subtitle_extension_set_is_case_insensitive()
    {
        VideoExtensions.Subtitle.Should().Contain(".srt");
        VideoExtensions.Subtitle.Contains(".SRT").Should().BeTrue();
        VideoExtensions.Subtitle.Contains(".mp4").Should().BeFalse();
    }
}
