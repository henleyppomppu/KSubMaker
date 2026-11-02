using FluentAssertions;
using KSubMaker.Domain.Settings;
using KSubMaker.Infrastructure.Persistence.Repositories;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Infrastructure;

/// <summary>
/// Covers "설정 저장/불러오기" against a real (in-memory) SQLite database.
///
/// Two properties matter more than any individual value: a missing row must fall back to the C#
/// default, and a garbage row must fall back to the default instead of throwing — a settings screen
/// that cannot open because one row is corrupt is worse than one showing defaults.
/// </summary>
public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly SqliteInMemoryContextFactory _factory = new();
    private readonly SettingsRepository _repository;

    public SettingsRepositoryTests() =>
        _repository = new SettingsRepository(_factory, NullLogger<SettingsRepository>.Instance);

    public void Dispose() => _factory.Dispose();

    private static AppSettings FullyPopulated() => new()
    {
        LastFolder = "/영상 보관함/2026",
        IncludeSubfolders = false,
        IncludeHiddenFolders = true,
        HideCompletedJobs = true,
        ReprocessCompleted = true,
        RetryFailedOnly = true,

        SourceLanguage = "ja",
        WhisperModel = "whisper-large-v3-turbo",
        ComputeType = KSubMaker.Domain.Settings.ComputeType.BFloat16,
        BeamSize = 2,
        VadFilter = false,
        WordTimestamps = false,
        ConditionOnPreviousText = true,
        InitialPrompt = "登場人物: 佐藤, 鈴木。",

        TranslationEngine = TranslationEngineKind.LocalLlm,
        TranslationModel = "nllb-200-distilled-1.3B",
        LlmModel = "qwen2.5-7b-instruct-q4km",
        TranslationStyle = TranslationStyle.PreserveSourceRegister,
        TranslationBatchMaxItems = 11,
        TranslationBatchMaxChars = 1_234,
        TranslationBatchMaxSeconds = 99,
        TranslationContextLines = 7,
        Glossary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Seoul"] = "서울",
            ["Han River"] = "한강",
            ["N Seoul Tower"] = "남산서울타워"
        },

        SubtitleSource = SubtitleSourcePreference.PreferAnySubtitle,
        ExistingSubtitleRule = ExistingSubtitleRule.SkipIfAnySubtitleExists,
        OutputConflictPolicy = OutputConflictPolicy.CreateNumberedCopy,
        OutputSuffix = "kor",
        MaxLinesPerCue = 3,
        MaxCharsPerLine = 19,
        MinCueDurationSeconds = 1.25d,
        MaxCueDurationSeconds = 6.75d,
        MinCueGapMilliseconds = 80,
        MergeShortCues = false,

        ProcessingStrategy = ProcessingStrategy.PipelinedParallel,
        MaxParallelCpuTasks = 6,

        // Deliberately not the default: Every_property_survives_a_save_and_load_cycle compares by
        // value, so a field left at its default here would pass the round trip without the
        // repository ever reading or writing it.
        AudioPrefetchDepth = 5,
        AutoRetryOnRecoverableError = false,
        PostQueueAction = PostQueueAction.Shutdown,
        PostQueueActionOnlyWhenAllSucceeded = false,

        CacheDirectory = "/tmp/cache",
        ModelDirectory = "/tmp/models",
        LogDirectory = "/tmp/logs",

        LogLevel = "Debug",
        MaskPathsInLogs = true,
        FakeAiMode = true
    };

    // -----------------------------------------------------------------------
    // round trip
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Every_property_survives_a_save_and_load_cycle()
    {
        var original = FullyPopulated();

        await _repository.SaveAsync(original);
        var loaded = await _repository.LoadAsync();

        loaded.Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task The_glossary_round_trips_including_korean_values()
    {
        var original = FullyPopulated();

        await _repository.SaveAsync(original);
        var loaded = await _repository.LoadAsync();

        loaded.Glossary.Should().BeEquivalentTo(original.Glossary);
        loaded.Glossary["seoul"].Should().Be("서울", "the glossary is documented as case-insensitive");
    }

    [Fact]
    public async Task An_empty_glossary_round_trips_as_empty()
    {
        var settings = new AppSettings { Glossary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };

        await _repository.SaveAsync(settings);

        (await _repository.LoadAsync()).Glossary.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(KSubMaker.Domain.Settings.ComputeType.Float32)]
    [InlineData(KSubMaker.Domain.Settings.ComputeType.Float16)]
    [InlineData(KSubMaker.Domain.Settings.ComputeType.BFloat16)]
    [InlineData(KSubMaker.Domain.Settings.ComputeType.Int8Float16)]
    [InlineData(KSubMaker.Domain.Settings.ComputeType.Int8)]
    public async Task The_nullable_compute_type_round_trips_including_null(ComputeType? computeType)
    {
        await _repository.SaveAsync(new AppSettings { ComputeType = computeType });

        (await _repository.LoadAsync()).ComputeType.Should().Be(computeType);
    }

    [Theory]
    [InlineData(TranslationStyle.Natural)]
    [InlineData(TranslationStyle.Literal)]
    [InlineData(TranslationStyle.Polite)]
    [InlineData(TranslationStyle.Casual)]
    [InlineData(TranslationStyle.PreserveSourceRegister)]
    public async Task Every_translation_style_round_trips(TranslationStyle style)
    {
        await _repository.SaveAsync(new AppSettings { TranslationStyle = style });

        (await _repository.LoadAsync()).TranslationStyle.Should().Be(style);
    }

    [Theory]
    [InlineData(SubtitleSourcePreference.AudioOnly)]
    [InlineData(SubtitleSourcePreference.PreferExternalFile)]
    [InlineData(SubtitleSourcePreference.PreferEmbeddedTrack)]
    [InlineData(SubtitleSourcePreference.PreferAnySubtitle)]
    [InlineData(SubtitleSourcePreference.AskPerFile)]
    public async Task Every_subtitle_source_round_trips(SubtitleSourcePreference source)
    {
        await _repository.SaveAsync(new AppSettings { SubtitleSource = source });

        (await _repository.LoadAsync()).SubtitleSource.Should().Be(source);
    }

    [Theory]
    [InlineData(ExistingSubtitleRule.CompleteIfKoreanExists)]
    [InlineData(ExistingSubtitleRule.SkipIfAnySubtitleExists)]
    [InlineData(ExistingSubtitleRule.ProcessAnyway)]
    public async Task Every_existing_subtitle_rule_round_trips(ExistingSubtitleRule rule)
    {
        await _repository.SaveAsync(new AppSettings { ExistingSubtitleRule = rule });

        (await _repository.LoadAsync()).ExistingSubtitleRule.Should().Be(rule);
    }

    [Theory]
    [InlineData(OutputConflictPolicy.Skip)]
    [InlineData(OutputConflictPolicy.Overwrite)]
    [InlineData(OutputConflictPolicy.CreateNumberedCopy)]
    public async Task Every_output_conflict_policy_round_trips(OutputConflictPolicy policy)
    {
        await _repository.SaveAsync(new AppSettings { OutputConflictPolicy = policy });

        (await _repository.LoadAsync()).OutputConflictPolicy.Should().Be(policy);
    }

    [Theory]
    [InlineData(ProcessingStrategy.Auto)]
    [InlineData(ProcessingStrategy.SequentialPerFile)]
    [InlineData(ProcessingStrategy.TranscribeAllThenTranslate)]
    [InlineData(ProcessingStrategy.PipelinedParallel)]
    public async Task Every_processing_strategy_round_trips(ProcessingStrategy strategy)
    {
        await _repository.SaveAsync(new AppSettings { ProcessingStrategy = strategy });

        (await _repository.LoadAsync()).ProcessingStrategy.Should().Be(strategy);
    }

    [Theory]
    [InlineData(PostQueueAction.None)]
    [InlineData(PostQueueAction.Sleep)]
    [InlineData(PostQueueAction.Hibernate)]
    [InlineData(PostQueueAction.Shutdown)]
    public async Task Every_post_queue_action_round_trips(PostQueueAction action)
    {
        await _repository.SaveAsync(new AppSettings { PostQueueAction = action });

        (await _repository.LoadAsync()).PostQueueAction.Should().Be(action);
    }

    [Theory]
    [InlineData(TranslationEngineKind.LocalTranslationModel)]
    [InlineData(TranslationEngineKind.LocalLlm)]
    [InlineData(TranslationEngineKind.Fake)]
    public async Task Every_translation_engine_round_trips(TranslationEngineKind engine)
    {
        await _repository.SaveAsync(new AppSettings { TranslationEngine = engine });

        (await _repository.LoadAsync()).TranslationEngine.Should().Be(engine);
    }

    [Fact]
    public async Task Enums_are_stored_by_name_so_reordering_a_member_cannot_corrupt_a_database()
    {
        await _repository.SaveAsync(new AppSettings { TranslationStyle = TranslationStyle.Polite });

        _factory.ReadAllSettings()[nameof(AppSettings.TranslationStyle)].Should().Be("Polite");
    }

    [Fact]
    public async Task Doubles_are_stored_with_an_invariant_decimal_point()
    {
        await _repository.SaveAsync(new AppSettings { MinCueDurationSeconds = 1.25d });

        _factory.ReadAllSettings()[nameof(AppSettings.MinCueDurationSeconds)].Should().Be("1.25");
    }

    [Fact]
    public async Task Saving_twice_overwrites_rather_than_duplicating()
    {
        await _repository.SaveAsync(new AppSettings { BeamSize = 3 });
        await _repository.SaveAsync(new AppSettings { BeamSize = 9 });

        (await _repository.LoadAsync()).BeamSize.Should().Be(9);
        _factory.ReadAllSettings()[nameof(AppSettings.BeamSize)].Should().Be("9");
    }

    [Fact]
    public async Task Rows_for_properties_that_no_longer_exist_are_pruned_on_save()
    {
        _factory.WriteRawSetting("SomeSettingFromAnOlderVersion", "42");

        await _repository.SaveAsync(new AppSettings());

        _factory.ReadAllSettings().Should().NotContainKey("SomeSettingFromAnOlderVersion");
    }

    // -----------------------------------------------------------------------
    // defaults
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_empty_database_loads_the_compiled_in_defaults()
    {
        var loaded = await _repository.LoadAsync();

        loaded.Should().BeEquivalentTo(new AppSettings());
    }

    [Fact]
    public async Task Missing_rows_fall_back_to_the_default_while_present_rows_are_honoured()
    {
        _factory.WriteRawSetting(nameof(AppSettings.BeamSize), "7");

        var loaded = await _repository.LoadAsync();

        loaded.BeamSize.Should().Be(7);
        loaded.SourceLanguage.Should().Be("auto");
        loaded.VadFilter.Should().BeTrue();
        loaded.OutputSuffix.Should().Be("ko");
        loaded.MaxCueDurationSeconds.Should().Be(7.0d);
    }

    [Fact]
    public async Task An_unknown_key_on_disk_is_ignored_instead_of_failing_the_load()
    {
        _factory.WriteRawSetting("SettingFromTheFuture", "whatever");

        var act = async () => await _repository.LoadAsync();

        await act.Should().NotThrowAsync();
        (await _repository.LoadAsync()).BeamSize.Should().Be(5);
    }

    // -----------------------------------------------------------------------
    // garbage tolerance
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("3.5")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9999999999999999999999")]
    public async Task A_garbage_integer_falls_back_to_the_default(string raw)
    {
        _factory.WriteRawSetting(nameof(AppSettings.BeamSize), raw);

        (await _repository.LoadAsync()).BeamSize.Should().Be(new AppSettings().BeamSize);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("참")]
    [InlineData("")]
    public async Task A_garbage_boolean_falls_back_to_the_default(string raw)
    {
        _factory.WriteRawSetting(nameof(AppSettings.VadFilter), raw);

        (await _repository.LoadAsync()).VadFilter.Should().BeTrue();
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    public async Task Legacy_and_hand_edited_boolean_spellings_are_tolerated(string raw, bool expected)
    {
        _factory.WriteRawSetting(nameof(AppSettings.VadFilter), raw);

        (await _repository.LoadAsync()).VadFilter.Should().Be(expected);
    }

    [Theory]
    [InlineData("not-a-double")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,25")]
    public async Task A_garbage_double_falls_back_to_the_default(string raw)
    {
        _factory.WriteRawSetting(nameof(AppSettings.MinCueDurationSeconds), raw);

        (await _repository.LoadAsync()).MinCueDurationSeconds.Should().Be(1.0d);
    }

    [Theory]
    [InlineData("NotAStyle")]
    [InlineData("42")]
    [InlineData("-1")]
    public async Task A_garbage_enum_falls_back_to_the_default(string raw)
    {
        _factory.WriteRawSetting(nameof(AppSettings.TranslationStyle), raw);

        (await _repository.LoadAsync()).TranslationStyle.Should().Be(TranslationStyle.Natural);
    }

    [Fact]
    public async Task An_enum_name_is_matched_case_insensitively()
    {
        _factory.WriteRawSetting(nameof(AppSettings.TranslationStyle), "polite");

        (await _repository.LoadAsync()).TranslationStyle.Should().Be(TranslationStyle.Polite);
    }

    [Theory]
    [InlineData("NotAComputeType")]
    [InlineData("999")]
    public async Task A_garbage_nullable_enum_falls_back_to_null(string raw)
    {
        _factory.WriteRawSetting(nameof(AppSettings.ComputeType), raw);

        (await _repository.LoadAsync()).ComputeType.Should().BeNull();
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public async Task A_garbage_glossary_falls_back_to_the_default(string raw)
    {
        _factory.WriteRawSetting(nameof(AppSettings.Glossary), raw);

        var act = async () => await _repository.LoadAsync();

        await act.Should().NotThrowAsync();
        (await _repository.LoadAsync()).Glossary.Should().BeEmpty();
    }

    [Fact]
    public async Task A_glossary_entry_with_a_blank_term_is_dropped()
    {
        _factory.WriteRawSetting(nameof(AppSettings.Glossary), """{"Seoul":"서울","   ":"버려짐"}""");

        var glossary = (await _repository.LoadAsync()).Glossary;

        glossary.Should().ContainKey("Seoul");
        glossary.Should().HaveCount(1);
    }

    [Fact]
    public async Task Several_corrupt_rows_at_once_still_load_a_usable_settings_object()
    {
        _factory.WriteRawSetting(nameof(AppSettings.BeamSize), "???");
        _factory.WriteRawSetting(nameof(AppSettings.VadFilter), "maybe");
        _factory.WriteRawSetting(nameof(AppSettings.MinCueDurationSeconds), "1,0");
        _factory.WriteRawSetting(nameof(AppSettings.TranslationStyle), "Unknown");
        _factory.WriteRawSetting(nameof(AppSettings.Glossary), "<xml/>");

        var loaded = await _repository.LoadAsync();

        loaded.Should().BeEquivalentTo(new AppSettings());
    }

    [Fact]
    public async Task Save_rejects_null()
    {
        var act = async () => await _repository.SaveAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------
    // AppSettings itself
    // -----------------------------------------------------------------------

    [Fact]
    public void Clone_produces_an_isolated_copy_including_the_glossary()
    {
        var original = FullyPopulated();

        var copy = original.Clone();
        copy.Glossary["새 항목"] = "값";
        copy.BeamSize = 42;

        original.Glossary.Should().NotContainKey("새 항목");
        original.BeamSize.Should().Be(2);
        copy.Should().BeEquivalentTo(original, options => options
            .Excluding(s => s.Glossary)
            .Excluding(s => s.BeamSize));
    }
}
