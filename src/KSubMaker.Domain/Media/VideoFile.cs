namespace KSubMaker.Domain.Media;

/// <summary>Extensions the scanner treats as video. Comparison is always case-insensitive.</summary>
public static class VideoExtensions
{
    public static readonly IReadOnlySet<string> Default = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".mts", ".m2ts",

        // 2026-09 사용자 신고: "재생은 되는데 KSubMaker 에서는 목록에 뜨지 않는다". 목록이
        // 짧아서였다 — 아래는 번들한 ffmpeg 가 실제로 디먹스할 수 있는 것만 골랐고
        // (`ffmpeg -demuxers` 로 확인), 읽지 못하는 컨테이너를 넣으면 목록에는 뜨고 처리에서
        // 실패하므로 지금보다 나쁜 경험이 된다.
        ".mpg", ".mpeg", ".mpe", ".m2v",   // MPEG-PS
        ".vob",                            // DVD (MPEG-PS)
        ".flv", ".f4v",                    // FLV
        ".asf",                            // ASF (wmv 의 형제)
        ".rm", ".rmvb",                    // RealMedia
        ".3gp", ".3g2",                    // QuickTime 계열
        ".ogv",                            // Ogg
        ".divx", ".mkv3d"                  // AVI/Matroska 를 다른 이름으로 쓰는 것들
    };

    /// <summary>Sidecar subtitle formats recognised when deciding whether a video already has subtitles.</summary>
    public static readonly IReadOnlySet<string> Subtitle = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".smi"
    };

    public static bool IsVideo(string path, IReadOnlySet<string>? allowed = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && (allowed ?? Default).Contains(ext);
    }
}

/// <summary>An audio stream discovered by FFprobe.</summary>
public sealed record AudioTrackInfo
{
    public required int Index { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public string? Codec { get; init; }
    public int Channels { get; init; }
    public bool IsDefault { get; init; }

    public string DisplayName =>
        $"#{Index} {(string.IsNullOrWhiteSpace(Language) ? "언어 미상" : Language)}" +
        (string.IsNullOrWhiteSpace(Title) ? string.Empty : $" · {Title}") +
        (Codec is null ? string.Empty : $" ({Codec})");
}

/// <summary>A subtitle stream embedded inside the container.</summary>
public sealed record EmbeddedSubtitleTrackInfo
{
    public required int Index { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public string? Codec { get; init; }
    public bool IsForced { get; init; }
    public bool IsDefault { get; init; }

    /// <summary>True when the language tag looks like Korean.</summary>
    public bool IsKorean =>
        Language is not null &&
        (Language.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ||
         Language.Equals("kor", StringComparison.OrdinalIgnoreCase));

    public string DisplayName =>
        $"#{Index} {(string.IsNullOrWhiteSpace(Language) ? "언어 미상" : Language)}" +
        (string.IsNullOrWhiteSpace(Title) ? string.Empty : $" · {Title}") +
        (IsForced ? " [forced]" : string.Empty);
}

/// <summary>Everything the scanner and FFprobe know about one candidate file.</summary>
public sealed record VideoFile
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }

    public double DurationSeconds { get; init; }
    public bool HasAudioTrack { get; init; }
    public IReadOnlyList<AudioTrackInfo> AudioTracks { get; init; } = [];
    public IReadOnlyList<EmbeddedSubtitleTrackInfo> SubtitleTracks { get; init; } = [];

    /// <summary>Sidecar subtitle files sharing the video's base name.</summary>
    public IReadOnlyList<string> ExternalSubtitlePaths { get; init; } = [];

    public bool HasExternalSubtitle => ExternalSubtitlePaths.Count > 0;
    public bool HasEmbeddedSubtitle => SubtitleTracks.Count > 0;

    /// <summary>True when a sidecar file looks like an existing Korean subtitle.</summary>
    public bool HasKoreanExternalSubtitle { get; init; }

    /// <summary>Set when FFprobe could not read the file.</summary>
    public string? ProbeError { get; init; }

    public bool Probed { get; init; }
}
