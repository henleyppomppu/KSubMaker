using System.Globalization;
using System.Text.Json;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.Persistence.Repositories;

/// <summary>
/// Stores <see cref="AppSettings"/> as one row per property in the <c>AppSettings</c> table.
///
/// Two properties drive the whole design:
/// <list type="bullet">
/// <item>Adding a setting must never need a migration — an unknown key on disk is ignored, a missing
/// key falls back to the C# default.</item>
/// <item>Loading must never throw. A settings screen that cannot open because one row contains
/// garbage is worse than a settings screen showing defaults, so every value is parsed defensively
/// and a failure is logged rather than propagated.</item>
/// </list>
/// </summary>
public sealed class SettingsRepository(
    IDbContextFactory<KSubMakerDbContext> contextFactory,
    ILogger<SettingsRepository> logger) : ISettingsRepository
{
    private static readonly JsonSerializerOptions GlossaryJson = new()
    {
        WriteIndented = false
    };

    private readonly IDbContextFactory<KSubMakerDbContext> _contextFactory = contextFactory;
    private readonly ILogger<SettingsRepository> _logger = logger;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = new AppSettings();

        Dictionary<string, string> rows;
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            rows = await db.Settings
                .AsNoTracking()
                .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Corrupt/locked database: the application still has to start. Defaults it is.
            _logger.LogError(ex, "설정을 읽지 못해 기본값을 사용합니다.");
            return settings;
        }

        // ---- scanning --------------------------------------------------------
        settings.LastFolder = GetString(rows, nameof(settings.LastFolder), settings.LastFolder);
        settings.IncludeSubfolders = GetBool(rows, nameof(settings.IncludeSubfolders), settings.IncludeSubfolders);
        settings.IncludeHiddenFolders = GetBool(rows, nameof(settings.IncludeHiddenFolders), settings.IncludeHiddenFolders);
        settings.SkipIfKoreanSubtitleExists = GetBool(rows, nameof(settings.SkipIfKoreanSubtitleExists), settings.SkipIfKoreanSubtitleExists);
        settings.ReprocessCompleted = GetBool(rows, nameof(settings.ReprocessCompleted), settings.ReprocessCompleted);
        settings.RetryFailedOnly = GetBool(rows, nameof(settings.RetryFailedOnly), settings.RetryFailedOnly);

        // ---- speech recognition ---------------------------------------------
        settings.SourceLanguage = GetString(rows, nameof(settings.SourceLanguage), settings.SourceLanguage);
        settings.WhisperModel = GetString(rows, nameof(settings.WhisperModel), settings.WhisperModel);
        settings.ComputeType = GetNullableEnum<ComputeType>(rows, nameof(settings.ComputeType));
        settings.BeamSize = GetInt(rows, nameof(settings.BeamSize), settings.BeamSize);
        settings.VadFilter = GetBool(rows, nameof(settings.VadFilter), settings.VadFilter);
        settings.WordTimestamps = GetBool(rows, nameof(settings.WordTimestamps), settings.WordTimestamps);
        settings.ConditionOnPreviousText = GetBool(rows, nameof(settings.ConditionOnPreviousText), settings.ConditionOnPreviousText);

        // ---- translation -----------------------------------------------------
        settings.TranslationEngine = GetEnum(rows, nameof(settings.TranslationEngine), settings.TranslationEngine);
        settings.TranslationModel = GetString(rows, nameof(settings.TranslationModel), settings.TranslationModel);
        settings.LlmModel = GetString(rows, nameof(settings.LlmModel), settings.LlmModel);
        settings.TranslationStyle = GetEnum(rows, nameof(settings.TranslationStyle), settings.TranslationStyle);
        settings.SkipTranslationForSameLanguage = GetBool(rows, nameof(settings.SkipTranslationForSameLanguage), settings.SkipTranslationForSameLanguage);
        settings.TranslationBatchMaxItems = GetInt(rows, nameof(settings.TranslationBatchMaxItems), settings.TranslationBatchMaxItems);
        settings.TranslationBatchMaxChars = GetInt(rows, nameof(settings.TranslationBatchMaxChars), settings.TranslationBatchMaxChars);
        settings.TranslationBatchMaxSeconds = GetInt(rows, nameof(settings.TranslationBatchMaxSeconds), settings.TranslationBatchMaxSeconds);
        settings.TranslationContextLines = GetInt(rows, nameof(settings.TranslationContextLines), settings.TranslationContextLines);
        settings.Glossary = GetGlossary(rows, nameof(settings.Glossary), settings.Glossary);

        // ---- subtitles / output ---------------------------------------------
        settings.ExistingSubtitlePolicy = GetEnum(rows, nameof(settings.ExistingSubtitlePolicy), settings.ExistingSubtitlePolicy);
        settings.OutputConflictPolicy = GetEnum(rows, nameof(settings.OutputConflictPolicy), settings.OutputConflictPolicy);
        settings.OutputSuffix = GetString(rows, nameof(settings.OutputSuffix), settings.OutputSuffix);
        settings.MaxLinesPerCue = GetInt(rows, nameof(settings.MaxLinesPerCue), settings.MaxLinesPerCue);
        settings.MaxCharsPerLine = GetInt(rows, nameof(settings.MaxCharsPerLine), settings.MaxCharsPerLine);
        settings.MinCueDurationSeconds = GetDouble(rows, nameof(settings.MinCueDurationSeconds), settings.MinCueDurationSeconds);
        settings.MaxCueDurationSeconds = GetDouble(rows, nameof(settings.MaxCueDurationSeconds), settings.MaxCueDurationSeconds);
        settings.MinCueGapMilliseconds = GetInt(rows, nameof(settings.MinCueGapMilliseconds), settings.MinCueGapMilliseconds);
        settings.MergeShortCues = GetBool(rows, nameof(settings.MergeShortCues), settings.MergeShortCues);

        // ---- execution -------------------------------------------------------
        settings.ProcessingStrategy = GetEnum(rows, nameof(settings.ProcessingStrategy), settings.ProcessingStrategy);
        settings.MaxParallelCpuTasks = GetInt(rows, nameof(settings.MaxParallelCpuTasks), settings.MaxParallelCpuTasks);
        settings.AudioPrefetchDepth = GetInt(rows, nameof(settings.AudioPrefetchDepth), settings.AudioPrefetchDepth);
        settings.AutoRetryOnRecoverableError = GetBool(rows, nameof(settings.AutoRetryOnRecoverableError), settings.AutoRetryOnRecoverableError);

        // ---- paths -----------------------------------------------------------
        settings.CacheDirectory = GetString(rows, nameof(settings.CacheDirectory), settings.CacheDirectory);
        settings.ModelDirectory = GetString(rows, nameof(settings.ModelDirectory), settings.ModelDirectory);
        settings.LogDirectory = GetString(rows, nameof(settings.LogDirectory), settings.LogDirectory);

        // ---- diagnostics -----------------------------------------------------
        settings.LogLevel = GetString(rows, nameof(settings.LogLevel), settings.LogLevel);
        settings.MaskPathsInLogs = GetBool(rows, nameof(settings.MaskPathsInLogs), settings.MaskPathsInLogs);
        settings.FakeAiMode = GetBool(rows, nameof(settings.FakeAiMode), settings.FakeAiMode);

        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var values = Flatten(settings);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.Settings
            .ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (key, value) in values)
        {
            if (existing.TryGetValue(key, out var record))
            {
                if (!string.Equals(record.Value, value, StringComparison.Ordinal))
                {
                    record.Value = value;
                }
            }
            else
            {
                db.Settings.Add(new SettingRecord { Key = key, Value = value });
            }
        }

        // Rows for properties that no longer exist would otherwise accumulate forever across
        // versions; drop anything the current AppSettings shape does not produce.
        foreach (var (key, record) in existing)
        {
            if (!values.ContainsKey(key))
            {
                db.Settings.Remove(record);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Dictionary<string, string> Flatten(AppSettings s)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(s.LastFolder)] = s.LastFolder,
            [nameof(s.IncludeSubfolders)] = Write(s.IncludeSubfolders),
            [nameof(s.IncludeHiddenFolders)] = Write(s.IncludeHiddenFolders),
            [nameof(s.SkipIfKoreanSubtitleExists)] = Write(s.SkipIfKoreanSubtitleExists),
            [nameof(s.ReprocessCompleted)] = Write(s.ReprocessCompleted),
            [nameof(s.RetryFailedOnly)] = Write(s.RetryFailedOnly),

            [nameof(s.SourceLanguage)] = s.SourceLanguage,
            [nameof(s.WhisperModel)] = s.WhisperModel,
            // A null ComputeType means "follow the hardware recommendation" and is stored as an empty
            // string so the distinction survives a round trip.
            [nameof(s.ComputeType)] = s.ComputeType?.ToString() ?? string.Empty,
            [nameof(s.BeamSize)] = Write(s.BeamSize),
            [nameof(s.VadFilter)] = Write(s.VadFilter),
            [nameof(s.WordTimestamps)] = Write(s.WordTimestamps),
            [nameof(s.ConditionOnPreviousText)] = Write(s.ConditionOnPreviousText),

            [nameof(s.TranslationEngine)] = s.TranslationEngine.ToString(),
            [nameof(s.TranslationModel)] = s.TranslationModel,
            [nameof(s.LlmModel)] = s.LlmModel,
            [nameof(s.TranslationStyle)] = s.TranslationStyle.ToString(),
            [nameof(s.SkipTranslationForSameLanguage)] = Write(s.SkipTranslationForSameLanguage),
            [nameof(s.TranslationBatchMaxItems)] = Write(s.TranslationBatchMaxItems),
            [nameof(s.TranslationBatchMaxChars)] = Write(s.TranslationBatchMaxChars),
            [nameof(s.TranslationBatchMaxSeconds)] = Write(s.TranslationBatchMaxSeconds),
            [nameof(s.TranslationContextLines)] = Write(s.TranslationContextLines),

            [nameof(s.ExistingSubtitlePolicy)] = s.ExistingSubtitlePolicy.ToString(),
            [nameof(s.OutputConflictPolicy)] = s.OutputConflictPolicy.ToString(),
            [nameof(s.OutputSuffix)] = s.OutputSuffix,
            [nameof(s.MaxLinesPerCue)] = Write(s.MaxLinesPerCue),
            [nameof(s.MaxCharsPerLine)] = Write(s.MaxCharsPerLine),
            [nameof(s.MinCueDurationSeconds)] = Write(s.MinCueDurationSeconds),
            [nameof(s.MaxCueDurationSeconds)] = Write(s.MaxCueDurationSeconds),
            [nameof(s.MinCueGapMilliseconds)] = Write(s.MinCueGapMilliseconds),
            [nameof(s.MergeShortCues)] = Write(s.MergeShortCues),

            [nameof(s.ProcessingStrategy)] = s.ProcessingStrategy.ToString(),
            [nameof(s.MaxParallelCpuTasks)] = Write(s.MaxParallelCpuTasks),
            [nameof(s.AudioPrefetchDepth)] = Write(s.AudioPrefetchDepth),
            [nameof(s.AutoRetryOnRecoverableError)] = Write(s.AutoRetryOnRecoverableError),

            [nameof(s.CacheDirectory)] = s.CacheDirectory,
            [nameof(s.ModelDirectory)] = s.ModelDirectory,
            [nameof(s.LogDirectory)] = s.LogDirectory,

            [nameof(s.LogLevel)] = s.LogLevel,
            [nameof(s.MaskPathsInLogs)] = Write(s.MaskPathsInLogs),
            [nameof(s.FakeAiMode)] = Write(s.FakeAiMode)
        };

        // The glossary is the one non-scalar setting: a single JSON object under one key keeps the
        // "one row per property" shape instead of exploding into one row per term.
        try
        {
            values[nameof(s.Glossary)] = JsonSerializer.Serialize(s.Glossary, GlossaryJson);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(ex, "용어집을 직렬화하지 못해 빈 값으로 저장합니다.");
            values[nameof(s.Glossary)] = "{}";
        }

        return values;
    }

    // -----------------------------------------------------------------------
    // Writers — always invariant so a Korean (or any other) locale cannot turn
    // "1.5" into "1,5" and make the value unreadable on the next machine.
    // -----------------------------------------------------------------------

    private static string Write(bool value) => value ? "true" : "false";

    private static string Write(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Write(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // -----------------------------------------------------------------------
    // Readers — a present-but-unparseable row degrades to the default and logs.
    // -----------------------------------------------------------------------

    private static string GetString(IReadOnlyDictionary<string, string> rows, string key, string fallback) =>
        rows.TryGetValue(key, out var value) && value is not null ? value : fallback;

    private bool GetBool(IReadOnlyDictionary<string, string> rows, string key, bool fallback)
    {
        if (!rows.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        // Tolerate "1"/"0" written by an older build or hand-edited by a user.
        if (raw.Trim() is "1")
        {
            return true;
        }

        if (raw.Trim() is "0")
        {
            return false;
        }

        Warn(key, raw);
        return fallback;
    }

    private int GetInt(IReadOnlyDictionary<string, string> rows, string key, int fallback)
    {
        if (!rows.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        Warn(key, raw);
        return fallback;
    }

    private double GetDouble(IReadOnlyDictionary<string, string> rows, string key, double fallback)
    {
        if (!rows.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            !double.IsNaN(parsed) && !double.IsInfinity(parsed))
        {
            return parsed;
        }

        Warn(key, raw);
        return fallback;
    }

    private TEnum GetEnum<TEnum>(IReadOnlyDictionary<string, string> rows, string key, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (!rows.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        // Names only: a numeric value would be an ordinal, and ordinals are exactly what the
        // string storage exists to avoid.
        if (Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        Warn(key, raw);
        return fallback;
    }

    private TEnum? GetNullableEnum<TEnum>(IReadOnlyDictionary<string, string> rows, string key)
        where TEnum : struct, Enum
    {
        if (!rows.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        Warn(key, raw);
        return null;
    }

    private Dictionary<string, string> GetGlossary(
        IReadOnlyDictionary<string, string> rows,
        string key,
        Dictionary<string, string> fallback)
    {
        if (!rows.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw, GlossaryJson);
            if (parsed is null)
            {
                return fallback;
            }

            // AppSettings.Glossary is documented as case-insensitive; rebuild with that comparer
            // because the deserialized dictionary uses the default ordinal one.
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (term, replacement) in parsed)
            {
                if (!string.IsNullOrWhiteSpace(term) && replacement is not null)
                {
                    result[term] = replacement;
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "용어집 설정을 해석하지 못해 기본값을 사용합니다.");
            return fallback;
        }
    }

    private void Warn(string key, string raw) =>
        _logger.LogWarning("설정 '{Key}'의 값 '{Value}'을(를) 해석하지 못해 기본값을 사용합니다.", key, raw);
}
