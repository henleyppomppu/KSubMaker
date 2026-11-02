namespace KSubMaker.Domain.Subtitles;

/// <summary>
/// Reads an SRT back into cues — the inverse of <see cref="SrtFormatter.Write"/>, for the subtitle
/// viewer. Lenient the way players are: a BOM, CRLF or LF, a missing or wrong index line, extra
/// blank lines and position hints after the timing are all accepted; a block whose timing line
/// cannot be read is skipped rather than failing the whole file.
/// </summary>
public static class SrtParser
{
    private const string Arrow = "-->";

    public static IReadOnlyList<SubtitleCue> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = content.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cues = new List<SubtitleCue>();
        var i = 0;

        while (i < lines.Length)
        {
            // Skip to the next timing line; the index line before it (if any) is not trusted.
            while (i < lines.Length && !lines[i].Contains(Arrow, StringComparison.Ordinal))
            {
                i++;
            }

            if (i >= lines.Length)
            {
                break;
            }

            var timing = lines[i++];
            var text = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                // A timing line with no blank line before it starts the next cue.
                if (lines[i].Contains(Arrow, StringComparison.Ordinal) && TryParseTiming(lines[i], out _, out _))
                {
                    break;
                }

                text.Add(lines[i].TrimEnd());
                i++;
            }

            // The previous text line was the next cue's index when a timing line interrupted it.
            if (i < lines.Length && text.Count > 0 && lines[i].Contains(Arrow, StringComparison.Ordinal)
                && int.TryParse(text[^1].Trim(), out _))
            {
                text.RemoveAt(text.Count - 1);
            }

            if (!TryParseTiming(timing, out var start, out var end))
            {
                continue;
            }

            cues.Add(new SubtitleCue
            {
                Index = cues.Count + 1,
                Start = start,
                End = end,
                Lines = text
            });
        }

        return cues;
    }

    private static bool TryParseTiming(string line, out double start, out double end)
    {
        start = end = 0d;
        var parts = line.Split(Arrow, 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        // "00:00:01,000 --> 00:00:02,000 X1:0 X2:0 …": position hints follow the end time.
        var endText = parts[1].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return SrtFormatter.TryParseTimestamp(parts[0], out start) && SrtFormatter.TryParseTimestamp(endText, out end);
    }
}
