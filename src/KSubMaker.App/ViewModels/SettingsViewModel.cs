using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// Edits a working copy of <see cref="AppSettings"/>.
///
/// Nothing is written back until 저장 is pressed: the view model starts from
/// <see cref="SettingsService.Current"/> (already an isolated clone) and only calls
/// <see cref="SettingsService.SaveAsync"/> once, so a cancelled dialog can never half-apply a change
/// to a job that is already running.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly HardwareService _hardwareService;
    private readonly ModelCatalog _catalog;
    private readonly IModelManager _models;
    private readonly IDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    private AppSettings _working = new();

    /// <summary>
    /// Ids found on disk the last time <see cref="RefreshInstallStatesAsync"/> succeeded.
    ///
    /// <b>null means "not known yet"</b>, which is deliberately different from "none installed": if
    /// the model manager cannot be read, warning that every selected model is missing would be a
    /// confident lie. The save-time check treats null as "no objection".
    /// </summary>
    private IReadOnlyList<string>? _installedModelIds;

    public SettingsViewModel(
        SettingsService settingsService,
        HardwareService hardwareService,
        ModelCatalog catalog,
        IModelManager models,
        IDialogService dialogs,
        IAppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _hardwareService = hardwareService;
        _catalog = catalog;
        _models = models;
        _dialogs = dialogs;
        _paths = paths;
        _logger = logger;

        Languages = DisplayText.SupportedLanguageCodes
            .Select(code => new Option<string>(code, DisplayText.LanguageName(code)))
            .ToArray();

        WhisperModels = _catalog.OfKind(ModelKind.Whisper)
            .Select(m => new ModelOption(m.Id, m.DisplayName))
            .ToArray();

        // One list, engine included. Two independent controls let a user name an LLM while the
        // engine still said 전용 번역 모델 — a combination that reads as configured and does
        // nothing, because the run only looks at the engine. TranslationChoice owns the mapping
        // back onto AppSettings' three fields, so the wire protocol and the database are untouched.
        TranslationModels = BuildTranslationOptions();

        ComputeTypes = Enum.GetValues<ComputeType>()
            .Select(c => new Option<ComputeType>(c, HardwareRecommendationPolicy.Describe(c)))
            .ToArray();

        TranslationStyles = Enum.GetValues<TranslationStyle>()
            .Select(s => new Option<TranslationStyle>(s, DisplayText.TranslationStyleName(s)))
            .ToArray();

        ExistingSubtitlePolicies = Enum.GetValues<ExistingSubtitlePolicy>()
            .Select(p => new Option<ExistingSubtitlePolicy>(p, DisplayText.ExistingSubtitlePolicyName(p)))
            .ToArray();

        OutputConflictPolicies = Enum.GetValues<OutputConflictPolicy>()
            .Select(p => new Option<OutputConflictPolicy>(p, DisplayText.OutputConflictPolicyName(p)))
            .ToArray();

        ProcessingStrategies = Enum.GetValues<ProcessingStrategy>()
            .Select(s => new Option<ProcessingStrategy>(s, DisplayText.ProcessingStrategyName(s)))
            .ToArray();

        LogLevels = DisplayText.LogLevels
            .Select(l => new Option<string>(l, DisplayText.LogLevelName(l)))
            .ToArray();

        Load(_settingsService.Current);
    }

    /// <summary>Raised with true when 저장 succeeded, false for 취소. The window closes on this.</summary>
    public event EventHandler<bool>? CloseRequested;

    // -----------------------------------------------------------------------
    // Option lists
    // -----------------------------------------------------------------------

    public IReadOnlyList<Option<string>> Languages { get; }
    public IReadOnlyList<ModelOption> WhisperModels { get; }
    /// <summary>NLLB models, local LLMs and the diagnostic engine, in one list.</summary>
    public IReadOnlyList<ModelOption> TranslationModels { get; }
    public IReadOnlyList<Option<ComputeType>> ComputeTypes { get; }
    public IReadOnlyList<Option<TranslationStyle>> TranslationStyles { get; }
    public IReadOnlyList<Option<ExistingSubtitlePolicy>> ExistingSubtitlePolicies { get; }
    public IReadOnlyList<Option<OutputConflictPolicy>> OutputConflictPolicies { get; }
    public IReadOnlyList<Option<ProcessingStrategy>> ProcessingStrategies { get; }
    public IReadOnlyList<Option<string>> LogLevels { get; }

    // -----------------------------------------------------------------------
    // 음성 인식
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private bool _isSourceLanguageAuto = true;

    [ObservableProperty]
    private string _selectedSourceLanguage = "en";

    [ObservableProperty]
    private bool _isWhisperModelAuto = true;

    [ObservableProperty]
    private string _selectedWhisperModel = ModelIds.WhisperLargeV3Turbo;

    [ObservableProperty]
    private bool _isComputeTypeAuto = true;

    [ObservableProperty]
    private ComputeType _selectedComputeType = ComputeType.Int8Float16;

    [ObservableProperty]
    private int _beamSize = 5;

    [ObservableProperty]
    private bool _vadFilter = true;

    [ObservableProperty]
    private bool _wordTimestamps = true;

    [ObservableProperty]
    private bool _conditionOnPreviousText;

    [ObservableProperty]
    private string _recommendationText = string.Empty;

    // -----------------------------------------------------------------------
    // 번역
    // -----------------------------------------------------------------------

    /// <summary>
    /// One id covering both engines and the diagnostic entry. <see cref="TranslationChoice"/> turns
    /// it back into <see cref="AppSettings"/>' engine + model pair on save.
    /// </summary>
    [ObservableProperty]
    private string _selectedTranslationModel = TranslationChoice.AutoTranslationId;

    [ObservableProperty]
    private TranslationStyle _selectedTranslationStyle = TranslationStyle.Natural;

    [ObservableProperty]
    private bool _skipTranslationForSameLanguage = true;

    [ObservableProperty]
    private int _translationBatchMaxItems = 30;

    [ObservableProperty]
    private int _translationBatchMaxChars = 2500;

    [ObservableProperty]
    private int _translationBatchMaxSeconds = 180;

    [ObservableProperty]
    private int _translationContextLines = 3;

    // ---- 고유명사 사전 ----------------------------------------------------

    /// <summary>Live edit buffer for the glossary grid. Folded into settings only on 저장.</summary>
    public ObservableCollection<GlossaryEntryViewModel> Glossary { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveGlossaryEntryCommand))]
    private GlossaryEntryViewModel? _selectedGlossaryEntry;

    [ObservableProperty]
    private string _newGlossarySource = string.Empty;

    [ObservableProperty]
    private string _newGlossaryTarget = string.Empty;

    /// <summary>Inline validation message under the grid; empty when the last edit was accepted.</summary>
    [ObservableProperty]
    private string _glossaryMessage = string.Empty;

    // -----------------------------------------------------------------------
    // 자막 / 출력
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private ExistingSubtitlePolicy _selectedExistingSubtitlePolicy = ExistingSubtitlePolicy.AlwaysTranscribe;

    [ObservableProperty]
    private OutputConflictPolicy _selectedOutputConflictPolicy = OutputConflictPolicy.Skip;

    [ObservableProperty]
    private string _outputSuffix = "ko";

    [ObservableProperty]
    private int _maxLinesPerCue = 2;

    [ObservableProperty]
    private int _maxCharsPerLine = 22;

    [ObservableProperty]
    private double _minCueDurationSeconds = 1.0;

    [ObservableProperty]
    private double _maxCueDurationSeconds = 7.0;

    [ObservableProperty]
    private int _minCueGapMilliseconds = 50;

    [ObservableProperty]
    private bool _mergeShortCues = true;

    // -----------------------------------------------------------------------
    // 실행
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private ProcessingStrategy _selectedProcessingStrategy = ProcessingStrategy.Auto;

    [ObservableProperty]
    private int _maxParallelCpuTasks;

    [ObservableProperty]
    private int _audioPrefetchDepth = 1;

    [ObservableProperty]
    private bool _autoRetryOnRecoverableError = true;

    [ObservableProperty]
    private bool _fakeAiMode;

    [ObservableProperty]
    private bool _reprocessCompleted;

    [ObservableProperty]
    private bool _retryFailedOnly;

    // -----------------------------------------------------------------------
    // 경로 / 로그
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private string _cacheDirectory = string.Empty;

    [ObservableProperty]
    private string _modelDirectory = string.Empty;

    [ObservableProperty]
    private string _logDirectory = string.Empty;

    [ObservableProperty]
    private string _selectedLogLevel = "Information";

    [ObservableProperty]
    private bool _maskPathsInLogs;

    // -----------------------------------------------------------------------
    // 시스템 정보 (read only)
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private string _gpuName = Strings.HardwareDetectingMessage;

    [ObservableProperty]
    private string _vramText = Strings.Dash;

    [ObservableProperty]
    private string _cudaText = Strings.Dash;

    [ObservableProperty]
    private string _driverText = Strings.Dash;

    [ObservableProperty]
    private string _cpuName = Strings.Dash;

    [ObservableProperty]
    private string _coreCountText = Strings.Dash;

    [ObservableProperty]
    private string _ramText = Strings.Dash;

    [ObservableProperty]
    private string _diskText = Strings.Dash;

    [ObservableProperty]
    private string _detectionWarnings = Strings.NoDetectionWarnings;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    /// <summary>Loads the cached hardware profile without forcing a re-detect.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsBusy = true;
            var profile = await _hardwareService.GetProfileAsync(cancellationToken).ConfigureAwait(true);
            var recommendation = await _hardwareService.GetRecommendationAsync(cancellationToken).ConfigureAwait(true);
            ApplyHardware(profile, recommendation);
            await RefreshInstallStatesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.HardwareDetectFailedMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "하드웨어 정보를 불러오지 못했습니다.");
            StatusMessage = Strings.HardwareDetectFailedMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshHardwareAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsBusy = true;
            StatusMessage = Strings.HardwareDetectingMessage;

            // IncludeWorker: 새로 고침 is an explicit user action, so this is the one place that may
            // pay for starting the Python process just to get the authoritative CUDA answer.
            await _hardwareService
                .RefreshAsync(HardwareRefreshMode.IncludeWorker, cancellationToken)
                .ConfigureAwait(true);

            var profile = _hardwareService.CurrentProfile;
            var recommendation = await _hardwareService.GetRecommendationAsync(cancellationToken).ConfigureAwait(true);

            ApplyHardware(profile, recommendation);
            StatusMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.HardwareDetectFailedMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "하드웨어를 다시 확인하지 못했습니다.");
            StatusMessage = Strings.HardwareDetectFailedMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BrowseCacheDirectory()
    {
        var picked = _dialogs.PickFolder(Strings.SelectCacheFolderTitle, Fallback(CacheDirectory, _paths.CacheDirectory));
        if (picked is not null)
        {
            CacheDirectory = picked;
        }
    }

    [RelayCommand]
    private void BrowseModelDirectory()
    {
        var picked = _dialogs.PickFolder(Strings.SelectModelFolderTitle, Fallback(ModelDirectory, _paths.ModelsDirectory));
        if (picked is not null)
        {
            ModelDirectory = picked;
        }
    }

    [RelayCommand]
    private void BrowseLogDirectory()
    {
        var picked = _dialogs.PickFolder(Strings.SelectLogFolderTitle, Fallback(LogDirectory, _paths.LogsDirectory));
        if (picked is not null)
        {
            LogDirectory = picked;
        }
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsBusy = true;
            var snapshot = BuildSettings();

            if (!await ConfirmMissingModelsAsync(snapshot, cancellationToken).ConfigureAwait(true))
            {
                return;
            }

            await _settingsService.SaveAsync(snapshot, cancellationToken).ConfigureAwait(true);
            _working = snapshot;
            CloseRequested?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.Cancel;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "설정 저장에 실패했습니다.");
            var message = string.Format(CultureInfo.CurrentCulture, Strings.SettingsSaveFailedFormat, ex.Message);
            StatusMessage = message;
            _dialogs.ShowError(message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    // -----------------------------------------------------------------------
    // 설치 상태
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fills in 설치됨 / 미설치 next to every model in the three combo boxes.
    ///
    /// Best effort: a model manager that throws (an unreadable models folder, a race with a running
    /// download) leaves the labels blank rather than blocking the settings screen. The state is
    /// decoration; the save-time check below is the part that has to be right.
    /// </summary>
    private async Task RefreshInstallStatesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelStatus> statuses;

        try
        {
            statuses = await _models.GetStatusAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "모델 설치 상태를 확인하지 못했습니다.");
            return;
        }

        var installed = statuses
            .Where(s => s.Installation.Installed)
            .Select(s => s.Descriptor.Id)
            .ToArray();

        _installedModelIds = installed;

        var lookup = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);

        foreach (var option in WhisperModels.Concat(TranslationModels))
        {
            // "자동" entries and the diagnostic engine are not models, so they have no install
            // state to show. Marking the fake engine 미설치 would read as something to download.
            option.IsInstalled = IsNotAModel(option.Id) ? null : lookup.Contains(option.Id);
        }
    }

    /// <summary>
    /// Warns before saving a configuration whose models are not on disk. Returns false when the user
    /// chose to go back and fix it.
    ///
    /// The check is re-run against fresh disk state rather than the labels: the settings window can
    /// sit open while a download finishes in the 모델 관리 window behind it.
    /// </summary>
    private async Task<bool> ConfirmMissingModelsAsync(AppSettings snapshot, CancellationToken cancellationToken)
    {
        await RefreshInstallStatesAsync(cancellationToken).ConfigureAwait(true);

        if (_installedModelIds is not { } installed)
        {
            // Install state could not be read at all. Say nothing rather than accuse every model.
            return true;
        }

        var issues = ModelSelectionValidator.FindMissing(snapshot, _catalog, installed);
        if (issues.Count == 0)
        {
            return true;
        }

        _logger.LogWarning(
            "설치되지 않은 모델이 선택되었습니다: {Models}",
            string.Join(", ", issues.Select(i => i.ModelId)));

        // Confirm rather than refuse: a user who is about to download the model, or who is moving the
        // models folder in the same sitting, has a legitimate reason to save this.
        return _dialogs.Confirm(
            ModelSelectionValidator.Describe(issues) +
            Environment.NewLine + Environment.NewLine +
            Strings.SettingsSaveAnywayQuestion,
            Strings.SettingsMissingModelsTitle);
    }

    // -----------------------------------------------------------------------
    // 고유명사 사전
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds 원문 → 한국어 to the buffer.
    ///
    /// Rejected rather than clamped, unlike the numeric fields: there is no sensible correction for
    /// a blank term or a second rendering of one that is already in the table, and silently keeping
    /// one of two conflicting entries would change translations without telling anyone.
    /// </summary>
    [RelayCommand]
    private void AddGlossaryEntry()
    {
        var source = NewGlossarySource.Trim();
        var target = NewGlossaryTarget.Trim();

        var verdict = GlossaryRules.Validate(source, target, Glossary.Select(e => e.Source));
        if (verdict != GlossaryValidation.Ok)
        {
            GlossaryMessage = DescribeGlossaryProblem(verdict, source);
            return;
        }

        Glossary.Add(new GlossaryEntryViewModel(source, target));
        NewGlossarySource = string.Empty;
        NewGlossaryTarget = string.Empty;
        GlossaryMessage = string.Empty;
    }

    private static string DescribeGlossaryProblem(GlossaryValidation verdict, string source) => verdict switch
    {
        GlossaryValidation.SourceRequired => Strings.GlossarySourceRequired,
        GlossaryValidation.TargetRequired => Strings.GlossaryTargetRequired,
        GlossaryValidation.DuplicateSource =>
            string.Format(CultureInfo.CurrentCulture, Strings.GlossaryDuplicateFormat, source),
        _ => string.Empty
    };

    private bool CanRemoveGlossaryEntry => SelectedGlossaryEntry is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveGlossaryEntry))]
    private void RemoveGlossaryEntry()
    {
        if (SelectedGlossaryEntry is not { } entry)
        {
            GlossaryMessage = Strings.GlossaryNoSelection;
            return;
        }

        Glossary.Remove(entry);
        SelectedGlossaryEntry = null;
        GlossaryMessage = string.Empty;
    }

    // -----------------------------------------------------------------------
    // Mapping
    // -----------------------------------------------------------------------

    private void Load(AppSettings settings)
    {
        _working = settings;

        IsSourceLanguageAuto = string.IsNullOrWhiteSpace(settings.SourceLanguage) ||
                               settings.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase);
        SelectedSourceLanguage = IsSourceLanguageAuto ? "en" : settings.SourceLanguage;

        IsWhisperModelAuto = settings.WhisperModel.Equals("auto", StringComparison.OrdinalIgnoreCase);
        SelectedWhisperModel = IsWhisperModelAuto ? ModelIds.WhisperLargeV3Turbo : settings.WhisperModel;

        IsComputeTypeAuto = settings.ComputeType is null;
        SelectedComputeType = settings.ComputeType ?? ComputeType.Int8Float16;

        BeamSize = settings.BeamSize;
        VadFilter = settings.VadFilter;
        WordTimestamps = settings.WordTimestamps;
        ConditionOnPreviousText = settings.ConditionOnPreviousText;

        SelectedTranslationModel = Known(TranslationModels, TranslationChoice.Selected(settings));
        SelectedTranslationStyle = settings.TranslationStyle;
        SkipTranslationForSameLanguage = settings.SkipTranslationForSameLanguage;
        TranslationBatchMaxItems = settings.TranslationBatchMaxItems;
        TranslationBatchMaxChars = settings.TranslationBatchMaxChars;
        TranslationBatchMaxSeconds = settings.TranslationBatchMaxSeconds;
        TranslationContextLines = settings.TranslationContextLines;

        Glossary.Clear();
        foreach (var (source, target) in settings.Glossary.OrderBy(e => e.Key, StringComparer.CurrentCulture))
        {
            Glossary.Add(new GlossaryEntryViewModel(source, target));
        }

        SelectedGlossaryEntry = null;
        GlossaryMessage = string.Empty;

        SelectedExistingSubtitlePolicy = settings.ExistingSubtitlePolicy;
        SelectedOutputConflictPolicy = settings.OutputConflictPolicy;
        OutputSuffix = settings.OutputSuffix;
        MaxLinesPerCue = settings.MaxLinesPerCue;
        MaxCharsPerLine = settings.MaxCharsPerLine;
        MinCueDurationSeconds = settings.MinCueDurationSeconds;
        MaxCueDurationSeconds = settings.MaxCueDurationSeconds;
        MinCueGapMilliseconds = settings.MinCueGapMilliseconds;
        MergeShortCues = settings.MergeShortCues;

        SelectedProcessingStrategy = settings.ProcessingStrategy;
        MaxParallelCpuTasks = settings.MaxParallelCpuTasks;
        AudioPrefetchDepth = settings.AudioPrefetchDepth;
        AutoRetryOnRecoverableError = settings.AutoRetryOnRecoverableError;
        FakeAiMode = settings.FakeAiMode;
        ReprocessCompleted = settings.ReprocessCompleted;
        RetryFailedOnly = settings.RetryFailedOnly;

        CacheDirectory = settings.CacheDirectory;
        ModelDirectory = settings.ModelDirectory;
        LogDirectory = settings.LogDirectory;
        SelectedLogLevel = Known(DisplayText.LogLevels, settings.LogLevel, "Information");
        MaskPathsInLogs = settings.MaskPathsInLogs;
    }

    /// <summary>
    /// Produces the object handed to <see cref="SettingsService.SaveAsync"/>.
    ///
    /// Numeric fields are clamped rather than validated with an error dialog: every one of them has a
    /// sane range, and silently correcting "0 lines per cue" is friendlier than refusing to save.
    /// </summary>
    private AppSettings BuildSettings()
    {
        var settings = _working.Clone();

        settings.SourceLanguage = IsSourceLanguageAuto ? "auto" : SelectedSourceLanguage;
        settings.WhisperModel = IsWhisperModelAuto ? "auto" : SelectedWhisperModel;
        settings.ComputeType = IsComputeTypeAuto ? null : SelectedComputeType;
        settings.BeamSize = Math.Clamp(BeamSize, 1, 10);
        settings.VadFilter = VadFilter;
        settings.WordTimestamps = WordTimestamps;
        settings.ConditionOnPreviousText = ConditionOnPreviousText;

        // Engine and slot together, always. Writing one without the other is the defect this
        // dropdown exists to make unrepresentable.
        TranslationChoice.Apply(settings, SelectedTranslationModel, _catalog);
        settings.TranslationStyle = SelectedTranslationStyle;
        settings.SkipTranslationForSameLanguage = SkipTranslationForSameLanguage;
        settings.TranslationBatchMaxItems = Math.Clamp(TranslationBatchMaxItems, 1, 200);
        settings.TranslationBatchMaxChars = Math.Clamp(TranslationBatchMaxChars, 200, 20_000);
        settings.TranslationBatchMaxSeconds = Math.Clamp(TranslationBatchMaxSeconds, 10, 3_600);
        settings.TranslationContextLines = Math.Clamp(TranslationContextLines, 0, 20);
        settings.Glossary = BuildGlossary();

        settings.ExistingSubtitlePolicy = SelectedExistingSubtitlePolicy;
        settings.OutputConflictPolicy = SelectedOutputConflictPolicy;
        settings.OutputSuffix = string.IsNullOrWhiteSpace(OutputSuffix) ? "ko" : OutputSuffix.Trim().Trim('.');
        settings.MaxLinesPerCue = Math.Clamp(MaxLinesPerCue, 1, 4);
        settings.MaxCharsPerLine = Math.Clamp(MaxCharsPerLine, 8, 60);
        settings.MinCueDurationSeconds = Math.Clamp(MinCueDurationSeconds, 0.2d, 10d);
        settings.MaxCueDurationSeconds = Math.Clamp(MaxCueDurationSeconds, settings.MinCueDurationSeconds, 30d);
        settings.MinCueGapMilliseconds = Math.Clamp(MinCueGapMilliseconds, 0, 2_000);
        settings.MergeShortCues = MergeShortCues;

        settings.ProcessingStrategy = SelectedProcessingStrategy;
        settings.MaxParallelCpuTasks = Math.Clamp(MaxParallelCpuTasks, 0, 64);

        // Upper bound is disk, not correctness: each queued file holds roughly 115MB of wav per
        // hour of video until the queue reaches it. 32 is already far past the point where more
        // lookahead stops buying throughput.
        settings.AudioPrefetchDepth = Math.Clamp(AudioPrefetchDepth, 0, 32);
        settings.AutoRetryOnRecoverableError = AutoRetryOnRecoverableError;
        settings.FakeAiMode = FakeAiMode;
        settings.ReprocessCompleted = ReprocessCompleted;
        settings.RetryFailedOnly = RetryFailedOnly;

        settings.CacheDirectory = CacheDirectory.Trim();
        settings.ModelDirectory = ModelDirectory.Trim();
        settings.LogDirectory = LogDirectory.Trim();
        settings.LogLevel = SelectedLogLevel;
        settings.MaskPathsInLogs = MaskPathsInLogs;

        return settings;
    }

    /// <summary>Projects the edit buffer back onto the persisted dictionary (<see cref="GlossaryRules"/>).</summary>
    private Dictionary<string, string> BuildGlossary() =>
        GlossaryRules.Build(Glossary.Select(e => new KeyValuePair<string, string>(e.Source, e.Target)));

    private void ApplyHardware(HardwareProfile profile, HardwareRecommendation recommendation)
    {
        var gpu = profile.PrimaryGpu;

        GpuName = gpu?.Name ?? Strings.GpuNotDetected;
        VramText = gpu is null ? Strings.Dash : DisplayText.GigabytesOrDash(gpu.TotalVramGb);
        CudaText = profile.CudaAvailable
            ? $"{Strings.Available} ({profile.CudaVersion ?? Strings.Unknown})"
            : Strings.Unavailable;
        DriverText = DisplayText.OrDash(gpu?.DriverVersion);

        CpuName = DisplayText.OrDash(profile.CpuName);
        CoreCountText = profile.LogicalCoreCount.ToString(CultureInfo.CurrentCulture);
        RamText = DisplayText.GigabytesOrDash(profile.TotalRamGb);
        DiskText = DisplayText.GigabytesOrDash(profile.FreeDiskGb);

        DetectionWarnings = profile.DetectionWarnings.Count == 0
            ? Strings.NoDetectionWarnings
            : string.Join(Environment.NewLine, profile.DetectionWarnings);

        RecommendationText = recommendation.Rationale;
    }

    private IReadOnlyList<ModelOption> BuildModelOptions(ModelKind kind)
    {
        var options = new List<ModelOption>(capacity: 4)
        {
            new("auto", Strings.ModelAutoOption)
        };

        foreach (var descriptor in _catalog.OfKind(kind))
        {
            options.Add(new ModelOption(descriptor.Id, descriptor.DisplayName));
        }

        return options;
    }

    /// <summary>
    /// The single 번역 모델 list: both engines' models plus the entries that are not models.
    ///
    /// <para>Ordered so the two "자동" entries lead — they are what a new install runs — and the
    /// diagnostic engine sits last, well away from anything someone might pick by accident.</para>
    /// </summary>
    private IReadOnlyList<ModelOption> BuildTranslationOptions()
    {
        var options = new List<ModelOption>(capacity: 8)
        {
            new(TranslationChoice.AutoTranslationId, Strings.ModelAutoOption),
            new(TranslationChoice.AutoLlmId, Strings.LlmAutoOption)
        };

        foreach (var descriptor in _catalog.OfKind(ModelKind.Translation))
        {
            options.Add(new ModelOption(descriptor.Id, descriptor.DisplayName));
        }

        foreach (var descriptor in _catalog.OfKind(ModelKind.Llm))
        {
            options.Add(new ModelOption(descriptor.Id, descriptor.DisplayName));
        }

        options.Add(new ModelOption(TranslationChoice.FakeId, Strings.TranslationEngineFake));

        return options;
    }

    /// <summary>"자동" entries and the diagnostic engine — nothing to download, nothing to verify.</summary>
    private static bool IsNotAModel(string id) =>
        id.Equals(TranslationChoice.AutoTranslationId, StringComparison.OrdinalIgnoreCase) ||
        id.Equals(TranslationChoice.AutoLlmId, StringComparison.OrdinalIgnoreCase) ||
        id.Equals(TranslationChoice.FakeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Falls back to the first entry when a persisted id is no longer in the catalog.</summary>
    private static string Known(IReadOnlyList<ModelOption> options, string? value)
    {
        foreach (var option in options)
        {
            if (option.Id.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return option.Id;
            }
        }

        return options.Count > 0 ? options[0].Id : "auto";
    }

    private static string Known(IReadOnlyList<string> values, string? value, string fallback)
    {
        foreach (var candidate in values)
        {
            if (candidate.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return fallback;
    }

    private static string Fallback(string? preferred, string standby) =>
        string.IsNullOrWhiteSpace(preferred) ? standby : preferred;
}
