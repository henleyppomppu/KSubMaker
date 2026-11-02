using KSubMaker.Domain.Jobs;

namespace KSubMaker.Application.Services;

/// <summary>
/// Decides which queue rows a scan takes away. The list used to only ever grow: picking another
/// folder appended to it, and a file deleted from disk kept its row forever. The rules now are
///
/// <list type="bullet">
/// <item><b>폴더 선택</b> (a folder other than the one the list was built from): the list becomes
/// exactly what the scan found. Every other row goes.</item>
/// <item><b>목록 새로고침</b> (the same folder again): rows are re-checked in place, and a row whose
/// video no longer exists goes.</item>
/// </list>
///
/// A job a worker is touching right now is never dropped — the caller blocks both actions while
/// the queue runs, and this is the second line of defence. Pure, so the table is unit-testable.
/// </summary>
public static class QueueListReconciler
{
    /// <summary>Rows that do not belong to a freshly picked folder's scan result.</summary>
    public static IReadOnlyList<Job> DropOnFolderChange(
        IEnumerable<Job> jobs,
        IEnumerable<string> scannedVideoPaths)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(scannedVideoPaths);

        var keep = new HashSet<string>(scannedVideoPaths, StringComparer.OrdinalIgnoreCase);

        return jobs
            .Where(j => !JobStateMachine.IsActive(j.Status) && !keep.Contains(j.VideoPath))
            .ToArray();
    }

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
    /// A row that would lose work if it were dropped: it got part-way and has not finished.
    /// Removing a row also deletes its resume cache, so this is what the user is asked about.
    /// </summary>
    public static bool HasUnfinishedProgress(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return job.Status switch
        {
            JobStatus.Paused => true,
            JobStatus.Pending or JobStatus.Failed or JobStatus.Cancelled => job.OverallProgress > 0d,
            _ => false
        };
    }

    /// <summary>Whether two folder strings name the same folder (case and trailing separator ignored).</summary>
    public static bool IsSameFolder(string? a, string? b)
    {
        static string Normalize(string? path) =>
            (path ?? string.Empty).Trim().TrimEnd('\\', '/');

        var left = Normalize(a);
        var right = Normalize(b);
        return left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
