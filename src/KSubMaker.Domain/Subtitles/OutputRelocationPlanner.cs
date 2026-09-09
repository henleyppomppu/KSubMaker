namespace KSubMaker.Domain.Subtitles;

/// <summary>One subtitle file that should move from its old output path to its new one.</summary>
public readonly record struct OutputRelocation(string OldPath, string NewPath);

/// <summary>
/// Decides which already-written subtitles need to move after the output folder setting changes.
///
/// <para>Pure apart from the injected existence probe (same pattern as
/// <see cref="OutputPathResolver"/>). Each job already knows the path its subtitle was actually
/// written to (<c>Job.OutputPath</c>), so this recomputes only the *new* path via
/// <see cref="OutputPathResolver.BuildDefaultPath"/> and compares.</para>
/// </summary>
public static class OutputRelocationPlanner
{
    /// <summary>
    /// Builds the relocation list. A job is included only when its recorded output actually exists,
    /// the freshly computed path differs from it, and nothing already sits at the new path — moving
    /// never overwrites.
    /// </summary>
    public static IReadOnlyList<OutputRelocation> Plan(
        IEnumerable<(string VideoPath, string? OldOutputPath)> jobs,
        string suffix,
        string? newOutputDirectory,
        Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(exists);

        var results = new List<OutputRelocation>();

        foreach (var (videoPath, oldOutputPath) in jobs)
        {
            if (string.IsNullOrWhiteSpace(videoPath)
                || string.IsNullOrWhiteSpace(oldOutputPath)
                || !exists(oldOutputPath))
            {
                continue;
            }

            var newPath = OutputPathResolver.BuildDefaultPath(videoPath, suffix, newOutputDirectory);

            if (PathsMatch(oldOutputPath, newPath) || exists(newPath))
            {
                continue;
            }

            results.Add(new OutputRelocation(oldOutputPath, newPath));
        }

        return results;
    }

    private static bool PathsMatch(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
