using FluentAssertions;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Reading SRT back for the subtitle viewer: round trip with the writer, and lenient input.</summary>
public sealed class SrtParserTests
{
    [Fact]
    public void What_the_writer_writes_reads_back_the_same()
    {
        IReadOnlyList<SubtitleCue> cues =
        [
            new() { Index = 1, Start = 1.2, End = 3.4, Lines = ["안녕하세요"] },
            new() { Index = 2, Start = 3.9, End = 6.1, Lines = ["두 줄짜리", "자막입니다"] }
        ];

        var written = SrtFormatter.ToWindowsLineEndings(SrtFormatter.Write(cues));
        var read = SrtParser.Parse("﻿" + written);

        read.Should().HaveCount(2);
        read[0].Start.Should().BeApproximately(1.2, 0.0005);
        read[1].End.Should().BeApproximately(6.1, 0.0005);
        read[1].Lines.Should().Equal("두 줄짜리", "자막입니다");
        SrtFormatter.Write(read).Should().Be(SrtFormatter.Write(cues));
    }

    [Fact]
    public void Missing_blank_lines_index_lines_and_position_hints_are_tolerated()
    {
        const string srt = "00:00:01,000 --> 00:00:02,000 X1:10 X2:20\n첫째\n2\n00:00:03.500 --> 00:00:04.000\n둘째\n\n\n";

        var read = SrtParser.Parse(srt);

        read.Select(c => c.Text).Should().Equal("첫째", "둘째");
        read[1].Start.Should().BeApproximately(3.5, 0.0005);
    }

    [Fact]
    public void A_block_with_an_unreadable_timing_is_skipped_not_fatal()
    {
        const string srt = "1\n00:00:01,000 --> 깨짐\n버려짐\n\n2\n00:00:05,000 --> 00:00:06,000\n남음\n";

        SrtParser.Parse(srt).Should().ContainSingle().Which.Text.Should().Be("남음");
    }
}
