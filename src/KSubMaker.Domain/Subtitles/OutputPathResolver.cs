using KSubMaker.Domain.Settings;

namespace KSubMaker.Domain.Subtitles;

/// <summary>Outcome of applying <see cref="OutputConflictPolicy"/> to a target path.</summary>
public sealed record OutputResolution(string Path, bool ShouldWrite, bool WasRenamed, string? Reason);

/// <summary>
/// Decides where the Korean SRT goes. Pure apart from the injected existence probe, which keeps it
/// unit testable without touching the file system.
/// </summary>
public static class OutputPathResolver
{
    /// <summary>
    /// Builds the subtitle path for a video.
    ///
    /// <para>With no <paramref name="outputDirectory"/> the SRT goes next to the source:
    /// <c>{source dir}/{base name}.{suffix}.srt</c>.</para>
    ///
    /// <para>With one, the source folder tree (minus its volume root) is recreated beneath it:
    /// <c>D:\videos\showA\ep1.mkv</c> → <c>{outputDirectory}\videos\showA\ep1.{suffix}.srt</c>.
    /// Mirroring rather than flattening is what stops <c>ep1.mkv</c> in two different source folders
    /// from resolving to the same output path.</para>
    ///
    /// <para>Grafting the whole source tree under another root can push the result past the classic
    /// 260-char limit on machines without long-path support enabled. The worker creates the parent
    /// directories and surfaces a write failure as a recoverable error, so this degrades to a failed
    /// job with a clear message rather than a crash.</para>
    /// </summary>
    public static string BuildDefaultPath(string videoPath, string suffix = "ko", string? outputDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
        // 자막 파일 자체가 원본이면 언어 태그를 떼야 한다 — movie.ja.srt 의 "확장자 없는 이름"
        // 은 movie.ja 라서, 그대로 쓰면 결과가 movie.ja.ko.srt 가 된다.
        var baseName = ExternalSubtitleSelector.IsTranslatableSubtitle(videoPath)
            ? ExternalSubtitleSelector.BaseNameWithoutLanguageTag(videoPath)
            : Path.GetFileNameWithoutExtension(videoPath);

        // A blank suffix is a real choice — "write it as {video}.srt" — not a mistake to correct to
        // "ko". The user is warned in the settings hint that players will not language-detect it.
        var cleanSuffix = suffix?.Trim().Trim('.') ?? string.Empty;
        var fileName = cleanSuffix.Length == 0 ? $"{baseName}.srt" : $"{baseName}.{cleanSuffix}.srt";

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return Path.Combine(directory, fileName);
        }

        return Path.Combine(outputDirectory.Trim(), MirroredSubPath(directory), fileName);
    }

    /// <summary>
    /// The source directory with its volume root removed, so it can be grafted under another root.
    /// <c>D:\videos\showA</c> → <c>videos\showA</c>; <c>\\nas\media\showA</c> → <c>showA</c> (the
    /// share is part of the root); a relative or rootless path is returned trimmed of leading
    /// separators.
    /// </summary>
    private static string MirroredSubPath(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return string.Empty;
        }

        var root = Path.GetPathRoot(directory) ?? string.Empty;
        var rest = directory.Length > root.Length ? directory[root.Length..] : string.Empty;

        // A UNC root swallows the server and the share (\nas\media\ for \nas\media\showA), so
        // dropping it whole maps \nas\media1\showA and \nas\media2\showA onto the same output —
        // the very collision this mirroring exists to prevent. Keep them as folders instead.
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var share = root.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (share.Length > 0)
            {
                rest = Path.Combine(
                    share.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar),
                    rest.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }

        return rest.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Applies the conflict policy. <paramref name="exists"/> is normally
    /// <see cref="File.Exists(string)"/>; tests substitute a set-backed predicate.
    /// </summary>
    public static OutputResolution Resolve(
        string desiredPath,
        OutputConflictPolicy policy,
        Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredPath);
        ArgumentNullException.ThrowIfNull(exists);

        if (!exists(desiredPath))
        {
            return new OutputResolution(desiredPath, ShouldWrite: true, WasRenamed: false, Reason: null);
        }

        switch (policy)
        {
            case OutputConflictPolicy.Overwrite:
                return new OutputResolution(desiredPath, ShouldWrite: true, WasRenamed: false,
                    Reason: "기존 자막 파일을 덮어씁니다.");

            case OutputConflictPolicy.CreateNumberedCopy:
            {
                var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
                var name = Path.GetFileNameWithoutExtension(desiredPath);
                var extension = Path.GetExtension(desiredPath);

                for (var i = 2; i < 1000; i++)
                {
                    var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
                    if (!exists(candidate))
                    {
                        return new OutputResolution(candidate, ShouldWrite: true, WasRenamed: true,
                            Reason: "기존 파일이 있어 번호를 붙여 새 파일로 저장합니다.");
                    }
                }

                return new OutputResolution(desiredPath, ShouldWrite: false, WasRenamed: false,
                    Reason: "번호를 붙일 수 있는 파일명을 찾지 못했습니다.");
            }

            case OutputConflictPolicy.Skip:
            default:
                return new OutputResolution(desiredPath, ShouldWrite: false, WasRenamed: false,
                    Reason: "이미 자막 파일이 있어 건너뜁니다.");
        }
    }

    /// <summary>
    /// True when the sidecar looks like an existing Korean subtitle for the video
    /// (<c>movie.ko.srt</c>, <c>movie.kor.srt</c>, <c>movie.korean.srt</c>, <c>movie.ko-KR.srt</c>).
    /// </summary>
    public static bool LooksKorean(string subtitlePath)
    {
        var name = Path.GetFileNameWithoutExtension(subtitlePath);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var lastDot = name.LastIndexOf('.');
        if (lastDot < 0)
        {
            return false;
        }

        var tag = name[(lastDot + 1)..];

        return tag.Equals("ko", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("kor", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("korean", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("ko-kr", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("ko_kr", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("kr", StringComparison.OrdinalIgnoreCase);
    }
}
