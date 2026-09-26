using KSubMaker.Domain.Media;

namespace KSubMaker.Domain.Subtitles;

/// <summary>The sidecar picked as a job's source text, and the language it appears to be in.</summary>
public sealed record ExternalSubtitleChoice
{
    public required string Path { get; init; }

    /// <summary>ISO-639-1, or null when the file name carries no usable language tag.</summary>
    public string? Language { get; init; }

    /// <summary>Which rule matched, for the log line that explains the pick.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Chooses which sidecar subtitle to translate when a video has several.
///
/// Lives in Domain and takes a plain list of paths so the rule is decided in one testable place —
/// the alternative, letting the worker glob the directory, would put the same ranking in Python and
/// leave the two to drift (see the C#/Python parity fixtures for how that goes).
/// </summary>
public static class ExternalSubtitleSelector
{
    /// <summary>
    /// Formats we can actually turn into cues. Deliberately narrower than
    /// <see cref="VideoExtensions.Subtitle"/>, which answers "does a subtitle exist" and so counts
    /// things we cannot read: <c>.sub</c>/<c>.idx</c> are VobSub bitmaps needing OCR, and <c>.smi</c>
    /// parses inconsistently. Those still mark a video as subtitled; they just cannot be a source.
    /// </summary>
    public static readonly IReadOnlySet<string> ReadableExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt" };

    /// <summary>Three-letter tags seen in the wild, mapped onto the two-letter codes we use.</summary>
    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpn"] = "ja", ["jap"] = "ja", ["japanese"] = "ja",
        ["eng"] = "en", ["english"] = "en",
        ["kor"] = "ko", ["korean"] = "ko",
        ["chi"] = "zh", ["zho"] = "zh", ["chinese"] = "zh",
        ["spa"] = "es", ["fra"] = "fr", ["fre"] = "fr", ["deu"] = "de", ["ger"] = "de",
        ["rus"] = "ru", ["por"] = "pt", ["ita"] = "it", ["vie"] = "vi", ["tha"] = "th",
        ["ind"] = "id", ["ara"] = "ar", ["hin"] = "hi", ["tur"] = "tr"
    };

    /// <summary>
    /// Picks the sidecar to translate, or null when none of them can serve as a source.
    ///
    /// <para>Order, highest first:</para>
    /// <list type="number">
    ///   <item>the configured source language — the user said what they are translating from</item>
    ///   <item><c>ja</c></item>
    ///   <item><c>en</c></item>
    ///   <item>any other language tag</item>
    ///   <item>no tag at all (<c>movie.srt</c>)</item>
    /// </list>
    ///
    /// <para>Korean sidecars are never chosen. <c>movie.ko.srt</c> is this pipeline's *output*;
    /// feeding it back in would translate Korean into Korean and overwrite the real subtitle with a
    /// round-trip of itself.</para>
    /// </summary>
    /// <param name="videoPath">The video the sidecars belong to.</param>
    /// <param name="candidates">Sidecar paths, typically <c>VideoFile.ExternalSubtitlePaths</c>.</param>
    /// <param name="sourceLanguage">The configured source language; <c>auto</c> or empty disables rule 1.</param>
    /// <param name="outputPath">
    /// Where this job's subtitle will be written, when that is already known. A candidate at the
    /// same path is refused: with a blank output suffix the result is <c>movie.srt</c>, which is
    /// also a legitimate *source* name, and translating a file onto itself would replace the
    /// original with a round trip of its own text. The output-conflict policy defaults to 건너뛰기
    /// and would stop it there, but 덮어쓰기 is one dropdown away and the loss is unrecoverable.
    /// </param>
    public static ExternalSubtitleChoice? Choose(
        string videoPath,
        IEnumerable<string> candidates,
        string? sourceLanguage = null,
        string? outputPath = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var baseName = Path.GetFileNameWithoutExtension(videoPath) ?? string.Empty;

        var configured = string.IsNullOrWhiteSpace(sourceLanguage) ||
                         sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : Normalize(sourceLanguage);

        ExternalSubtitleChoice? best = null;
        var bestRank = (int.MaxValue, int.MaxValue, int.MaxValue);

        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!ReadableExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            if (OutputPathResolver.LooksKorean(path))
            {
                continue;
            }

            if (outputPath is not null && SamePath(path, outputPath))
            {
                continue;
            }

            var language = ExtractLanguage(path, baseName);
            if (string.Equals(language, "ko", StringComparison.Ordinal))
            {
                // A tag LooksKorean does not recognise, e.g. "movie.ko.forced.srt".
                continue;
            }

            var (tier, reason) = Rank(language, configured);

            // Ties break on format first — .srt needs no conversion — then on the shorter name, which
            // prefers "movie.ja.srt" over "movie.ja.forced.sdh.srt", then on the path so that two
            // equally good files always resolve the same way.
            var rank = (tier, Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase) ? 0 : 1,
                        Path.GetFileName(path).Length);

            if (rank.CompareTo(bestRank) < 0 ||
                (rank == bestRank && best is not null &&
                 string.CompareOrdinal(path, best.Path) < 0))
            {
                bestRank = rank;
                best = new ExternalSubtitleChoice { Path = path, Language = language, Reason = reason };
            }
        }

        return best;
    }

    /// <summary>
    /// Whether this file can be translated on its own — i.e. is a subtitle format we can turn into
    /// cues. Used by the scan to pick up subtitles that have no video beside them.
    /// </summary>
    public static bool IsTranslatableSubtitle(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && ReadableExtensions.Contains(Path.GetExtension(path))
        && !OutputPathResolver.LooksKorean(path);

    /// <summary>
    /// The file's name with its extension and any trailing language tag removed:
    /// <c>movie.ja.srt</c> → <c>movie</c>, <c>movie.srt</c> → <c>movie</c>.
    ///
    /// <para>Without this a subtitle-only job writes <c>movie.ja.ko.srt</c>, because the "base name"
    /// of <c>movie.ja.srt</c> is <c>movie.ja</c>. Only tags that really look like a language are
    /// stripped, so <c>movie.forced.srt</c> keeps its <c>forced</c>.</para>
    /// </summary>
    public static string BaseNameWithoutLanguageTag(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;

        var lastDot = name.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return name;
        }

        return Normalize(name[(lastDot + 1)..]) is null ? name : name[..lastDot];
    }

    /// <summary>Path comparison that survives <c>..</c>, mixed separators and casing.</summary>
    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // An unrooted or malformed path cannot be the output we just built, so it is not a match.
            return false;
        }
    }

    private static (int Tier, string Reason) Rank(string? language, string? configured)
    {
        if (configured is not null && string.Equals(language, configured, StringComparison.Ordinal))
        {
            return (0, $"설정된 원본 언어({configured})와 일치");
        }

        if (string.Equals(language, "ja", StringComparison.Ordinal))
        {
            return (1, "일본어(ja)");
        }

        if (string.Equals(language, "en", StringComparison.Ordinal))
        {
            return (2, "영어(en)");
        }

        return language is null
            ? (4, "언어 표기 없음")
            : (3, $"기타 언어({language})");
    }

    /// <summary>
    /// Reads the language tag out of <c>movie.ja.forced.srt</c>. Returns null when there is none —
    /// which is a real answer, not a failure: <c>movie.srt</c> is a perfectly usable source.
    /// </summary>
    private static string? ExtractLanguage(string path, string baseName)
    {
        var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;

        if (baseName.Length > 0 && name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
        {
            name = name[baseName.Length..].TrimStart('.');
        }

        foreach (var token in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var code = Normalize(token);
            if (code is not null)
            {
                return code;
            }
        }

        return null;
    }

    /// <summary>Turns a filename token into an ISO-639-1 code, or null when it is not a language tag.</summary>
    private static string? Normalize(string token)
    {
        // "ja-JP" / "pt_BR": the region is not something this pipeline distinguishes.
        var head = token.Split('-', '_')[0];

        if (LanguageAliases.TryGetValue(head, out var mapped))
        {
            return mapped;
        }

        return head.Length == 2 && head.All(char.IsAsciiLetter)
            ? head.ToLowerInvariant()
            : null;
    }
}
