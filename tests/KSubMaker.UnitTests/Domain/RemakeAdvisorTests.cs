using FluentAssertions;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

public sealed class RemakeAdvisorTests
{
    private static Job Made(string? whisper = "whisper-medium", string? language = "ja") => new()
    {
        WhisperModel = whisper,
        TranslationEngine = TranslationEngineKind.LocalTranslationModel,
        TranslationModel = "nllb-200-distilled-1.3B",
        DetectedLanguage = language
    };

    private static AppSettings Using(string whisper, string translation,
        TranslationEngineKind engine = TranslationEngineKind.LocalTranslationModel) => new()
    {
        WhisperModel = whisper,
        TranslationModel = translation,
        TranslationEngine = engine
    };

    [Theory]
    [InlineData("whisper-medium", "nllb-200-distilled-1.3B")]
    [InlineData("auto", "auto")]
    [InlineData("WHISPER-MEDIUM", "auto")]
    public void The_same_models_or_auto_count_as_unchanged(string whisper, string translation) =>
        RemakeAdvisor.ModelsUnchanged(Made(), Using(whisper, translation)).Should().BeTrue();

    [Fact]
    public void A_different_speech_model_is_a_change() =>
        RemakeAdvisor.ModelsUnchanged(Made(), Using("whisper-large-v3", "auto")).Should().BeFalse();

    [Fact]
    public void A_different_translation_model_is_a_change() =>
        RemakeAdvisor.ModelsUnchanged(Made(), Using("auto", "nllb-200-distilled-600M")).Should().BeFalse();

    [Fact]
    public void A_different_translation_engine_is_a_change() =>
        RemakeAdvisor.ModelsUnchanged(Made(), Using("auto", "auto", TranslationEngineKind.LocalLlm))
            .Should().BeFalse();

    [Fact]
    public void Korean_speech_was_not_translated_so_the_translation_model_does_not_matter() =>
        RemakeAdvisor.ModelsUnchanged(Made(language: "ko"), Using("auto", "nllb-200-distilled-600M"))
            .Should().BeTrue();

    [Fact]
    public void A_job_that_was_never_made_here_is_never_unchanged() =>
        RemakeAdvisor.ModelsUnchanged(Made(whisper: null), Using("auto", "auto")).Should().BeFalse();
}
