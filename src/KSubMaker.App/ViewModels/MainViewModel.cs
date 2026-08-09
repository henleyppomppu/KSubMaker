using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KSubMaker.App.Collections;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Media;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// The main window's view model: folder selection, the scan → enqueue → probe flow, queue control and
/// the status bar.
///
/// Threading contract for the whole class: every field touched here is only ever read or written on
/// the dispatcher thread. <see cref="JobQueueService"/> raises its events from the pump thread, so
/// both handlers do nothing but hand the payload to <see cref="OnJobChanged"/>'s coalescing buffer
/// and post a flush.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly VideoScanService _scanService;
    private readonly JobQueueService _queue;
    private readonly HardwareService _hardwareService;
    private readonly IModelManager _models;
    private readonly ModelCatalog _catalog;
    private readonly IMediaProbe _mediaProbe;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IWindowService _windows;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    private readonly Dictionary<string, JobRowViewModel> _rowsById = new(StringComparer.Ordinal);

    /// <summary>
    /// Progress arrives dozens of times a second per running job. Posting one dispatcher operation
    /// per event starves the UI thread, so events are folded into this map (last write per job wins)
    /// and a single background-priority flush drains it.
    /// </summary>
    private readonly ConcurrentDictionary<string, Job> _pendingUpdates = new(StringComparer.Ordinal);

    /// <summary>
    /// Probe results from the most recent scan, keyed by path. Kept only so the
    /// <see cref="ExistingSubtitlePolicy.AskPerFile"/> prompt can list a file's tracks without
    /// re-running FFprobe over every file it is about to ask about. Cleared and refilled per scan.
    /// </summary>
    private readonly ConcurrentDictionary<string, VideoFile> _lastScan = new(StringComparer.OrdinalIgnoreCase);

    private int _flushScheduled;
    private bool _subscribed;
    private bool _disposed;

    /// <summary>
    /// Non-zero while a bulk operation is touching many rows. 전체 선택 on a 5,000-row queue, or one
    /// flush carrying five hundred status changes, would otherwise raise thousands of
    /// CanExecuteChanged events for a single user action.
    /// </summary>
    private int _selectionNotificationDepth;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _modelPreparationCts;
    private AppSettings _settings = new();

    public MainViewModel(
        SettingsService settingsService,
        VideoScanService scanService,
        JobQueueService queue,
        HardwareService hardwareService,
        IModelManager models,
        ModelCatalog catalog,
        IMediaProbe mediaProbe,
        IDialogService dialogs,
        IShellService shell,
        IWindowService windows,
        ILogger<MainViewModel> logger)
    {
        _settingsService = settingsService;
        _scanService = scanService;
        _queue = queue;
        _hardwareService = hardwareService;
        _models = models;
        _catalog = catalog;
        _mediaProbe = mediaProbe;
        _dialogs = dialogs;
        _shell = shell;
        _windows = windows;
        _logger = logger;

        // Resolved on the UI thread by the composition root, so CurrentDispatcher is the UI one; the
        // Application lookup is preferred because it stays correct if this type is ever constructed
        // from a worker thread in a test host.
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Jobs = [];
    }

    /// <summary>Rows shown in the grid, in queue order.</summary>
    public BulkObservableCollection<JobRowViewModel> Jobs { get; }

    // -----------------------------------------------------------------------
    // Scan options
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _targetFolder = string.Empty;

    [ObservableProperty]
    private bool _includeSubfolders = true;

    [ObservableProperty]
    private bool _includeHiddenFolders;

    [ObservableProperty]
    private bool _skipIfKoreanSubtitleExists = true;

    // -----------------------------------------------------------------------
    // Queue / status
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueStateText))]
    [NotifyPropertyChangedFor(nameof(IsQueueRunning))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCompletedCommand))]
    private QueueState _queueStatus = QueueState.Idle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isScanning;

    /// <summary>
    /// True while the pre-start model download is running. It gates <see cref="StartCommand"/> the
    /// same way <see cref="IsScanning"/> does — the download is part of starting, so a second click
    /// must not queue a second one.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelModelPreparationCommand))]
    private bool _isPreparingModels;

    /// <summary>0–100 across the whole set of models being fetched, for the status-bar bar.</summary>
    [ObservableProperty]
    private double _modelPreparationPercent;

    [ObservableProperty]
    private string _statusMessage = Strings.ReadyMessage;

    [ObservableProperty]
    private string _gpuSummary = Strings.HardwareDetectingMessage;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _runningCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _failedCount;

    /// <summary>
    /// The highlighted row. Feeds the selection-driven commands' CanExecute, so every one of them is
    /// re-evaluated when the user clicks a different row.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelJobsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseSubtitleSourceCommand))]
    private JobRowViewModel? _selectedJob;

    public string QueueStateText => DisplayText.QueueStateName(QueueStatus);

    public bool IsQueueRunning => QueueStatus is QueueState.Running or QueueState.Pausing or QueueState.Stopping;

    private bool CanScan =>
        !IsScanning && !IsQueueRunning && !IsPreparingModels && !string.IsNullOrWhiteSpace(TargetFolder);

    private bool CanCancelScan => IsScanning;

    private bool CanStart => !IsQueueRunning && !IsScanning && !IsPreparingModels;

    private bool CanCancelModelPreparation => IsPreparingModels;

    private bool CanPause => QueueStatus == QueueState.Running;

    private bool CanStop => IsQueueRunning;

    private bool CanRemoveCompleted => !IsQueueRunning;

    // The four selection-driven commands answer "is anything I could act on selected right now?".
    // A greyed-out button tells the user that before the click; the alert inside each command is the
    // backstop for a CanExecute that has gone stale.

    private bool CanRetry => Resolve(JobAction.Retry).IsOk;

    private bool CanCancelJobs => Resolve(JobAction.Cancel).IsOk;

    private bool CanRemoveSelected => Resolve(JobAction.Remove).IsOk;

    private bool CanChooseSubtitleSource => ResolveSingle(JobAction.ChooseSubtitleSource).IsOk;

    // -----------------------------------------------------------------------
    // Lifetime
    // -----------------------------------------------------------------------

    /// <summary>
    /// Subscribes to the queue and fills the grid from whatever <see cref="JobQueueService.LoadAsync"/>
    /// restored. Called once, from the dispatcher thread, after the window is on screen.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_subscribed)
        {
            _queue.JobChanged += OnJobChanged;
            _queue.StateChanged += OnQueueStateChanged;
            _settingsService.SettingsChanged += OnSettingsChanged;
            _hardwareService.ProfileChanged += OnHardwareProfileChanged;
            _subscribed = true;
        }

        _settings = _settingsService.Current;
        ApplySettings(_settings);

        RebuildRows(_queue.Jobs);
        QueueStatus = _queue.State;
        StatusMessage = Strings.ReadyMessage;

        try
        {
            var profile = await _hardwareService.GetProfileAsync(cancellationToken).ConfigureAwait(true);
            GpuSummary = FormatGpuSummary(profile);
        }
        catch (OperationCanceledException)
        {
            GpuSummary = Strings.HardwareDetectFailedMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "하드웨어 정보를 확인하지 못했습니다.");
            GpuSummary = Strings.HardwareDetectFailedMessage;
        }
    }

    /// <summary>
    /// Stops the queue and detaches every event handler. Awaited by the window before it closes so
    /// the Python worker is gone before the process starts tearing the container down.
    /// </summary>
    public async Task ShutdownAsync()
    {
        CancelScan();

        Unsubscribe();

        try
        {
            await _queue.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "작업 큐를 정리하는 중 오류가 발생했습니다.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unsubscribe();

        foreach (var row in _rowsById.Values)
        {
            DetachRow(row);
        }

        var cts = Interlocked.Exchange(ref _scanCts, null);
        cts?.Dispose();
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        _queue.JobChanged -= OnJobChanged;
        _queue.StateChanged -= OnQueueStateChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _hardwareService.ProfileChanged -= OnHardwareProfileChanged;
    }

    // -----------------------------------------------------------------------
    // Commands: folder + scan
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void BrowseFolder()
    {
        var picked = _dialogs.PickFolder(Strings.SelectFolderDialogTitle, TargetFolder);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            TargetFolder = picked;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (IsQueueRunning)
        {
            StatusMessage = Strings.QueueBusyCannotScan;
            _dialogs.ShowWarning(Strings.QueueBusyCannotScan);
            return;
        }

        var folder = TargetFolder.Trim();

        if (string.IsNullOrWhiteSpace(folder))
        {
            _dialogs.ShowWarning(Strings.ScanFolderNotSelected);
            return;
        }

        if (!Directory.Exists(folder))
        {
            _dialogs.ShowWarning(Strings.ScanFolderMissing);
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _scanCts, cts)?.Dispose();
        IsScanning = true;

        try
        {
            var settings = await PersistScanOptionsAsync(folder, cts.Token).ConfigureAwait(true);

            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.ScanningFolderFormat, folder);

            var request = new ScanRequest
            {
                RootFolder = folder,
                IncludeSubfolders = IncludeSubfolders,
                IncludeHiddenFolders = IncludeHiddenFolders
            };

            // ScanAsync already hops onto the thread pool; awaiting it keeps the UI live.
            var report = await _scanService.ScanAsync(request, cts.Token).ConfigureAwait(true);

            if (report.Files.Count == 0)
            {
                StatusMessage = Strings.ScanNoFilesFound;
                _dialogs.ShowInformation(Strings.ScanNoFilesFound);
                return;
            }

            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                Strings.ScanCompletedFormat,
                report.Files.Count,
                report.DirectoriesVisited,
                report.Elapsed.TotalSeconds);

            var results = await _queue.EnqueueAsync(report.Files, settings, cts.Token).ConfigureAwait(true);

            await ProbeNewlyQueuedAsync(report.Files, results, cts.Token).ConfigureAwait(true);

            ShowEnqueueSummary(results);

            // After the summary: the prompt is modal and per file, so the user should already know
            // how many files the scan produced before being asked about them one at a time.
            await AskPerFileIfRequestedAsync(settings, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.ScanCancelledMessage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "폴더 검색 중 오류가 발생했습니다.");
            var message = string.Format(CultureInfo.CurrentCulture, Strings.ScanFailedFormat, ex.Message);
            StatusMessage = message;
            _dialogs.ShowError(message);
        }
        finally
        {
            IsScanning = false;
            Interlocked.CompareExchange(ref _scanCts, null, cts);
            cts.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        try
        {
            _scanCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The scan finished between the button press and here; nothing to cancel.
        }
    }

    /// <summary>
    /// Runs FFprobe over the files that actually became (or were reset to) pending jobs.
    ///
    /// FFprobe is a short-lived CPU/IO process, so it is safe to run several at once — unlike every
    /// GPU stage, which the queue keeps strictly serialised. The degree is deliberately half the core
    /// count so a scan does not make the machine unusable.
    /// </summary>
    private async Task ProbeNewlyQueuedAsync(
        IReadOnlyList<VideoFile> files,
        IReadOnlyList<EnqueueResult> results,
        CancellationToken cancellationToken)
    {
        _lastScan.Clear();

        var toProbe = new List<VideoFile>(files.Count);
        var count = Math.Min(files.Count, results.Count);

        for (var i = 0; i < count; i++)
        {
            if (results[i].Decision is EnqueueDecision.Created or EnqueueDecision.Requeued)
            {
                toProbe.Add(files[i]);
            }
        }

        if (toProbe.Count == 0)
        {
            return;
        }

        var total = toProbe.Count;
        var completed = 0;

        // Created on the dispatcher thread, so Report() marshals back automatically.
        var progress = new Progress<int>(done =>
            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.ProbingFilesFormat, done, total));

        var reporter = (IProgress<int>)progress;
        reporter.Report(0);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(toProbe, options, async (file, token) =>
        {
            try
            {
                var probed = await _mediaProbe.ProbeAsync(file, token).ConfigureAwait(false);
                await _queue.ApplyProbeAsync(probed, token).ConfigureAwait(false);

                // Written from the parallel loop, hence the concurrent dictionary.
                _lastScan[probed.FullPath] = probed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable file must not abandon the probe pass for the other 4,999. The job is
                // still queued; the pipeline will report the real error when it tries to process it.
                _logger.LogWarning(ex, "파일 정보를 확인하지 못했습니다: {Path}", file.FullPath);
            }

            var done = Interlocked.Increment(ref completed);

            // One status update per ten files: the point is progress, not a per-file readout.
            if (done == total || done % 10 == 0)
            {
                reporter.Report(done);
            }
        }).ConfigureAwait(true);
    }

    private void ShowEnqueueSummary(IReadOnlyList<EnqueueResult> results)
    {
        var created = 0;
        var requeued = 0;
        var alreadyDone = 0;
        var unchanged = 0;
        var skipped = 0;

        foreach (var result in results)
        {
            switch (result.Decision)
            {
                case EnqueueDecision.Created: created++; break;
                case EnqueueDecision.Requeued: requeued++; break;
                case EnqueueDecision.AlreadyDone: alreadyDone++; break;
                case EnqueueDecision.Unchanged: unchanged++; break;
                default: skipped++; break;
            }
        }

        var summary = string.Format(
            CultureInfo.CurrentCulture,
            Strings.EnqueueSummaryFormat,
            created, requeued, alreadyDone, unchanged, skipped);

        StatusMessage = summary;
        _dialogs.ShowInformation(summary, Strings.EnqueueSummaryTitle);
    }

    private async Task<AppSettings> PersistScanOptionsAsync(string folder, CancellationToken cancellationToken)
    {
        var settings = _settingsService.Current;
        settings.LastFolder = folder;
        settings.IncludeSubfolders = IncludeSubfolders;
        settings.IncludeHiddenFolders = IncludeHiddenFolders;
        settings.SkipIfKoreanSubtitleExists = SkipIfKoreanSubtitleExists;

        try
        {
            await _settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Remembering the folder is a convenience; failing to remember it must not stop the scan.
            _logger.LogWarning(ex, "검색 옵션을 저장하지 못했습니다.");
        }

        _settings = settings;
        return settings;
    }

    // -----------------------------------------------------------------------
    // Commands: queue control
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var anyChecked = CheckedIds().Count > 0;

        var selection = JobSelectionResolver.ResolveStart(Candidates());
        if (!Accept(selection, JobAction.Start))
        {
            return;
        }

        _settings = _settingsService.Current;

        if (_settings.TestDurationSeconds > 0)
        {
            System.Windows.MessageBox.Show(
                $"테스트 모드가 활성화되어 있습니다.\n\n영상의 앞부분({_settings.TestDurationSeconds}초)만 자막 작업이 진행되고 완료 처리됩니다.\n영상 전체를 처리하시려면 [설정] ➔ [실행] 탭에서 '테스트용 앞부분만 처리' 값을 0으로 변경해 주세요.",
                "테스트 실행 안내",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        if (await EnsureModelsAsync().ConfigureAwait(true) is not { } runSettings)
        {
            return;
        }

        // Only pass a restriction when the user actually checked rows; null means "everything pending".
        await _queue.StartAsync(runSettings, anyChecked ? selection.Ids : null).ConfigureAwait(true);
        StatusMessage = Strings.StartedMessage;
    }

    /// <summary>
    /// Makes sure every model this run will load is on disk, offering to fetch what is missing, and
    /// returns the settings the queue should actually run under. Null means the queue must not start.
    ///
    /// <para>Why here and not in the worker: the worker discovers a missing model only when it tries
    /// to load one, which is after ffmpeg has demuxed the audio. On a queue of a few hundred files
    /// that is minutes of disk churn per file before an error naming a model the user never chose.
    /// The whole point of this check is to happen before any of that.</para>
    ///
    /// <para><b>It returns settings rather than a bool</b> because the resolution has to reach the
    /// run. <c>"auto"</c> is sent to the worker verbatim today, and the worker maps it to a
    /// hardcoded <c>whisper-small</c> — so downloading the recommended large-v3 here and then
    /// starting would fail looking for a model nobody asked for. The concrete ids this method
    /// checked and downloaded are written into the snapshot the queue is handed, so what runs is
    /// exactly what the user was shown.</para>
    /// </summary>
    private async Task<AppSettings?> EnsureModelsAsync()
    {
        IReadOnlyList<ModelRequirement> required;

        try
        {
            // Both are best-effort reads; a failure here must not become "you cannot start".
            var recommendation = await _hardwareService.GetRecommendationAsync().ConfigureAwait(true);
            var statuses = await _models.GetStatusAsync().ConfigureAwait(true);
            var installed = statuses.Where(s => s.Installation.Installed).Select(s => s.Descriptor.Id);

            required = ModelSelectionValidator.Resolve(_settings, _catalog, recommendation, installed);
        }
        catch (Exception ex)
        {
            // Let the run proceed unchanged: the worker still reports a missing model, just later
            // and worse. Blocking the queue because a status query failed is the bigger regression.
            _logger.LogWarning(ex, "시작 전 모델 확인에 실패했습니다. 확인 없이 진행합니다.");
            return _settings;
        }

        if (_settings.FakeAiMode)
        {
            // Fake AI loads nothing, and Resolve returns empty for it. Nothing to check or rewrite.
            return _settings;
        }

        if (required.Count == 0)
        {
            // Reaching here means neither slot resolved to a catalog entry — a recommendation naming
            // an id the catalog does not have. Rare, but guessing a default instead is precisely the
            // defect this check exists to prevent, so say so and let the user choose.
            _dialogs.ShowWarning(Strings.ModelPrepareUnresolvedMessage, Strings.ModelPrepareTitle);
            StatusMessage = Strings.ModelPrepareUnresolvedMessage;
            return null;
        }

        var missing = required.Where(r => !r.IsInstalled).ToList();

        if (missing.Count > 0)
        {
            if (!_dialogs.Confirm(DescribeMissingModels(missing), Strings.ModelPrepareTitle))
            {
                StatusMessage = Strings.ModelPrepareDeclinedMessage;
                return null;
            }

            if (!await DownloadModelsAsync(missing).ConfigureAwait(true))
            {
                return null;
            }
        }

        return WithResolvedModels(_settings, required);
    }

    /// <summary>
    /// A copy of <paramref name="settings"/> with every <c>"auto"</c> slot replaced by the id
    /// <see cref="ModelSelectionValidator.Resolve"/> chose. A copy, not a mutation: this is the
    /// run's snapshot, not a settings change, and it must not be written back to disk.
    /// </summary>
    private static AppSettings WithResolvedModels(
        AppSettings settings,
        IReadOnlyList<ModelRequirement> required)
    {
        var resolved = settings.Clone();

        foreach (var requirement in required)
        {
            switch (requirement.Kind)
            {
                case ModelKind.Whisper:
                    resolved.WhisperModel = requirement.ModelId;
                    break;
                case ModelKind.Translation:
                    resolved.TranslationModel = requirement.ModelId;
                    break;
                case ModelKind.Llm:
                    resolved.LlmModel = requirement.ModelId;
                    break;
            }
        }

        return resolved;
    }

    private static string DescribeMissingModels(IReadOnlyList<ModelRequirement> missing)
    {
        var builder = new System.Text.StringBuilder(Strings.ModelPrepareHeader);

        foreach (var requirement in missing)
        {
            builder.Append(Environment.NewLine)
                   .AppendFormat(
                       CultureInfo.CurrentCulture,
                       Strings.ModelPrepareItemFormat,
                       ModelSelectionValidator.DescribeKind(requirement.Kind),
                       requirement.DisplayName,
                       DisplayText.Bytes(requirement.ApproxSizeBytes));

            if (requirement.FromRecommendation)
            {
                builder.Append(Strings.ModelPrepareRecommendedSuffix);
            }
        }

        return builder
            .Append(Environment.NewLine)
            .Append(Environment.NewLine)
            .AppendFormat(
                CultureInfo.CurrentCulture,
                Strings.ModelPrepareQuestionFormat,
                DisplayText.Bytes(missing.Sum(r => r.ApproxSizeBytes)))
            .ToString();
    }

    /// <summary>Fetches each missing model in turn, reporting into the status bar. False if it did not finish.</summary>
    private async Task<bool> DownloadModelsAsync(IReadOnlyList<ModelRequirement> missing)
    {
        var cts = new CancellationTokenSource();
        _modelPreparationCts = cts;
        IsPreparingModels = true;
        ModelPreparationPercent = 0d;

        var current = string.Empty;

        try
        {
            for (var index = 0; index < missing.Count; index++)
            {
                var requirement = missing[index];
                current = requirement.DisplayName;

                // Percent spans the whole set, so the bar does not snap back to 0 per model.
                var completed = index;
                var progress = new Progress<ModelDownloadProgress>(p =>
                {
                    // Progress<T> posts to the UI context, so a callback queued during the last
                    // download can still run after this method has finished — and would overwrite
                    // the completion message with a stale "내려받는 중". Same gate as the queue's
                    // InlineProgress: once the preparation is over, late reports are dropped.
                    if (!IsPreparingModels)
                    {
                        return;
                    }

                    ModelPreparationPercent = (completed + p.Percent / 100d) / missing.Count * 100d;
                    StatusMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        Strings.ModelPrepareProgressFormat,
                        index + 1, missing.Count, requirement.DisplayName, p.Percent);
                });

                await _models.DownloadAsync(requirement.ModelId, progress, cts.Token).ConfigureAwait(true);
                _logger.LogInformation("시작 전 모델 다운로드를 완료했습니다: {ModelId}", requirement.ModelId);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.ModelPrepareCancelledMessage;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "시작 전 모델 다운로드에 실패했습니다: {ModelId}", current);

            var message = string.Format(
                CultureInfo.CurrentCulture, Strings.ModelPrepareFailedFormat, current, ex.Message);
            _dialogs.ShowError(message, Strings.ModelPrepareTitle);
            StatusMessage = message;
            return false;
        }
        finally
        {
            IsPreparingModels = false;
            ModelPreparationPercent = 0d;
            _modelPreparationCts = null;
            cts.Dispose();
        }

        StatusMessage = Strings.ModelPrepareCompletedMessage;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanCancelModelPreparation))]
    private void CancelModelPreparation()
    {
        // Deliberately not disposed here: DownloadModelsAsync owns it and disposes in its finally.
        // Cancelling a disposed source is the defect that cost eight queue tests a 10s timeout.
        _modelPreparationCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _queue.Pause();
        StatusMessage = Strings.PauseRequestedMessage;
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (!_dialogs.Confirm(Strings.ConfirmStopRunning))
        {
            return;
        }

        await _queue.StopAsync().ConfigureAwait(true);
        StatusMessage = Strings.StopRequestedMessage;
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        var selection = Resolve(JobAction.Retry);
        if (!Accept(selection, JobAction.Retry))
        {
            return;
        }

        await _queue.RetryAsync(selection.Ids).ConfigureAwait(true);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.RetryDoneFormat, selection.Count);
    }

    [RelayCommand(CanExecute = nameof(CanCancelJobs))]
    private async Task CancelJobsAsync()
    {
        var selection = Resolve(JobAction.Cancel);
        if (!Accept(selection, JobAction.Cancel))
        {
            return;
        }

        await _queue.CancelAsync(selection.Ids).ConfigureAwait(true);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.CancelDoneFormat, selection.Count);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveCompleted))]
    private async Task RemoveCompletedAsync()
    {
        if (!Jobs.Any(j => j.Status == JobStatus.Completed))
        {
            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.RemoveCompletedDoneFormat, 0);
            return;
        }

        if (!_dialogs.Confirm(Strings.RemoveCompletedConfirm))
        {
            return;
        }

        var result = await _queue.RemoveCompletedAsync().ConfigureAwait(true);
        RemoveRows(result.Removed);

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            Strings.RemoveCompletedDoneFormat,
            result.RemovedCount);
    }

    /// <summary>
    /// 선택 항목 제거: drops the selected jobs and the cache they own.
    ///
    /// Confirmed first, because the cache delete is not undoable and because the question a user
    /// actually has — "does this delete my video or the subtitle I already got?" — has to be answered
    /// before the click, not after. A job the pump is still running is cancelled and waited for by
    /// <see cref="JobQueueService.RemoveAsync"/>; anything that will not stop is reported instead of
    /// being torn out mid-flight.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveSelectedAsync(CancellationToken cancellationToken)
    {
        var selection = Resolve(JobAction.Remove);
        if (!Accept(selection, JobAction.Remove))
        {
            return;
        }

        var question = string.Format(
            CultureInfo.CurrentCulture,
            Strings.RemoveSelectedConfirmFormat,
            selection.Count);

        if (!_dialogs.Confirm(question, Strings.RemoveSelectedConfirmTitle))
        {
            return;
        }

        JobRemovalResult result;
        try
        {
            result = await _queue.RemoveAsync(selection.Ids, cancellationToken: cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RemoveRows(result.Removed);

        if (result.SkippedCount == 0)
        {
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                Strings.RemoveSelectedDoneFormat,
                result.RemovedCount);
            return;
        }

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            Strings.RemoveSelectedPartialFormat,
            result.RemovedCount,
            result.SkippedCount);

        _dialogs.ShowWarning(Strings.RemoveSelectedRunningSkipped, Strings.RemoveSelectedConfirmTitle);
    }

    // -----------------------------------------------------------------------
    // Commands: 자막 원본 (per-file source override)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Lets the user say what this one file should be translated from.
    ///
    /// The container is re-probed here rather than cached on the job: a track list is a dozen strings
    /// per file, storing it for a 5,000-file queue to serve one dialog would be a poor trade, and
    /// FFprobe on a local file costs milliseconds.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChooseSubtitleSource))]
    private async Task ChooseSubtitleSourceAsync(CancellationToken cancellationToken)
    {
        var selection = ResolveSingle(JobAction.ChooseSubtitleSource);
        if (!Accept(selection, JobAction.ChooseSubtitleSource))
        {
            return;
        }

        // Resolve reports ids; the picker needs the row itself. A miss means the row went away
        // between the resolve and here, which is the same as "nothing selected".
        if (!_rowsById.TryGetValue(selection.Ids[0], out var row))
        {
            return;
        }

        VideoFile probed;
        try
        {
            probed = await _mediaProbe.ProbeAsync(Describe(row), cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "자막 원본을 고르기 위한 파일 정보 확인에 실패했습니다: {Path}", row.FullPath);
            StatusMessage = Strings.SubtitleSourceProbeFailed;
            _dialogs.ShowWarning(Strings.SubtitleSourceProbeFailed);
            return;
        }

        // ProbeAsync reports a bad file through ProbeError rather than throwing, so this is the
        // normal failure path, not the exceptional one.
        if (probed.ProbeError is not null)
        {
            _logger.LogWarning("파일 정보를 읽지 못했습니다: {Path} ({Error})", row.FullPath, probed.ProbeError);
            StatusMessage = Strings.SubtitleSourceProbeFailed;
            _dialogs.ShowWarning(Strings.SubtitleSourceProbeFailed);
            return;
        }

        await PromptForSourceAsync(
            row,
            probed,
            Strings.SubtitleSourceDialogTitle,
            string.Format(CultureInfo.CurrentCulture, Strings.SubtitleSourceMessageFormat, row.FileName),
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Implements <see cref="ExistingSubtitlePolicy.AskPerFile"/>: after a scan, walk the files that
    /// already have subtitles and ask what to do with each.
    ///
    /// Only files with an existing subtitle are offered. Asking about a file with nothing to choose
    /// from would be a modal dialog with one option, which is a worse experience than the default.
    /// </summary>
    private async Task AskPerFileIfRequestedAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.ExistingSubtitlePolicy != ExistingSubtitlePolicy.AskPerFile)
        {
            return;
        }

        var candidates = _lastScan.Values
            .Where(file => file.HasEmbeddedSubtitle || file.HasExternalSubtitle)
            .OrderBy(file => file.FileName, StringComparer.CurrentCulture)
            .ToArray();

        if (candidates.Length == 0)
        {
            return;
        }

        var answered = 0;

        for (var i = 0; i < candidates.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = candidates[i];
            var row = FindRow(file.FullPath);
            if (row is null)
            {
                continue;
            }

            var message = string.Format(
                CultureInfo.CurrentCulture,
                Strings.AskPerFileMessageFormat,
                file.FileName,
                i + 1,
                candidates.Length);

            if (await PromptForSourceAsync(row, file, Strings.AskPerFileTitle, message, cancellationToken)
                    .ConfigureAwait(true))
            {
                answered++;
            }
        }

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            Strings.AskPerFileDoneFormat,
            candidates.Length,
            answered);
    }

    /// <summary>Shows the picker for one row and persists the answer. False when nothing changed.</summary>
    private async Task<bool> PromptForSourceAsync(
        JobRowViewModel row,
        VideoFile probed,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var options = BuildSourceOptions(probed);

        if (options.Count <= 1)
        {
            // Only the "follow the setting" entry: there is nothing to choose between.
            StatusMessage = Strings.SubtitleSourceNoTracks;
            return false;
        }

        var chosen = _dialogs.PickSubtitleSource(title, message, options, IndexOfCurrent(options, row));
        if (chosen is null)
        {
            return false;
        }

        var applied = await _queue.SetSourceOverrideAsync(
            row.Id,
            chosen.Mode,
            chosen.Mode == JobSourceOverride.Audio ? chosen.TrackIndex : null,
            chosen.Mode == JobSourceOverride.EmbeddedSubtitle ? chosen.TrackIndex : null,
            chosen.Language,
            cancellationToken).ConfigureAwait(true);

        if (!applied)
        {
            StatusMessage = Strings.SubtitleSourceJobRunning;
            return false;
        }

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            Strings.SubtitleSourceChangedFormat,
            chosen.Display);

        return true;
    }

    /// <summary>
    /// Builds the picker's list: "follow the setting" first, then every audio track, then every
    /// embedded subtitle track, each labelled with the track's own <c>DisplayName</c>.
    /// </summary>
    private static List<SubtitleSourceOption> BuildSourceOptions(VideoFile probed)
    {
        var options = new List<SubtitleSourceOption>(1 + probed.AudioTracks.Count + probed.SubtitleTracks.Count)
        {
            new(Strings.SubtitleSourceDefault, JobSourceOverride.None)
            {
                Hint = Strings.SubtitleSourceUseSettingHint
            }
        };

        if (probed.AudioTracks.Count == 0 && probed.HasAudioTrack)
        {
            // Probed as "has audio" but with no enumerable stream list: still offer plain ASR so a
            // file the prober only half-understood is not locked out of the core path.
            options.Add(new SubtitleSourceOption(Strings.SubtitleSourceAudioDefault, JobSourceOverride.Audio));
        }

        foreach (var track in probed.AudioTracks)
        {
            options.Add(new SubtitleSourceOption(
                string.Format(CultureInfo.CurrentCulture, Strings.SubtitleSourceAudioFormat, track.DisplayName),
                JobSourceOverride.Audio,
                track.Index));
        }

        foreach (var track in probed.SubtitleTracks)
        {
            options.Add(new SubtitleSourceOption(
                string.Format(CultureInfo.CurrentCulture, Strings.SubtitleSourceEmbeddedFormat, track.DisplayName),
                JobSourceOverride.EmbeddedSubtitle,
                track.Index,
                track.Language));
        }

        return options;
    }

    /// <summary>Index of the option that matches what the job is already set to; 0 when none does.</summary>
    private static int IndexOfCurrent(IReadOnlyList<SubtitleSourceOption> options, JobRowViewModel row)
    {
        var trackIndex = row.SourceOverride == JobSourceOverride.EmbeddedSubtitle
            ? row.SelectedSubtitleTrackIndex
            : row.SelectedAudioTrackIndex;

        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Mode == row.SourceOverride && options[i].TrackIndex == trackIndex)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>A minimal <see cref="VideoFile"/> for re-probing a row whose scan result is gone.</summary>
    private static VideoFile Describe(JobRowViewModel row) => new()
    {
        FullPath = row.FullPath,
        FileName = row.FileName,
        Extension = Path.GetExtension(row.FullPath),
        SizeBytes = 0L,
        LastWriteTimeUtc = DateTime.UtcNow
    };

    private JobRowViewModel? FindRow(string videoPath)
    {
        foreach (var row in Jobs)
        {
            if (string.Equals(row.FullPath, videoPath, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Commands: selection helpers and shell integration
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void SelectAll() => SetAllChecked(true);

    [RelayCommand]
    private void ClearSelection() => SetAllChecked(false);

    private void SetAllChecked(bool value) => BatchSelectionChange(() =>
    {
        foreach (var row in Jobs)
        {
            row.IsSelected = value;
        }
    });

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var row = PrimaryRow();
        if (row is null)
        {
            StatusMessage = Strings.NoSelectionMessage;
            _dialogs.ShowInformation(Strings.NoSelectionMessage);
            return;
        }

        if (string.IsNullOrWhiteSpace(row.OutputPath))
        {
            StatusMessage = Strings.OutputNotReadyMessage;
            return;
        }

        if (!_shell.RevealOrOpenParent(row.OutputPath))
        {
            StatusMessage = Strings.OutputNotReadyMessage;
        }
    }

    [RelayCommand]
    private void OpenSourceFolder()
    {
        var row = PrimaryRow();
        if (row is null)
        {
            StatusMessage = Strings.NoSelectionMessage;
            _dialogs.ShowInformation(Strings.NoSelectionMessage);
            return;
        }

        if (!_shell.RevealOrOpenParent(row.FullPath))
        {
            StatusMessage = Strings.SourceNotFoundMessage;
        }
    }

    [RelayCommand]
    private void OpenLogWindow() => _windows.ShowLogs();

    [RelayCommand]
    private void OpenSettings()
    {
        if (!_windows.ShowSettings())
        {
            return;
        }

        // SettingsService raises SettingsChanged on save, which refreshes the checkboxes; reading the
        // snapshot here keeps the copy used by StartAsync in step even if that event ordering changes.
        _settings = _settingsService.Current;
        StatusMessage = Strings.SettingsSavedMessage;
    }

    [RelayCommand]
    private void OpenModels() => _windows.ShowModels();

    // -----------------------------------------------------------------------
    // Queue events (background threads)
    // -----------------------------------------------------------------------

    private void OnJobChanged(object? sender, JobChangedEventArgs e)
    {
        _pendingUpdates[e.Job.Id] = e.Job;
        ScheduleFlush();
    }

    private void OnQueueStateChanged(object? sender, QueueStateChangedEventArgs e)
    {
        var state = e.State;
        var message = e.Message;

        _ = _dispatcher.InvokeAsync(() =>
        {
            QueueStatus = state;

            if (!string.IsNullOrWhiteSpace(message))
            {
                StatusMessage = message;
            }

            RecalculateCounters();
        });
    }

    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        var snapshot = e;
        _ = _dispatcher.InvokeAsync(() =>
        {
            _settings = snapshot;
            ApplySettings(snapshot);
        });
    }

    private void OnHardwareProfileChanged(object? sender, HardwareProfile e)
    {
        var profile = e;
        _ = _dispatcher.InvokeAsync(() => GpuSummary = FormatGpuSummary(profile));
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) != 0)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(FlushPendingUpdates, DispatcherPriority.Background);
    }

    /// <summary>
    /// Drains the coalescing buffer onto the rows. Always runs on the dispatcher thread.
    ///
    /// The flag is cleared *before* draining: a producer that arrives mid-drain then schedules a
    /// second flush, which is at worst a no-op — whereas clearing it afterwards could drop the last
    /// update of a job and leave a row stuck at 99%.
    /// </summary>
    private void FlushPendingUpdates()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);

        if (_pendingUpdates.IsEmpty)
        {
            return;
        }

        List<JobRowViewModel>? newRows = null;

        // One drain can carry hundreds of status changes, so the command re-evaluation each of them
        // triggers is folded into the single pass at the end.
        BatchSelectionChange(() =>
        {
            foreach (var id in _pendingUpdates.Keys)
            {
                if (!_pendingUpdates.TryRemove(id, out var job))
                {
                    continue;
                }

                if (_rowsById.TryGetValue(id, out var row))
                {
                    row.Update(job);
                    continue;
                }

                var created = new JobRowViewModel(job);
                AttachRow(created);
                _rowsById[id] = created;
                (newRows ??= []).Add(created);
            }

            if (newRows is { Count: > 0 })
            {
                Jobs.AddRange(newRows);
            }

            RecalculateCounters();
        });
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void RebuildRows(IReadOnlyList<Job> jobs)
    {
        foreach (var existing in _rowsById.Values)
        {
            DetachRow(existing);
        }

        _rowsById.Clear();
        _pendingUpdates.Clear();

        var rows = new List<JobRowViewModel>(jobs.Count);

        BatchSelectionChange(() =>
        {
            foreach (var job in jobs)
            {
                var row = new JobRowViewModel(job);
                AttachRow(row);
                _rowsById[job.Id] = row;
                rows.Add(row);
            }

            Jobs.Reset(rows);
            RecalculateCounters();
        });
    }

    private void RecalculateCounters()
    {
        var pending = 0;
        var running = 0;
        var completed = 0;
        var failed = 0;

        foreach (var row in Jobs)
        {
            switch (row.Status)
            {
                case JobStatus.Completed:
                    completed++;
                    break;
                case JobStatus.Failed:
                    failed++;
                    break;
                case JobStatus.Pending:
                case JobStatus.Paused:
                    pending++;
                    break;
                case JobStatus.Cancelled:
                    break;
                default:
                    running++;
                    break;
            }
        }

        TotalCount = Jobs.Count;
        PendingCount = pending;
        RunningCount = running;
        CompletedCount = completed;
        FailedCount = failed;
    }

    private void ApplySettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(TargetFolder) && !string.IsNullOrWhiteSpace(settings.LastFolder))
        {
            TargetFolder = settings.LastFolder;
        }

        IncludeSubfolders = settings.IncludeSubfolders;
        IncludeHiddenFolders = settings.IncludeHiddenFolders;
        SkipIfKoreanSubtitleExists = settings.SkipIfKoreanSubtitleExists;
    }

    private static string FormatGpuSummary(HardwareProfile profile)
    {
        var gpu = profile.PrimaryGpu;

        if (gpu is null)
        {
            return Strings.GpuNotDetected;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            Strings.GpuSummaryFormat,
            gpu.Name,
            gpu.TotalVramGb,
            profile.CudaAvailable ? Strings.Available : Strings.Unavailable);
    }

    /// <summary>Ids of the checked rows.</summary>
    private List<string> CheckedIds()
    {
        var ids = new List<string>();

        foreach (var row in Jobs)
        {
            if (row.IsSelected)
            {
                ids.Add(row.Id);
            }
        }

        return ids;
    }

    // -----------------------------------------------------------------------
    // Selection
    //
    // The decision table itself lives in KSubMaker.Domain.Jobs.JobSelectionResolver, which the Linux
    // test suite can reach; this project only turns the verdict into Korean and into CanExecute.
    // -----------------------------------------------------------------------

    /// <summary>Every row as the resolver sees it, in display order.</summary>
    private IEnumerable<JobSelectionCandidate> Candidates()
    {
        foreach (var row in Jobs)
        {
            yield return new JobSelectionCandidate(row.Id, row.IsSelected, row.Status);
        }
    }

    private JobSelectionCandidate? Highlighted() =>
        SelectedJob is { } row ? new JobSelectionCandidate(row.Id, row.IsSelected, row.Status) : null;

    /// <summary>
    /// The row the shell commands act on: the highlighted row, else the first checked one — the same
    /// precedence <see cref="JobSelectionResolver.ResolveSingle"/> uses.
    ///
    /// These two have no eligibility rule (any row has a source folder, and a missing output is
    /// reported separately), so <see cref="Strings.NoSelectionMessage"/> is the only thing they can
    /// fail with and the resolver would add nothing.
    /// </summary>
    private JobRowViewModel? PrimaryRow()
    {
        if (SelectedJob is not null)
        {
            return SelectedJob;
        }

        foreach (var row in Jobs)
        {
            if (row.IsSelected)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>Bulk rule (취소 / 재시도 / 선택 항목 제거): checked rows win, else the highlighted one.</summary>
    private JobSelection Resolve(JobAction action) =>
        JobSelectionResolver.Resolve(Candidates(), Highlighted(), action);

    /// <summary>Single-row rule (자막 원본 선택): the highlighted row wins, else the first checked one.</summary>
    private JobSelection ResolveSingle(JobAction action) =>
        JobSelectionResolver.ResolveSingle(Candidates(), Highlighted(), action);

    /// <summary>
    /// True when the command may proceed; otherwise reports why it may not.
    ///
    /// The two failure cases are kept apart on purpose. They used to share
    /// <see cref="Strings.NoSelectionMessage"/>, so pressing 취소 with a *failed* job selected —
    /// 실패 is a terminal state, so nothing was cancellable — told the user to select something
    /// first, contradicting the row they had just clicked.
    /// </summary>
    private bool Accept(JobSelection selection, JobAction action)
    {
        if (selection.IsOk)
        {
            return true;
        }

        if (selection.Outcome == SelectionOutcome.NothingSelected)
        {
            var nothing = NothingSelectedMessage(action);
            StatusMessage = nothing;
            _dialogs.ShowInformation(nothing);
            return false;
        }

        // A refusal, not a hint: the user did pick something and the action is not going to happen.
        var reason = IneligibleMessage(action);
        StatusMessage = reason;
        _dialogs.ShowWarning(reason);
        return false;
    }

    /// <summary>
    /// What an empty selection means for this action.
    ///
    /// 시작 is the odd one out: it does not act on a selection at all when nothing is checked, so an
    /// empty result means "the queue holds nothing runnable", not "you forgot to pick a row". Telling
    /// a user staring at 147 취소됨 rows to select something first would be the same contradiction
    /// <see cref="JobSelectionResolver"/> was written to end.
    /// </summary>
    private static string NothingSelectedMessage(JobAction action) => action switch
    {
        JobAction.Start => Strings.NoRunnableJobs,
        _ => Strings.NoSelectionMessage
    };

    /// <summary>Why this particular action cannot be applied to what is selected.</summary>
    private static string IneligibleMessage(JobAction action) => action switch
    {
        JobAction.Start => Strings.SelectionNotStartableMessage,
        JobAction.Cancel => Strings.SelectionNotCancellableMessage,
        JobAction.Retry => Strings.SelectionNotRetryableMessage,
        JobAction.ChooseSubtitleSource => Strings.SubtitleSourceJobRunning,

        // JobAction.Remove accepts every status, so it never lands here; the generic sentence keeps
        // the mapping total instead of falling back on the no-selection message that caused the bug.
        _ => Strings.SelectionNotEligibleMessage
    };

    /// <summary>
    /// Re-evaluates the four selection-driven commands.
    ///
    /// Suppressed while a bulk change is in progress (see <see cref="_selectionNotificationDepth"/>);
    /// <see cref="BatchSelectionChange"/> makes the one call at the end.
    /// </summary>
    private void NotifySelectionCommands()
    {
        if (_selectionNotificationDepth > 0)
        {
            return;
        }

        RetryCommand.NotifyCanExecuteChanged();
        CancelJobsCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ChooseSubtitleSourceCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Runs <paramref name="change"/> with command re-evaluation coalesced into a single pass at the
    /// end — unconditionally, because adding or removing rows changes what is actionable without any
    /// one property change announcing it.
    /// </summary>
    private void BatchSelectionChange(Action change)
    {
        _selectionNotificationDepth++;

        try
        {
            change();
        }
        finally
        {
            _selectionNotificationDepth--;
            NotifySelectionCommands();
        }
    }

    /// <summary>
    /// Starts watching one row. Both the checkbox and the status feed CanExecute — a job that
    /// finishes stops being cancellable while the user is looking at the button.
    /// </summary>
    private void AttachRow(JobRowViewModel row) => row.PropertyChanged += OnRowPropertyChanged;

    private void DetachRow(JobRowViewModel row) => row.PropertyChanged -= OnRowPropertyChanged;

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only these two decide eligibility. A running job raises progress, speed and ETA changes
        // dozens of times a second, and none of them may drag four commands through a re-evaluation.
        // A null or empty name means "everything changed", which has to be honoured.
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(JobRowViewModel.IsSelected) or nameof(JobRowViewModel.Status))
        {
            NotifySelectionCommands();
        }
    }

    /// <summary>
    /// Takes rows out of the grid after the queue has already let go of the jobs behind them.
    /// </summary>
    private void RemoveRows(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var removed = new HashSet<string>(ids, StringComparer.Ordinal);

        BatchSelectionChange(() =>
        {
            foreach (var id in removed)
            {
                if (_rowsById.Remove(id, out var row))
                {
                    DetachRow(row);
                }

                // A queue event that was already buffered when the job was removed must not put the
                // row back on the next flush.
                _pendingUpdates.TryRemove(id, out _);
            }

            if (SelectedJob is { } selected && removed.Contains(selected.Id))
            {
                SelectedJob = null;
            }

            Jobs.RemoveWhere(row => removed.Contains(row.Id));
            RecalculateCounters();
        });
    }
}
