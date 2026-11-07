using KSubMaker.Domain.Jobs;

namespace KSubMaker.Application.Services;

/// <summary>
/// Folder rules for the queue list. The list holds jobs from every folder the user has opened; the
/// grid shows one folder at a time (<see cref="IsInFolder"/>), and opening or refreshing a folder
/// re-checks its rows in place. Rows of other folders are left alone — switching folder only
/// switches the view, so work in one folder keeps running while the user looks at another.
///
/// A row whose video has disappeared is dropped on refresh (<see cref="DropOnRefresh"/>). A job a
/// worker is touching right now is never dropped. Pure, so the table is unit-testable.
/// </summary>
public static class QueueListReconciler
{
    /// <summary>Rows whose source video has disappeared from disk.</summary>
    public static IReadOnlyList<Job> DropOnRefresh(IEnumerable<Job> jobs, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(fileExists);

        return jobs
            .Where(j => !JobStateMachine.IsActive(j.Status) && !fileExists(j.VideoPath))
            .ToArray();
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies in <paramref name="folder"/> — directly, or anywhere
    /// below it when <paramref name="includeSubfolders"/> is set, matching what a scan of that folder
    /// would find. Case and either separator are ignored.
    /// </summary>
    public static bool IsInFolder(string? path, string? folder, bool includeSubfolders)
    {
        var root = Normalize(folder);
        var file = Normalize(path);
        if (root.Length == 0 || file.Length <= root.Length + 1)
        {
            return false;
        }

        if (!file.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return includeSubfolders || file.IndexOf('/', root.Length + 1) < 0;
    }

    /// <summary>Whether two folder strings name the same folder (case and trailing separator ignored).</summary>
    public static bool IsSameFolder(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        return left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? path) =>
        (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
}
