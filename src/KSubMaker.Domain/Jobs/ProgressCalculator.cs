namespace KSubMaker.Domain.Jobs;

/// <summary>
/// Converts (stage, stage-progress) into a single monotonically increasing overall percentage.
/// Weights are rough wall-clock shares measured on a GPU run; they only need to be stable and
/// monotonic, not exact.
///
/// A job does not always run every stage — Korean audio skips 번역, a translation from an embedded
/// subtitle skips 음성 인식, prefetched audio skips 음성 추출. Those stages are passed as
/// <c>skipped</c> and the rest are scaled up to fill 100%, so the bar is not parked for a while and
/// then thrown forward by a third. Dropping a stage that has not been reached only ever moves the
/// figure forward.
/// </summary>
public static class ProgressCalculator
{
    /// <summary>Fraction of total work attributed to each stage. Must sum to 1.0.</summary>
    public static readonly IReadOnlyDictionary<JobStage, double> Weights = new Dictionary<JobStage, double>
    {
        [JobStage.Probing] = 0.02,
        [JobStage.ExtractingAudio] = 0.08,
        [JobStage.Transcribing] = 0.55,
        [JobStage.Translating] = 0.32,
        [JobStage.WritingSubtitle] = 0.03
    };

    private static readonly JobStage[] Order =
    [
        JobStage.Probing,
        JobStage.ExtractingAudio,
        JobStage.Transcribing,
        JobStage.Translating,
        JobStage.WritingSubtitle
    ];

    /// <summary>The stages in the order a job runs them.</summary>
    public static IReadOnlyList<JobStage> StageOrder => Order;

    /// <summary>Position of <paramref name="stage"/> in <see cref="StageOrder"/>, or -1 (None, Done).</summary>
    public static int IndexOf(JobStage stage) => Array.IndexOf(Order, stage);

    /// <summary>Overall percentage (0-100) for the given stage at the given stage percentage (0-100).</summary>
    public static double Overall(JobStage stage, double stageProgress) => Overall(stage, stageProgress, null);

    /// <summary>
    /// Overall percentage (0-100) for the given stage at the given stage percentage, counting only
    /// the stages this job runs. The current stage always counts, even if listed as skipped.
    /// </summary>
    public static double Overall(JobStage stage, double stageProgress, IReadOnlySet<JobStage>? skipped)
    {
        if (stage == JobStage.Done)
        {
            return 100d;
        }

        if (stage == JobStage.None)
        {
            return 0d;
        }

        bool Counts(JobStage s) => s == stage || skipped is null || !skipped.Contains(s);

        var clamped = Math.Clamp(stageProgress, 0d, 100d) / 100d;
        var completed = 0d;
        var total = 0d;
        var reached = false;

        foreach (var s in Order)
        {
            if (!Counts(s))
            {
                continue;
            }

            total += Weights[s];

            if (s == stage)
            {
                reached = true;
            }
            else if (!reached)
            {
                completed += Weights[s];
            }
        }

        if (total <= 0d)
        {
            return 0d;
        }

        var value = (completed + (Weights[stage] * clamped)) / total * 100d;
        return Math.Round(Math.Clamp(value, 0d, 100d), 2);
    }

    /// <summary>
    /// Estimates remaining wall-clock time from overall progress and elapsed time.
    /// Returns null while progress is too small to extrapolate from.
    /// </summary>
    public static TimeSpan? EstimateRemaining(double overallProgress, TimeSpan elapsed) =>
        EstimateRemaining(overallProgress, elapsed, startProgress: 0d);

    /// <summary>
    /// Estimates remaining time from the progress made <i>during this run</i>: from
    /// <paramref name="startProgress"/> to <paramref name="overallProgress"/> in
    /// <paramref name="elapsed"/>. A job that resumes at 60%, or whose audio was prefetched, starts
    /// well above zero with nothing elapsed; measuring from zero made it look nearly done.
    /// </summary>
    public static TimeSpan? EstimateRemaining(double overallProgress, TimeSpan elapsed, double startProgress)
    {
        var done = overallProgress - startProgress;
        if (done <= 0.5d || overallProgress >= 100d || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var secondsPerPercent = elapsed.TotalSeconds / done;
        var remaining = (100d - overallProgress) * secondsPerPercent;
        return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining);
    }
}
