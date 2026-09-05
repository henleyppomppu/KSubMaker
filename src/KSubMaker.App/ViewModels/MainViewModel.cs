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
using KSubMaker.Domain.Subtitles;
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
    private readonly IFileActionService _fileActions;
    private readonly IWindowService _windows;
    private readonly ISystemPowerService _power;
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
    /// <see cref="SubtitleSourcePreference.AskPerFile"/> prompt can list a file's tracks without
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
        IFileActionService fileActions,
        IWindowService windows,
        ISystemPowerService power,
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
        _fileActions = fileActions;
        _windows = windows;
        _power = power;
        _logger = logger;

        // Resolved on the UI thread by the composition root, so CurrentDispatcher is the UI one; the
        // Application lookup is preferred because it stays correct if this type is ever constructed
        // from a worker thread in a test host.
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Jobs = [];
    }

    /// <summary>Rows shown in the grid, in queue order.</summary>
    public BulkObservableCollection<JobRowViewModel> Jobs { get; }

    /// <summary>큐 완료 후 동작 choices for the command-bar dropdown, in enum order.</summary>
    public IReadOnlyList<Option<PostQueueAction>> PostQueueActions { get; } =
        Enum.GetValues<PostQueueAction>()
            .Select(a => new Option<PostQueueAction>(a, DisplayText.PostQueueActionName(a)))
            .ToArray();

    /// <summary>
    /// The 큐 완료 후 동작, shown on the main screen so it can be set without opening 설정. Bound
    /// two-way to the command-bar dropdown; a change here is persisted immediately and reaches the
    /// settings window through <see cref="SettingsService.SettingsChanged"/> like any other save.
    /// </summary>
    [ObservableProperty]
    private PostQueueAction _selectedPostQueueAction;

    partial void OnSelectedPostQueueActionChanged(PostQueueAction value)
    {
        // Equal to the live setting means this change came from ApplySettings echoing a save back,
        // not from the user touching the dropdown — nothing to persist, and re-saving would loop.
        if (value == _settings.PostQueueAction)
        {
            return;
        }

        var updated = _settingsService.Current;
        updated.PostQueueAction = value;
        _ = PersistPostQueueActionAsync(updated);
    }

    private async Task PersistPostQueueActionAsync(AppSettings updated)
    {
        try
        {
            await _settingsService.SaveAsync(updated).ConfigureAwait(false);
            _logger.LogInformation("큐 완료 후 동작을 변경했습니다: {Action}", updated.PostQueueAction);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "큐 완료 후 동작 설정을 저장하지 못했습니다.");
        }
    }

    // -----------------------------------------------------------------------
    // Scan options
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSourceFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    private string _targetFolder = string.Empty;

    [ObservableProperty]
    private bool _includeSubfolders = true;

    [ObservableProperty]
    private bool _includeHiddenFolders;


    // -----------------------------------------------------------------------
    // Queue / status
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueStateText))]
    [NotifyPropertyChangedFor(nameof(IsQueueRunning))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartTestCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCompletedCommand))]
    private QueueState _queueStatus = QueueState.Idle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartTestCommand))]
    private bool _isScanning;

    /// <summary>
    /// True while the pre-start model download is running. It gates <see cref="StartCommand"/> the
    /// same way <see cref="IsScanning"/> does — the download is part of starting, so a second click
    /// must not queue a second one.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartTestCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelModelPreparationCommand))]
    private bool _isPreparingModels;

    /// <summary>
    /// How many seconds from the start of each video the 테스트 실행 button processes. The dropdown
    /// beside the button sets it; it is remembered in <see cref="AppSettings.TestDurationSeconds"/>
    /// but no longer surfaced on the settings screen — it only ever reaches a run through that button,
    /// and 시작 forces it to zero so a stale value can never truncate a real run.
    /// </summary>
    [ObservableProperty]
    private int _testLengthSeconds = 60;

    private static string DescribeTestLength(int seconds) =>
        seconds % 60 == 0 ? $"{seconds / 60}분" : $"{seconds}초";

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
    [NotifyPropertyChangedFor(nameof(StartButtonCaption))]
    private int _pendingCount;

    /// <summary>
    /// 시작 button label. Spells out what a click will do: the checked rows that can actually run if
    /// any are checked, otherwise the whole pending queue. Checking a 취소됨 / 완료 row and pressing
    /// 시작 does nothing (those are finished states — 재시도 puts them back), so it must not be
    /// counted here.
    /// </summary>
    public string StartButtonCaption
    {
        get
        {
            var runnableChecked = 0;
            var anyChecked = false;
            foreach (var row in Jobs)
            {
                if (!row.IsSelected)
                {
                    continue;
                }

                anyChecked = true;
                if (row.IsRunnable)
                {
                    runnableChecked++;
                }
            }

            if (anyChecked)
            {
                return string.Format(CultureInfo.CurrentCulture, "시작 · 선택 {0}", runnableChecked);
            }

            return PendingCount > 0
                ? string.Format(CultureInfo.CurrentCulture, "시작 · 전체 {0}", PendingCount)
                : Strings.StartButton;
        }
    }

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
    [NotifyCanExecuteChangedFor(nameof(RunThisJobCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSourceFolderCommand))]
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
            _queue.QueueDrained += OnQueueDrained;
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
        _queue.QueueDrained -= OnQueueDrained;
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

    /// <summary>
    /// Entry point for files and folders dropped onto the queue grid. Same pipeline as a scan —
    /// resolve → enqueue → probe → summary — and the same guards, so a drop can do nothing a scan
    /// could not.
    /// </summary>
    public async Task AddDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (IsQueueRunning)
        {
            StatusMessage = Strings.QueueBusyCannotScan;
            _dialogs.ShowWarning(Strings.QueueBusyCannotScan);
            return;
        }

        if (IsScanning || IsPreparingModels)
        {
            // A scan is already using the status bar and the CTS; a competing drop would race it.
            StatusMessage = Strings.DropWhileBusyMessage;
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _scanCts, cts)?.Dispose();
        IsScanning = true;

        try
        {
            // The drop deliberately does not persist scan options or touch TargetFolder: the user
            // handed us paths, not a new default folder.
            var settings = _settingsService.Current;
            _settings = settings;

            StatusMessage = Strings.DropResolvingMessage;

            var options = new ScanRequest
            {
                RootFolder = string.Empty, // per-item; ResolveDropped substitutes each folder
                IncludeSubfolders = IncludeSubfolders,
                IncludeHiddenFolders = IncludeHiddenFolders
            };

            var resolution = await _scanService.ResolveDroppedAsync(paths, options, cts.Token).ConfigureAwait(true);

            if (resolution.Files.Count == 0)
            {
                StatusMessage = string.Format(
                    CultureInfo.CurrentCulture, Strings.DropNothingToAddFormat, resolution.IgnoredPaths);
                return;
            }

            var results = await _queue.EnqueueAsync(resolution.Files, settings, cts.Token).ConfigureAwait(true);

            await ProbeNewlyQueuedAsync(resolution.Files, results, cts.Token).ConfigureAwait(true);

            ShowEnqueueSummary(results);

            await AskPerFileIfRequestedAsync(settings, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.ScanCancelledMessage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "끌어다 놓은 항목을 처리하는 중 오류가 발생했습니다.");
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

    /// <summary>시작: checked rows if any are checked, otherwise the whole runnable queue.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => LaunchQueueAsync(testDurationSeconds: 0);

    /// <summary>
    /// "이 파일만 실행" from the row's right-click menu: runs exactly that job, regardless of what is
    /// checked. Explicit, so it does not need the highlight-vs-checkbox precedence that 시작 avoids.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RunThisJobAsync(JobRowViewModel? row)
    {
        row ??= PrimaryRow();
        if (row is null)
        {
            return Task.CompletedTask;
        }

        if (!row.IsRunnable)
        {
            _dialogs.ShowWarning(Strings.SelectionNotStartableMessage, Strings.StartButton);
            return Task.CompletedTask;
        }

        return LaunchQueueAsync(testDurationSeconds: 0, explicitIds: [row.Id]);
    }

    /// <summary>
    /// 테스트 실행: process only the first <see cref="TestLengthSeconds"/> seconds of each file, so a
    /// setup can be checked end to end without committing to a long queue. The dropdown passes a new
    /// length as a string; a bare click (no parameter) reuses the remembered one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartTestAsync(string? seconds)
    {
        if (int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var picked)
            && picked > 0
            && picked != TestLengthSeconds)
        {
            TestLengthSeconds = picked;
            _ = PersistTestLengthAsync(picked);
        }

        return LaunchQueueAsync(TestLengthSeconds);
    }

    private async Task LaunchQueueAsync(int testDurationSeconds, IReadOnlyList<string>? explicitIds = null)
    {
        IReadOnlyList<string>? restrict;

        if (explicitIds is not null)
        {
            // "이 파일만 실행": the caller already checked eligibility.
            restrict = explicitIds;
        }
        else
        {
            var anyChecked = CheckedIds().Count > 0;

            var selection = JobSelectionResolver.ResolveStart(Candidates());
            if (!Accept(selection, JobAction.Start))
            {
                return;
            }

            // A checked selection is a restriction; null means "the whole pending queue" and is kept
            // unpinned so a job added between here and the pump still runs.
            restrict = anyChecked ? selection.Ids : null;
        }

        _settings = _settingsService.Current;

        if (await EnsureModelsAsync().ConfigureAwait(true) is not { } runSettings)
        {
            return;
        }

        // A copy, because EnsureModelsAsync can hand back the live settings object (fake AI, a failed
        // status probe). 시작 pins the length to zero regardless of what is stored, so a leftover test
        // length is structurally unable to shorten a real run.
        runSettings = runSettings.Clone();
        runSettings.TestDurationSeconds = testDurationSeconds;

        await _queue.StartAsync(runSettings, restrict).ConfigureAwait(true);

        StatusMessage = testDurationSeconds > 0
            ? string.Format(CultureInfo.CurrentCulture, "테스트 실행을 시작했습니다 (앞 {0}).", DescribeTestLength(testDurationSeconds))
            : Strings.StartedMessage;
    }

    /// <summary>
    /// Remembers the test length across restarts. Best effort: it lands in the same settings row the
    /// scan options use, so a failure here is no worse than the length reverting to its default.
    /// </summary>
    private async Task PersistTestLengthAsync(int seconds)
    {
        try
        {
            var settings = _settingsService.Current.Clone();
            settings.TestDurationSeconds = seconds;
            await _settingsService.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "테스트 실행 길이를 저장하지 못했습니다.");
        }
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
    /// Implements <see cref="SubtitleSourcePreference.AskPerFile"/>: after a scan, walk the files that
    /// already have subtitles and ask what to do with each.
    ///
    /// Only files with an existing subtitle are offered. Asking about a file with nothing to choose
    /// from would be a modal dialog with one option, which is a worse experience than the default.
    /// </summary>
    private async Task AskPerFileIfRequestedAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.SubtitleSource != SubtitleSourcePreference.AskPerFile)
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

    // Opening a folder never needs a selection: with a row highlighted these reveal that one file,
    // otherwise they open the folder the queue is working with. They are only disabled when there is
    // genuinely no folder to open — a first run before any scan.

    private string ConfiguredOutputDirectory => _settings.OutputDirectory?.Trim() ?? string.Empty;

    private bool CanOpenSourceFolder =>
        PrimaryRow() is not null || !string.IsNullOrWhiteSpace(TargetFolder);

    private bool CanOpenOutputFolder =>
        PrimaryRow() is not null
        || !string.IsNullOrWhiteSpace(ConfiguredOutputDirectory)
        || !string.IsNullOrWhiteSpace(TargetFolder);

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private void OpenOutputFolder()
    {
        var row = PrimaryRow();

        if (row is not null && !string.IsNullOrWhiteSpace(row.OutputPath))
        {
            if (!_shell.RevealOrOpenParent(row.OutputPath))
            {
                StatusMessage = Strings.OutputNotReadyMessage;
            }

            return;
        }

        // No finished subtitle to point at: open where subtitles go — the configured output folder,
        // or the source folder when they are written next to the video.
        var folder = !string.IsNullOrWhiteSpace(ConfiguredOutputDirectory) ? ConfiguredOutputDirectory : TargetFolder;

        if (!_shell.OpenFolder(folder))
        {
            StatusMessage = Strings.OutputNotReadyMessage;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSourceFolder))]
    private void OpenSourceFolder()
    {
        var row = PrimaryRow();

        if (row is not null)
        {
            if (!_shell.RevealOrOpenParent(row.FullPath))
            {
                StatusMessage = Strings.SourceNotFoundMessage;
            }

            return;
        }

        if (!_shell.OpenFolder(TargetFolder))
        {
            StatusMessage = Strings.SourceNotFoundMessage;
        }
    }

    /// <summary>Plays a video in the OS default player. Bound to a double-click on the 파일명 cell.</summary>
    [RelayCommand]
    private void OpenVideoFile(JobRowViewModel? row)
    {
        row ??= PrimaryRow();
        if (row is null)
        {
            return;
        }

        if (!_shell.OpenFile(row.FullPath))
        {
            StatusMessage = Strings.SourceNotFoundMessage;
        }
    }

    /// <summary>Windows 속성 대화상자.</summary>
    [RelayCommand]
    private void ShowFileProperties(JobRowViewModel? row)
    {
        row ??= PrimaryRow();
        if (row is not null && !_fileActions.ShowProperties(row.FullPath))
        {
            StatusMessage = Strings.SourceNotFoundMessage;
        }
    }

    /// <summary>이 작업의 메모를 편집한다. 실행 중이어도 허용 — 파이프라인과 무관한 메타데이터다.</summary>
    [RelayCommand]
    private async Task EditNoteAsync(JobRowViewModel? row)
    {
        row ??= PrimaryRow();
        if (row is null)
        {
            return;
        }

        var text = _dialogs.PromptText(
            Strings.NoteDialogTitle,
            string.Format(CultureInfo.CurrentCulture, Strings.NoteDialogMessageFormat, row.FileName),
            row.Note,
            multiline: true);

        if (text is null)
        {
            return;
        }

        await _queue.SetNoteAsync(row.Id, text).ConfigureAwait(true);
        StatusMessage = string.IsNullOrWhiteSpace(text) ? Strings.NoteClearedMessage : Strings.NoteSavedMessage;
    }

    /// <summary>
    /// 파일 이름 바꾸기: renames the source video (and, if the user agrees, the subtitle files sitting
    /// next to it), then points the job at the new path. Refused while the job is processing.
    /// </summary>
    [RelayCommand]
    private async Task RenameFileAsync(JobRowViewModel? row)
    {
        row ??= PrimaryRow();
        if (row is null)
        {
            return;
        }

        if (JobStateMachine.IsActive(row.Status))
        {
            _dialogs.ShowWarning(Strings.FileActionJobRunning, Strings.RenameDialogTitle);
            return;
        }

        var currentName = Path.GetFileName(row.FullPath);

        var newName = _dialogs.PromptText(
            Strings.RenameDialogTitle,
            string.Format(CultureInfo.CurrentCulture, Strings.RenameDialogMessageFormat, currentName),
            currentName);

        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName.Trim(), currentName, StringComparison.Ordinal))
        {
            return;
        }

        var sidecars = FindSidecarSubtitles(row.FullPath);
        var renameSidecars = sidecars.Count > 0 && _dialogs.Confirm(
            string.Format(CultureInfo.CurrentCulture, Strings.RenameSidecarConfirmFormat, sidecars.Count),
            Strings.RenameDialogTitle);

        var oldBase = Path.GetFileNameWithoutExtension(row.FullPath);
        var newBase = Path.GetFileNameWithoutExtension(newName.Trim());

        var newVideoPath = _fileActions.Rename(row.FullPath, newName.Trim());
        if (newVideoPath is null)
        {
            StatusMessage = Strings.RenameFailedMessage;
            _dialogs.ShowWarning(Strings.RenameFailedMessage, Strings.RenameDialogTitle);
            return;
        }

        var renamedSidecars = 0;
        if (renameSidecars)
        {
            foreach (var sidecar in sidecars)
            {
                var tail = Path.GetFileName(sidecar)[oldBase.Length..]; // ".ko.srt", ".srt", ...
                if (_fileActions.Rename(sidecar, newBase + tail) is not null)
                {
                    renamedSidecars++;
                }
            }
        }

        var newOutputPath = OutputPathResolver.BuildDefaultPath(
            newVideoPath, _settings.OutputSuffix, _settings.OutputDirectory);

        await _queue.UpdateSourcePathAsync(row.Id, newVideoPath, newOutputPath).ConfigureAwait(true);

        StatusMessage = renameSidecars
            ? string.Format(CultureInfo.CurrentCulture, Strings.RenameDoneWithSidecarsFormat, renamedSidecars)
            : Strings.RenameDoneMessage;
    }

    /// <summary>
    /// 삭제: sends the source video to the Recycle Bin (optionally its subtitles too) and drops the
    /// job from the queue. Confirmed first; refused while the job is processing.
    /// </summary>
    [RelayCommand]
    private async Task DeleteFileAsync(JobRowViewModel? row)
    {
        row ??= PrimaryRow();
        if (row is null)
        {
            return;
        }

        if (JobStateMachine.IsActive(row.Status))
        {
            _dialogs.ShowWarning(Strings.FileActionJobRunning, Strings.DeleteDialogTitle);
            return;
        }

        if (!_dialogs.Confirm(
                string.Format(CultureInfo.CurrentCulture, Strings.DeleteConfirmFormat, Path.GetFileName(row.FullPath)),
                Strings.DeleteDialogTitle))
        {
            return;
        }

        var toDelete = new List<string> { row.FullPath };

        var sidecars = FindSidecarSubtitles(row.FullPath);
        if (sidecars.Count > 0 && _dialogs.Confirm(
                string.Format(CultureInfo.CurrentCulture, Strings.DeleteSidecarConfirmFormat, sidecars.Count),
                Strings.DeleteDialogTitle))
        {
            toDelete.AddRange(sidecars);
        }

        if (!_fileActions.RecycleFiles(toDelete))
        {
            StatusMessage = Strings.DeleteFailedMessage;
            _dialogs.ShowWarning(Strings.DeleteFailedMessage, Strings.DeleteDialogTitle);
            return;
        }

        var result = await _queue.RemoveAsync([row.Id]).ConfigureAwait(true);
        RemoveRows(result.Removed);

        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.DeleteDoneFormat, toDelete.Count);
    }

    /// <summary>
    /// Subtitle files sitting next to a video and sharing its base name — <c>movie.srt</c>,
    /// <c>movie.ko.srt</c>, <c>movie.en.ass</c>. Used by 이름 바꾸기 and 삭제 to offer to carry them along.
    /// </summary>
    private static IReadOnlyList<string> FindSidecarSubtitles(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(baseName))
        {
            return [];
        }

        string[] extensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".smi", ".sami"];

        try
        {
            var found = new List<string>();

            foreach (var path in Directory.EnumerateFiles(directory, baseName + ".*"))
            {
                var name = Path.GetFileName(path);

                // "movie.ko.srt" starts with "movie." and ends in a subtitle extension; "movie2.srt"
                // does not start with "movie." so it is correctly left out.
                if (name.Length > baseName.Length
                    && name[baseName.Length] == '.'
                    && extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(path);
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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

            // Hold sleep off while there is work in flight, and let the idle timers resume the
            // moment the queue is idle or paused. Called on the dispatcher thread on purpose: the
            // SetThreadExecutionState hold is tied to the calling thread, and this one lives for the
            // life of the app.
            if (state == QueueState.Running)
            {
                _power.PreventSleep();
            }
            else if (state is QueueState.Idle or QueueState.Paused)
            {
                _power.AllowSleep();
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                StatusMessage = message;
            }

            RecalculateCounters();
        });
    }

    /// <summary>
    /// Runs when the queue has processed everything and gone idle on its own. Carries out the
    /// configured 큐 완료 후 동작 — behind the policy check and a cancellable countdown. Never reached
    /// after a manual 중단 or 일시정지: the queue does not raise <see cref="JobQueueService.QueueDrained"/>
    /// for those.
    /// </summary>
    private void OnQueueDrained(object? sender, QueueDrainedEventArgs e)
    {
        var outcome = e.Outcome;

        _ = _dispatcher.InvokeAsync(() =>
        {
            var settings = _settings;
            var action = PostQueueActionPolicy.Resolve(
                settings.PostQueueAction,
                settings.PostQueueActionOnlyWhenAllSucceeded,
                outcome);

            if (action == PostQueueAction.None)
            {
                return;
            }

            _logger.LogInformation("큐 완료 후 동작을 실행합니다: {Action}", action);

            if (!_windows.ConfirmPostQueueAction(action))
            {
                StatusMessage = Strings.PostQueueActionCancelledMessage;
                _logger.LogInformation("사용자가 큐 완료 후 동작을 취소했습니다.");
                return;
            }

            if (!_power.Execute(action))
            {
                StatusMessage = Strings.PostQueueActionFailedMessage;
            }
        });
    }

    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        var snapshot = e;
        _ = _dispatcher.InvokeAsync(async () =>
        {
            var previous = _settings;
            _settings = snapshot;
            ApplySettings(snapshot);
            await OfferOutputRelocationAsync(previous, snapshot).ConfigureAwait(true);
        });
    }

    /// <summary>
    /// When the output folder setting itself changes, subtitles the old setting already wrote sit at
    /// paths the new setting will never look at again — the file is not gone, just orphaned from the
    /// job that made it. Offers to move what is found rather than doing it silently: this runs on
    /// every settings save, so a confirmation-free move would fire on saves that touch something
    /// else entirely.
    ///
    /// <para>Deliberately does not change what counts as "already done" (§E in the review this came
    /// from) — moving the file is what makes the existing completion check see it in the new
    /// location, without touching that check's logic or re-running anything.</para>
    /// </summary>
    private async Task OfferOutputRelocationAsync(AppSettings previous, AppSettings current)
    {
        var oldDirectory = previous.OutputDirectory?.Trim() ?? string.Empty;
        var newDirectory = current.OutputDirectory?.Trim() ?? string.Empty;

        if (string.Equals(oldDirectory, newDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var candidates = Jobs.Select(row => (row.FullPath, row.OutputPath));
        var plan = OutputRelocationPlanner.Plan(candidates, current.OutputSuffix, current.OutputDirectory, File.Exists);

        if (plan.Count == 0)
        {
            return;
        }

        var confirmed = _dialogs.Confirm(
            string.Format(CultureInfo.CurrentCulture, Strings.RelocateOutputsConfirmFormat, plan.Count),
            Strings.RelocateOutputsDialogTitle);

        if (!confirmed)
        {
            return;
        }

        var moved = 0;

        foreach (var relocation in plan)
        {
            if (!_fileActions.Move(relocation.OldPath, relocation.NewPath))
            {
                continue;
            }

            moved++;

            var job = Jobs.FirstOrDefault(row => row.OutputPath == relocation.OldPath);
            if (job is not null)
            {
                await _queue.UpdateSourcePathAsync(job.Id, job.FullPath, relocation.NewPath).ConfigureAwait(true);
            }
        }

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture, Strings.RelocateOutputsDoneFormat, moved, plan.Count);
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
                case JobStatus.Skipped:
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
        SelectedPostQueueAction = settings.PostQueueAction;

        // A value the user set before this ran under the old "테스트 모드" setting carries over as the
        // remembered length; the old "0 = whole video" state just leaves the default in place.
        if (settings.TestDurationSeconds > 0)
        {
            TestLengthSeconds = settings.TestDurationSeconds;
        }

        // 결과 폴더 열기 can become available the moment an output directory is configured.
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
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
        RunThisJobCommand.NotifyCanExecuteChanged();

        // 결과/원본 폴더 열기 fall back to the first checked row when nothing is highlighted, and the
        // 시작 label counts checked rows — both move with the same events the commands do.
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
        OpenSourceFolderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StartButtonCaption));
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
