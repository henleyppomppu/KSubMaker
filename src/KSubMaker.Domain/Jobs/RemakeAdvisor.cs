using KSubMaker.Domain.Settings;

namespace KSubMaker.Domain.Jobs;

/// <summary>
/// Answers the one question 다시 만들기 asks before it runs: would the new subtitle come out any
/// different?
///
/// <para>Both engines are close to deterministic. NLLB returns the same translation for the same
/// input, and Whisper with the same settings changed 4–7 lines out of 271 between two runs on the
/// same GPU (CLAUDE.md §5). Running again with the models unchanged therefore reuses the cached
/// result — or, with the cache cleared, recomputes nearly the same one. The models are what a user
/// changes to get a different result, so they are what is compared.</para>
///
/// <para>Pure, and in Domain for the same reason as <see cref="JobSelectionResolver"/>: the App
/// project cannot be referenced from the Linux test suite.</para>
/// </summary>
public static class RemakeAdvisor
{
    private const string Auto = "auto";

    /// <summary>
    /// True when the job was made with the speech and translation models <paramref name="settings"/>
    /// would use now. "auto" counts as unchanged: it resolves to the hardware recommendation, which is
    /// what the job was most likely made with. A job with no recorded model — marked 완료 because a
    /// subtitle was already beside it — was never made here, so it is never "unchanged".
    /// </summary>
    public static bool ModelsUnchanged(Job job, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(job.WhisperModel))
        {
            return false;
        }

        if (!SameModel(settings.WhisperModel, job.WhisperModel))
        {
            return false;
        }

        // Korean speech is not translated, and a job that recorded no engine translated nothing:
        // there is no translation model to have changed.
        if (job.TranslationEngine is not { } engine
            || string.Equals(job.DetectedLanguage, "ko", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return engine == settings.TranslationEngine && SameModel(settings.TranslationModel, job.TranslationModel);
    }

    private static bool SameModel(string? configured, string? used) =>
        string.IsNullOrWhiteSpace(configured)
        || string.Equals(configured, Auto, StringComparison.OrdinalIgnoreCase)
        || string.Equals(configured, used, StringComparison.OrdinalIgnoreCase);
}
