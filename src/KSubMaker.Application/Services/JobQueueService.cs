using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Media;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Application.Services;

public enum QueueState
{
    Idle,
    Running,
    Pausing,
    Paused,
    Stopping
}

public sealed class JobChangedEventArgs(Job job) : EventArgs
{
    public Job Job { get; } = job;
}

/// <summary>
/// Raised once when the queue has processed everything it was asked to and returned to idle on its
/// own — not after a 중단 or 일시정지. Carries the run's tallies so a listener can decide whether to
/// act (a post-run 절전/종료, a notification) without re-counting the grid.
/// </summary>
public sealed class QueueDrainedEventArgs(QueueRunOutcome outcome) : EventArgs
{
    public QueueRunOutcome Outcome { get; } = outcome;
}

public sealed class QueueStateChangedEventArgs(QueueState state, string? message) : EventArgs
{
    public QueueState State { get; } = state;
    public string? Message { get; } = message;
}

/// <summary>
/// The outcome of a removal request.
///
/// Removal is not all-or-nothing: a job the pump is still holding is left where it is rather than
/// being ripped out from under it, so the caller has to be able to tell the user how many rows
/// actually went away and how many did not.
/// </summary>
/// <param name="Removed">Ids gone from the queue, the database and the cache.</param>
/// <param name="Skipped">Ids still running when the stop budget expired. Still in the queue.</param>
public sealed record JobRemovalResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Skipped)
{
    public static JobRemovalResult Empty { get; } = new([], []);

    public int RemovedCount => Removed.Count;

    public int SkippedCount => Skipped.Count;
}

/// <summary>
/// Owns the work queue: which job runs next, how many run at once, what happens on failure, and how
/// state reaches the database and the UI.
///
/// Concurrency contract: exactly one pump loop mutates job status. Callers interact through the
/// public methods, which are safe to call from the UI thread and never block it.
/// </summary>
public sealed class JobQueueService : IAsyncDisposable
{
    private readonly IJobRepository _repository;
    private readonly IJobProcessorSelector _processorSelector;
    private readonly ICheckpointStore _checkpointStore;
    private readonly HardwareService _hardwareService;
    private readonly ILogger<JobQueueService> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    /// <summary>
    /// Jobs the pump is executing right now, keyed by id. Populated by <see cref="RunJobAsync"/> for
    /// exactly as long as a processor is running, so 취소 and 제거 can stop one job without stopping
    /// the whole queue and can wait for it to actually let go.
    /// </summary>
    private readonly ConcurrentDictionary<string, ActiveJob> _active = new(StringComparer.Ordinal);

    private CancellationTokenSource? _runCts;
    private Task _pump = Task.CompletedTask;
    private volatile bool _pauseRequested;
    private volatile QueueState _state = QueueState.Idle;

    /// <summary>Ids explicitly selected for the current run; empty means "everything pending".</summary>
    private HashSet<string> _restrictTo = new(StringComparer.Ordinal);

    /// <summary>
    /// Terminal outcomes tallied within the current run, so <see cref="QueueDrained"/> can report
    /// what the run actually did. Reset by <see cref="StartAsync"/>; each is bumped once per job from
    /// the single choke point in <see cref="RunJobAsync"/>'s finally.
    /// </summary>
    private int _runCompleted;
    private int _runFailed;
    private int _runCancelled;

    /// <summary>
    /// How often the prefetch lane re-checks whether the pump has moved on. Coarse on purpose: the
    /// stages it is pacing against take minutes, so a tighter poll would only burn wake-ups.
    /// </summary>
    private static readonly TimeSpan PrefetchPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long <see cref="RemoveAsync"/> waits for a cancelled job to actually stop before giving up
    /// on it. Long enough for a worker to unwind a stage and write its checkpoint, short enough that
    /// the button does not look wedged.
    /// </summary>
    public static TimeSpan DefaultRemovalStopTimeout { get; } = TimeSpan.FromSeconds(10);

    public JobQueueService(
        IJobRepository repository,
        IJobProcessorSelector processorSelector,
        ICheckpointStore checkpointStore,
        HardwareService hardwareService,
        ILogger<JobQueueService> logger,
        TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _processorSelector = processorSelector;
        _checkpointStore = checkpointStore;
        _hardwareService = hardwareService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<JobChangedEventArgs>? JobChanged;
    public event EventHandler<QueueStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Fires once after the pump has worked through everything and gone idle on its own. Never fires
    /// after 중단 or 일시정지. The handler runs on the pump's thread; keep it quick.
    /// </summary>
    public event EventHandler<QueueDrainedEventArgs>? QueueDrained;

    public QueueState State => _state;

    public IReadOnlyList<Job> Jobs =>
        _jobs.Values.OrderBy(j => j.QueueOrder).ThenBy(j => j.CreatedAtUtc).ToArray();

    public bool IsRunning => _state is QueueState.Running or QueueState.Pausing or QueueState.Stopping;

    // -----------------------------------------------------------------------
    // Loading and enqueueing
    // -----------------------------------------------------------------------

    /// <summary>Loads persisted jobs and repairs any state left behind by a crash.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var reset = await _repository.ResetOrphanedActiveJobsAsync(cancellationToken).ConfigureAwait(false);
        if (reset > 0)
        {
            _logger.LogInformation("비정상 종료로 남아 있던 작업 {Count}건을 대기 상태로 복구했습니다.", reset);
        }

        var jobs = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);

        _jobs.Clear();
        foreach (var job in jobs)
        {
            _jobs[job.Id] = job;
        }

        RaiseState(QueueState.Idle, null);
    }

    /// <summary>
    /// Deletes cache directories that belong to no job in the queue, plus <c>*.tmp</c> files left
    /// half-written by a hard kill. Returns the number of bytes reclaimed.
    ///
    /// Must run <b>after</b> <see cref="LoadAsync"/>: the set of known ids comes from the loaded
    /// queue, and sweeping against an empty set would delete the checkpoints of every job that is
    /// waiting to be resumed.
    ///
    /// Never throws. This is housekeeping — a locked <c>audio.wav</c> (antivirus, a media player
    /// still holding the file) must cost the user nothing more than the disk space it occupies.
    /// </summary>
    public async Task<long> CleanupOrphanedCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var known = _jobs.Keys.ToArray();
            var reclaimed = await _checkpointStore
                .CleanupOrphansAsync(known, cancellationToken)
                .ConfigureAwait(false);

            if (reclaimed > 0)
            {
                _logger.LogInformation(
                    "남아 있던 캐시를 정리했습니다. ({Megabytes:0.#}MB, 작업 {Count}건 유지)",
                    reclaimed / 1024d / 1024d,
                    known.Length);
            }
            else
            {
                _logger.LogDebug("정리할 캐시가 없습니다. (작업 {Count}건)", known.Length);
            }

            return reclaimed;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("캐시 정리가 취소되었습니다.");
            return 0L;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "남아 있던 캐시를 정리하지 못했습니다.");
            return 0L;
        }
    }

    /// <summary>Merges a scan result into the queue and persists the changes.</summary>
    public async Task<IReadOnlyList<EnqueueResult>> EnqueueAsync(
        IReadOnlyList<VideoFile> files,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        var results = new List<EnqueueResult>(files.Count);
        var toAdd = new List<Job>();
        var toUpdate = new List<Job>();
        var order = _jobs.Count;

        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var existing = _jobs.Values.FirstOrDefault(j =>
                    string.Equals(j.VideoPath, file.FullPath, StringComparison.OrdinalIgnoreCase));

                var result = JobFactory.Create(file, existing, settings, timeProvider: _timeProvider);
                results.Add(result);

                switch (result.Decision)
                {
                    case EnqueueDecision.Created or EnqueueDecision.AlreadyDone when result.Job is not null:
                        result.Job.QueueOrder = order++;
                        _jobs[result.Job.Id] = result.Job;
                        toAdd.Add(result.Job);
                        break;

                    case EnqueueDecision.Requeued when result.Job is not null:
                        toUpdate.Add(result.Job);
                        break;
                }
            }

            if (toAdd.Count > 0)
            {
                await _repository.AddRangeAsync(toAdd, cancellationToken).ConfigureAwait(false);
            }

            if (toUpdate.Count > 0)
            {
                await _repository.UpdateRangeAsync(toUpdate, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _mutationLock.Release();
        }

        foreach (var job in toAdd.Concat(toUpdate))
        {
            RaiseJobChanged(job);
        }

        return results;
    }

    /// <summary>Attaches FFprobe results to an already-queued job.</summary>
    public async Task ApplyProbeAsync(VideoFile probed, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Values.FirstOrDefault(j =>
            string.Equals(j.VideoPath, probed.FullPath, StringComparison.OrdinalIgnoreCase));

        if (job is null)
        {
            return;
        }

        job.DurationSeconds = probed.DurationSeconds;
        job.HasAudioTrack = probed.HasAudioTrack;
        job.HasEmbeddedSubtitle = probed.HasEmbeddedSubtitle;
        job.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        if (probed.ProbeError is not null && !probed.HasAudioTrack)
        {
            job.MarkFailed(ErrorCodes.VideoUnreadable, probed.ProbeError, _timeProvider);
        }

        await _repository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        RaiseJobChanged(job);
    }

    /// <summary>
    /// Records the user's per-file 자막 원본 choice.
    ///
    /// Refused while the job is running: the worker has already been handed a
    /// <c>process</c> command built from the old value, and changing it underneath would make the
    /// grid disagree with what is actually being produced. Returns false in that case.
    /// </summary>
    public async Task<bool> SetSourceOverrideAsync(
        string jobId,
        JobSourceOverride mode,
        int? audioTrackIndex = null,
        int? subtitleTrackIndex = null,
        string? subtitleLanguage = null,
        CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        if (JobStateMachine.IsActive(job.Status))
        {
            _logger.LogWarning("실행 중인 작업의 자막 원본은 바꿀 수 없습니다: {JobId}", jobId);
            return false;
        }

        if (mode == JobSourceOverride.None)
        {
            job.ClearSourceOverride();
        }
        else
        {
            job.SourceOverride = mode;
            job.SelectedAudioTrackIndex = mode == JobSourceOverride.Audio ? audioTrackIndex : null;
            job.SelectedSubtitleTrackIndex = mode == JobSourceOverride.EmbeddedSubtitle ? subtitleTrackIndex : null;

            job.SelectedSubtitleLanguage = mode == JobSourceOverride.EmbeddedSubtitle
                ? Normalise(subtitleLanguage)
                : null;
        }

        job.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await _repository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        RaiseJobChanged(job);

        _logger.LogInformation(
            "자막 원본을 변경했습니다: {JobId} → {Mode} (오디오 {Audio}, 자막 {Subtitle}, 언어 {Language})",
            jobId, job.SourceOverride, job.SelectedAudioTrackIndex, job.SelectedSubtitleTrackIndex,
            job.SelectedSubtitleLanguage ?? "-");

        return true;
    }

    /// <summary>
    /// Stores the free-text 메모 for a job. Metadata only — allowed at any time, including while the
    /// job runs, because it has no effect on the pipeline.
    /// </summary>
    public async Task SetNoteAsync(string jobId, string? note, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return;
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (string.Equals(job.Note, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        job.Note = trimmed;
        job.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await _repository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        RaiseJobChanged(job);
    }

    /// <summary>
    /// Points a job at a renamed source file. The caller has already moved the file (and any sidecar
    /// subtitles) on disk; this only updates the queue's bookkeeping so the grid and later scans stay
    /// consistent. Refused while the job is running — the worker holds a command built from the old
    /// path. Returns false when the job is gone or active.
    /// </summary>
    public async Task<bool> UpdateSourcePathAsync(
        string jobId,
        string newVideoPath,
        string? newOutputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newVideoPath);

        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        if (JobStateMachine.IsActive(job.Status))
        {
            _logger.LogWarning("실행 중인 작업의 파일 경로는 바꿀 수 없습니다: {JobId}", jobId);
            return false;
        }

        job.VideoPath = newVideoPath;
        job.FileName = Path.GetFileName(newVideoPath);
        job.OutputPath = string.IsNullOrWhiteSpace(newOutputPath) ? job.OutputPath : newOutputPath;
        job.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await _repository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        RaiseJobChanged(job);

        _logger.LogInformation("작업의 원본 파일 경로를 변경했습니다: {JobId} → {Path}", jobId, newVideoPath);
        return true;
    }

    /// <summary>Blank and the placeholder "und" both mean "no usable tag", which is stored as null.</summary>
    private static string? Normalise(string? language)
    {
        var trimmed = language?.Trim();

        return string.IsNullOrEmpty(trimmed) || trimmed.Equals("und", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    // -----------------------------------------------------------------------
    // Queue control
    // -----------------------------------------------------------------------

    /// <summary>
    /// Starts (or resumes) processing. <paramref name="jobIds"/> restricts the run to a selection;
    /// null runs everything that is pending, paused or failed-with-retry.
    /// </summary>
    public Task StartAsync(AppSettings settings, IEnumerable<string>? jobIds = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _restrictTo = jobIds is null ? [] : new HashSet<string>(jobIds, StringComparer.Ordinal);
        _pauseRequested = false;
        _runCompleted = 0;
        _runFailed = 0;
        _runCancelled = 0;
        _runCts = new CancellationTokenSource();

        var token = _runCts.Token;
        RaiseState(QueueState.Running, null);

        _pump = Task.Run(() => PumpAsync(settings, token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Finishes the current job then stops. The running job is left resumable.</summary>
    public void Pause()
    {
        if (_state != QueueState.Running)
        {
            return;
        }

        _pauseRequested = true;
        RaiseState(QueueState.Pausing, "현재 작업을 마무리하는 중입니다.");
        _runCts?.Cancel();
    }

    /// <summary>Cancels immediately. The running job becomes Cancelled but keeps its checkpoint.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        _pauseRequested = false;
        RaiseState(QueueState.Stopping, null);
        _runCts?.Cancel();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    public async Task RetryAsync(IEnumerable<string> jobIds, CancellationToken cancellationToken = default)
    {
        var changed = new List<Job>();

        foreach (var id in jobIds)
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                continue;
            }

            if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped
                or JobStatus.Completed or JobStatus.Paused))
            {
                continue;
            }

            job.RetryCount++;
            job.TransitionTo(JobStatus.Pending, _timeProvider);
            changed.Add(job);
        }

        if (changed.Count > 0)
        {
            await _repository.UpdateRangeAsync(changed, cancellationToken).ConfigureAwait(false);
            foreach (var job in changed)
            {
                RaiseJobChanged(job);
            }
        }
    }

    public async Task CancelAsync(IEnumerable<string> jobIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        var changed = new List<Job>();

        foreach (var id in jobIds)
        {
            if (!_jobs.TryGetValue(id, out var job) || JobStateMachine.IsTerminal(job.Status))
            {
                continue;
            }

            // Stop the work, not just the label. Without this a 취소 on a running row flipped the
            // status while the worker kept burning GPU on a file the user had already given up on —
            // and the pump would then write its own terminal state over the cancellation.
            if (_active.TryGetValue(id, out var active))
            {
                active.RequestCancel();
            }

            // A job still Pending never ran, so 건너뛰기 here really is a skip — nothing is being
            // abandoned mid-work. Anything else (an active stage, or Paused with progress already
            // made) is an interruption, which 취소 describes more honestly than "skipped".
            var outcome = job.Status == JobStatus.Pending ? JobStatus.Skipped : JobStatus.Cancelled;
            job.TransitionTo(outcome, _timeProvider);
            changed.Add(job);
        }

        if (changed.Count > 0)
        {
            await _repository.UpdateRangeAsync(changed, cancellationToken).ConfigureAwait(false);
            foreach (var job in changed)
            {
                RaiseJobChanged(job);
            }
        }
    }

    /// <summary>Removes completed jobs from the list and drops their checkpoints.</summary>
    public Task<JobRemovalResult> RemoveCompletedAsync(CancellationToken cancellationToken = default)
    {
        var completed = _jobs.Values.Where(j => j.Status == JobStatus.Completed).Select(j => j.Id).ToArray();
        return RemoveAsync(completed, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Removes jobs from the queue, the database and the cache.
    ///
    /// <para>The source video and any subtitle file already written are never touched: only the
    /// job record and the per-job cache directory (checkpoint, extracted audio, partial translation)
    /// go away.</para>
    ///
    /// <para>A job the pump is running is cancelled first and then waited for, bounded by
    /// <paramref name="stopTimeout"/>. Removing it while the pump still holds the
    /// <see cref="Job"/> object would let a straggling save put the row back into the database
    /// moments after it was deleted. Anything that has not stopped by the deadline stays in the queue
    /// and comes back in <see cref="JobRemovalResult.Skipped"/> rather than blocking the other
    /// removals.</para>
    /// </summary>
    /// <param name="jobIds">Ids to remove. Unknown ids are ignored.</param>
    /// <param name="stopTimeout">Budget for running jobs to stop; <see cref="DefaultRemovalStopTimeout"/> when null.</param>
    /// <param name="cancellationToken">Cancels the wait and the persistence work.</param>
    public async Task<JobRemovalResult> RemoveAsync(
        IEnumerable<string> jobIds,
        TimeSpan? stopTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        var ids = jobIds
            .Where(id => _jobs.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (ids.Length == 0)
        {
            return JobRemovalResult.Empty;
        }

        var (removable, skipped) = await StopRunningAsync(
            ids,
            stopTimeout ?? DefaultRemovalStopTimeout,
            cancellationToken).ConfigureAwait(false);

        if (removable.Count == 0)
        {
            _logger.LogWarning("실행 중이라 제거하지 못한 작업 {Count}건이 있습니다.", skipped.Count);
            return new JobRemovalResult([], skipped);
        }

        await _repository.RemoveRangeAsync(removable, cancellationToken).ConfigureAwait(false);

        foreach (var id in removable)
        {
            // Out of the queue before the cache delete: from here the job no longer exists as far as
            // the pump, the grid and RaiseJobChanged are concerned, whatever the store does next.
            _jobs.TryRemove(id, out _);

            try
            {
                await _checkpointStore.ClearAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One undeletable cache directory — antivirus, a media player still holding
                // audio.wav — must not abort the batch or leave the other rows half-removed. The
                // startup sweep (CleanupOrphanedCacheAsync) reclaims whatever is left behind.
                _logger.LogWarning(ex, "체크포인트 삭제에 실패했습니다: {JobId}", id);
            }
        }

        _logger.LogInformation(
            "작업 {Removed}건을 목록과 캐시에서 제거했습니다. (건너뜀 {Skipped}건)",
            removable.Count,
            skipped.Count);

        RaiseState(_state, null);

        return new JobRemovalResult(removable, skipped);
    }

    /// <summary>
    /// Cancels whichever of <paramref name="ids"/> the pump is running and waits, once, for all of
    /// them to stop. Splits the input into "safe to remove" and "still running".
    ///
    /// One shared deadline rather than one per job: cancelling five running jobs must not cost five
    /// timeouts, and they unwind concurrently anyway.
    /// </summary>
    private async Task<(List<string> Removable, List<string> Skipped)> StopRunningAsync(
        IReadOnlyList<string> ids,
        TimeSpan stopTimeout,
        CancellationToken cancellationToken)
    {
        var waits = new List<Task>();
        var running = new List<string>();

        foreach (var id in ids)
        {
            if (_active.TryGetValue(id, out var active))
            {
                running.Add(id);
                waits.Add(active.Finished);
            }
        }

        if (waits.Count > 0)
        {
            // CancelAsync signals the per-job token and flips the status, so the grid stops showing
            // "번역 중" while the worker unwinds.
            await CancelAsync(running, cancellationToken).ConfigureAwait(false);

            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var stopped = Task.WhenAll(waits);
            var deadline = Task.Delay(stopTimeout, deadlineCts.Token);

            await Task.WhenAny(stopped, deadline).ConfigureAwait(false);

            // Release the timer whichever way the race went; the delay task is only ever cancelled,
            // never faulted, so nothing is left unobserved.
            await deadlineCts.CancelAsync().ConfigureAwait(false);
        }

        var removable = new List<string>(ids.Count);
        var skipped = new List<string>();

        foreach (var id in ids)
        {
            if (_active.ContainsKey(id))
            {
                skipped.Add(id);
            }
            else
            {
                removable.Add(id);
            }
        }

        return (removable, skipped);
    }

    // -----------------------------------------------------------------------
    // Pump
    // -----------------------------------------------------------------------

    private async Task PumpAsync(AppSettings settings, CancellationToken token)
    {
        // Owned here, not by the lane: the pump has to be able to cancel it in the finally below,
        // and a source the lane disposed on its way out would make that Cancel() throw — inside a
        // finally, which would skip the RaiseState that returns the queue to Idle.
        using var prefetchStop = CancellationTokenSource.CreateLinkedTokenSource(token);

        var prefetch = Task.Run(
            () => RunAudioPrefetchAsync(settings, prefetchStop.Token),
            CancellationToken.None);

        // Set only when a strategy method returned without throwing and without the run token being
        // cancelled — i.e. the queue emptied on its own. A 중단 cancels the token; a 일시정지 sets
        // _pauseRequested. Either of those leaves this false and QueueDrained does not fire.
        var drainedNaturally = false;

        try
        {
            var strategy = await ResolveStrategyAsync(settings, token).ConfigureAwait(false);
            _logger.LogInformation("작업 처리 방식: {Strategy}", strategy);

            switch (strategy)
            {
                case ProcessingStrategy.TranscribeAllThenTranslate:
                    await RunTwoPassAsync(settings, token).ConfigureAwait(false);
                    break;

                case ProcessingStrategy.PipelinedParallel:
                    await RunPipelinedAsync(settings, token).ConfigureAwait(false);
                    break;

                default:
                    await RunSequentialAsync(settings, token).ConfigureAwait(false);
                    break;
            }

            drainedNaturally = !token.IsCancellationRequested && !_pauseRequested;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("작업 큐가 중단되었습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "작업 큐에서 예기치 않은 오류가 발생했습니다.");
        }
        finally
        {
            // The run token is already cancelled or the batch is done; either way the lane must not
            // outlive the pump, or the next 시작 would race a stale prefetch onto the same wav.
            await StopPrefetchAsync(prefetchStop, prefetch).ConfigureAwait(false);

            var finalState = _pauseRequested ? QueueState.Paused : QueueState.Idle;
            _pauseRequested = false;
            RaiseState(finalState, null);

            if (drainedNaturally)
            {
                var outcome = new QueueRunOutcome(_runCompleted, _runFailed, _runCancelled);
                _logger.LogInformation(
                    "작업 큐가 모두 처리되어 대기 상태로 돌아갔습니다. (완료 {Completed} · 실패 {Failed} · 취소 {Cancelled})",
                    outcome.Completed, outcome.Failed, outcome.Cancelled);

                try
                {
                    QueueDrained?.Invoke(this, new QueueDrainedEventArgs(outcome));
                }
                catch (Exception ex)
                {
                    // A misbehaving handler must not turn a clean finish into a logged pump crash.
                    _logger.LogWarning(ex, "QueueDrained 처리기에서 오류가 발생했습니다.");
                }
            }
        }
    }

    /// <summary>
    /// Stops the prefetch lane and waits for it, swallowing everything.
    ///
    /// Runs inside the pump's <c>finally</c>, so nothing in here may throw: an escaping exception
    /// would skip the state change that returns the queue to 대기 중 and leave the UI convinced a
    /// run is still going.
    /// </summary>
    private async Task StopPrefetchAsync(CancellationTokenSource stop, Task lane)
    {
        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by a concurrent StopAsync; the lane is going away regardless.
        }

        try
        {
            await lane.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: that is how the lane stops.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "음성 미리 추출 레인이 오류로 끝났습니다.");
        }
    }

    /// <summary>
    /// Extracts the audio of upcoming jobs while the pump works on the current one.
    ///
    /// <para>Orthogonal to strategies A/B/C rather than a fourth strategy, because it competes with
    /// none of them: demuxing is CPU and disk, and every strategy's expensive stages are GPU. That
    /// is also why it works on hardware strategy C is never offered on — C needs both models
    /// resident at once (16GB+), this needs no VRAM at all.</para>
    ///
    /// <para>The lookahead is bounded, and the bound costs nothing. Total time converges to
    /// <c>extract₁ + Σ(asr + translate)</c> as soon as the extractor stays ahead of the consumer,
    /// and since extraction is far quicker than transcription a depth of one already achieves that.
    /// Running further ahead buys no throughput and spends real disk — a 16 kHz mono wav is about
    /// 115MB per hour of video, so an unbounded run over a folder of two-hour files is tens of
    /// gigabytes of audio waiting to be read once.</para>
    /// </summary>
    private async Task RunAudioPrefetchAsync(AppSettings settings, CancellationToken laneToken)
    {
        var depth = settings.AudioPrefetchDepth;
        if (depth <= 0)
        {
            _logger.LogInformation("음성 미리 추출이 꺼져 있습니다. (설정값 {Depth})", depth);
            return;
        }

        var processor = _processorSelector.Select(settings);

        // Deliberately not PendingSnapshot(): that filters on "runnable", and the pump flips the
        // head job to Probing the moment it picks it up. Whether the head is still in the list
        // would then depend on which of the two loops got there first — and with it gone, index 0
        // is a file nobody has started and the lane skips it for good. Counting every non-terminal
        // job keeps the head at index 0 no matter how far the pump has got.
        var batch = UnfinishedInQueueOrder();

        // Reported at Information, not Debug. Everything this lane does is invisible by design —
        // it produces no progress, no row change and no output — so without a line here the only
        // way to tell "prefetching 5 ahead" from "doing nothing at all" is to watch the disk. The
        // count matters as much as the depth: a queue whose other files are all 완료 or 취소됨 has
        // nothing to run ahead of, and that reads exactly like a broken setting.
        if (batch.Count <= 1)
        {
            _logger.LogInformation(
                "음성 미리 추출: 앞서 처리할 작업이 없습니다. (대기 중인 작업 {Count}건, 설정 깊이 {Depth}) " +
                "완료·실패·취소된 작업은 대상이 아닙니다.",
                batch.Count,
                depth);
            return;
        }

        _logger.LogInformation(
            "음성 미리 추출을 시작합니다. (최대 {Depth}개 앞서서, 대상 {Count}건)",
            depth,
            batch.Count - 1);

        var prefetched = 0;

        // From index 1: index 0 is the job the pump is running or is about to start, and
        // prefetching it would put a second ffmpeg on the wav the job is already extracting.
        for (var i = 1; i < batch.Count && !laneToken.IsCancellationRequested; i++)
        {
            var job = batch[i];

            // Hold at `depth` files ahead of wherever the pump has got to. Counting the unfinished
            // jobs before this one measures that without the two loops having to talk.
            while (!laneToken.IsCancellationRequested && Unfinished(batch, i) > depth)
            {
                try
                {
                    await Task.Delay(PrefetchPollInterval, laneToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (laneToken.IsCancellationRequested)
            {
                return;
            }

            // Re-checked here rather than at snapshot time: minutes have passed, and the user may
            // have removed, cancelled, or already run this row.
            if (!_jobs.ContainsKey(job.Id) ||
                _active.ContainsKey(job.Id) ||
                JobStateMachine.IsTerminal(job.Status))
            {
                continue;
            }

            try
            {
                var outcome = await processor
                    .PrefetchAudioAsync(job, settings, laneToken)
                    .ConfigureAwait(false);

                switch (outcome)
                {
                    case AudioPrefetchOutcome.Extracted:
                        prefetched++;
                        _logger.LogInformation(
                            "음성을 미리 추출했습니다: {FileName} ({Done}번째)", job.FileName, prefetched);
                        MarkAudioReady(job);
                        break;

                    case AudioPrefetchOutcome.AlreadyPresent:
                        _logger.LogInformation(
                            "이미 추출된 음성이 있어 그대로 씁니다: {FileName}", job.FileName);
                        MarkAudioReady(job);
                        break;

                    default:
                        // Not an error — an embedded-subtitle job, a worker that never came up, a
                        // pre-1.3 worker. Worth a line anyway: a lane that quietly does nothing at
                        // all is indistinguishable from one that is not running.
                        _logger.LogInformation("음성 미리 추출을 건너뛰었습니다: {FileName}", job.FileName);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Best effort by contract. A lookahead failure must never take the run down.
                _logger.LogWarning(ex, "음성 미리 추출에 실패했습니다: {FileName}", job.FileName);
            }
        }
    }

    /// <summary>
    /// Shows on the row that this file's audio is ready before the pump has reached it.
    ///
    /// <para>The status stays 대기: the job is not running, and saying otherwise would make 취소 and
    /// 재시도 disagree with the grid. Only the stage and the bar move, which is exactly the amount
    /// of work that has genuinely been done — without it the lane is invisible and a user watching
    /// a row sit at 0% has no way to tell prefetching from doing nothing.</para>
    ///
    /// <para>Deliberately not <see cref="Job.ReportProgress"/>: that drags the status forward with
    /// the stage, which is the one thing that must not happen here.</para>
    /// </summary>
    private void MarkAudioReady(Job job)
    {
        if (JobStateMachine.IsTerminal(job.Status) || _active.ContainsKey(job.Id))
        {
            return;
        }

        job.CurrentStage = JobStage.ExtractingAudio;
        job.StageProgress = 100d;
        job.OverallProgress = ProgressCalculator.Overall(JobStage.ExtractingAudio, 100d);

        RaiseJobChanged(job);
    }

    /// <summary>
    /// Every job the run still has to get through, in the order the pump will take them.
    ///
    /// Includes the one already in flight, which is the difference from
    /// <see cref="PendingSnapshot"/> and the whole reason this exists.
    /// </summary>
    private IReadOnlyList<Job> UnfinishedInQueueOrder() =>
        _jobs.Values
            .Where(j => _restrictTo.Count == 0 || _restrictTo.Contains(j.Id))
            .Where(j => !JobStateMachine.IsTerminal(j.Status))
            .OrderBy(j => j.QueueOrder)
            .ThenBy(j => j.CreatedAtUtc)
            .ToArray();

    /// <summary>How many jobs before <paramref name="index"/> have not reached a terminal state.</summary>
    private static int Unfinished(IReadOnlyList<Job> batch, int index)
    {
        var count = 0;

        for (var i = 0; i < index; i++)
        {
            if (!JobStateMachine.IsTerminal(batch[i].Status))
            {
                count++;
            }
        }

        return count;
    }

    private async Task<ProcessingStrategy> ResolveStrategyAsync(AppSettings settings, CancellationToken token)
    {
        if (settings.ProcessingStrategy != ProcessingStrategy.Auto)
        {
            return settings.ProcessingStrategy;
        }

        var recommendation = await _hardwareService.GetRecommendationAsync(token).ConfigureAwait(false);
        return recommendation.Strategy;
    }

    /// <summary>Strategy A: one file at a time, all stages.</summary>
    private async Task RunSequentialAsync(AppSettings settings, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var job = NextPending();
            if (job is null)
            {
                return;
            }

            await RunJobAsync(job, settings, JobPhase.Full, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Strategy B: transcribe every queued file (Whisper stays hot), then translate every file
    /// (Whisper is unloaded before the translation model loads).
    /// </summary>
    private async Task RunTwoPassAsync(AppSettings settings, CancellationToken token)
    {
        var batch = PendingSnapshot();

        foreach (var job in batch)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            await RunJobAsync(job, settings, JobPhase.TranscribeOnly, token).ConfigureAwait(false);
        }

        foreach (var job in batch)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            // Skip anything that failed or was cancelled during pass 1.
            if (job.Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Completed)
            {
                continue;
            }

            await RunJobAsync(job, settings, JobPhase.TranslateAndWrite, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Strategy C: two lanes. Transcription of file N+1 overlaps translation of file N.
    /// Only selected when the hardware check said both models fit in VRAM simultaneously.
    /// </summary>
    private async Task RunPipelinedAsync(AppSettings settings, CancellationToken token)
    {
        var batch = PendingSnapshot();
        var handoff = new Queue<Job>();
        Task translateTask = Task.CompletedTask;

        foreach (var job in batch)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            await RunJobAsync(job, settings, JobPhase.TranscribeOnly, token).ConfigureAwait(false);

            if (job.Status is JobStatus.Failed or JobStatus.Cancelled)
            {
                continue;
            }

            handoff.Enqueue(job);

            // Wait for the previous translation before starting the next one: translations are
            // serialised against each other, but they run alongside the next transcription.
            await translateTask.ConfigureAwait(false);

            var next = handoff.Dequeue();
            translateTask = RunJobAsync(next, settings, JobPhase.TranslateAndWrite, token);
        }

        await translateTask.ConfigureAwait(false);

        while (handoff.Count > 0 && !token.IsCancellationRequested)
        {
            await RunJobAsync(handoff.Dequeue(), settings, JobPhase.TranslateAndWrite, token).ConfigureAwait(false);
        }
    }

    private async Task RunJobAsync(Job job, AppSettings settings, JobPhase phase, CancellationToken token)
    {
        // Strategies B and C snapshot the pending set once, so a job the user removed in the
        // meantime would still be handed to a processor. The queue is the authority on what exists.
        if (!_jobs.ContainsKey(job.Id))
        {
            return;
        }

        var processor = _processorSelector.Select(settings);
        var stopwatch = Stopwatch.StartNew();
        var lastPersist = TimeSpan.Zero;

        // Registered for exactly as long as a processor is running, so 취소 and 제거 can stop this one
        // job and wait for it, instead of stopping the whole run or ripping the row out mid-flight.
        var active = new ActiveJob(token);
        _active[job.Id] = active;
        var jobToken = active.Token;

        // Deliberately NOT System.Progress<T>: that type posts every callback to the captured
        // SynchronizationContext (here, the thread pool), so reports arrive out of order and can land
        // *after* the result has already been applied — leaving a Completed job displaying
        // "음성 인식 중 65%". Reporting inline keeps ordering, and the gate stops late reports from a
        // straggling stage from overwriting the terminal state.
        var acceptProgress = new ProgressGate();

        var progress = new InlineProgress<JobProgress>(update =>
        {
            if (!acceptProgress.IsOpen || !_jobs.ContainsKey(update.JobId))
            {
                return;
            }

            job.ReportProgress(update.Stage, update.StageProgress, _timeProvider);

            if (update.Speed is > 0)
            {
                job.ProcessingSpeed = update.Speed.Value;
            }

            if (!string.IsNullOrEmpty(update.DetectedLanguage))
            {
                job.DetectedLanguage = update.DetectedLanguage;
                job.LanguageProbability = update.LanguageProbability;
            }

            job.EstimatedTimeRemaining = ProgressCalculator.EstimateRemaining(job.OverallProgress, stopwatch.Elapsed);

            // Progress arrives many times a second; the database only needs it every couple of
            // seconds so a crash resumes from roughly the right place.
            if (stopwatch.Elapsed - lastPersist > TimeSpan.FromSeconds(2))
            {
                lastPersist = stopwatch.Elapsed;
                _ = PersistQuietlyAsync(job);
            }

            RaiseJobChanged(job);
        });

        try
        {
            // Pass 2 of the two-pass strategies starts at 번역 중, everything else at 검사 중. Every
            // status the pump can hand in here — Pending, Paused, or the stage pass 1 left behind —
            // may legally enter either, so this is a real transition now. It used to be silently
            // skipped whenever the start status was Translating, which left the row claiming to be
            // in a stage it had already finished.
            var startStage = phase == JobPhase.TranslateAndWrite ? JobStage.Translating : JobStage.Probing;
            var startStatus = JobStateMachine.StatusForStage(startStage);

            if (JobStateMachine.CanTransition(job.Status, startStatus))
            {
                job.TransitionTo(startStatus, _timeProvider);
                job.CurrentStage = startStage;
            }

            await PersistQuietlyAsync(job).ConfigureAwait(false);
            RaiseJobChanged(job);

            var result = await processor.ProcessAsync(job, settings, phase, progress, jobToken).ConfigureAwait(false);

            // Close the gate before the terminal transition so no in-flight report can undo it.
            acceptProgress.Close();
            await ApplyResultAsync(job, settings, phase, result, jobToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            acceptProgress.Close();
            var next = _pauseRequested ? JobStatus.Paused : JobStatus.Cancelled;

            if (JobStateMachine.CanTransition(job.Status, next))
            {
                job.TransitionTo(next, _timeProvider);
            }

            await PersistQuietlyAsync(job).ConfigureAwait(false);
            RaiseJobChanged(job);
        }
        catch (Exception ex)
        {
            acceptProgress.Close();
            _logger.LogError(ex, "작업 처리 중 오류가 발생했습니다: {JobId}", job.Id);
            job.MarkFailed(ErrorCodes.Unknown, UserFacingErrors.Describe(ErrorCodes.Unknown), _timeProvider);
            await PersistQuietlyAsync(job).ConfigureAwait(false);
            RaiseJobChanged(job);
        }
        finally
        {
            // Deregister before signalling: a remover that sees Finished complete must then find the
            // id gone from _active, or it would classify a finished job as "still running".
            _active.TryRemove(job.Id, out _);
            active.Complete();
            active.Dispose();

            // The one place every terminal outcome funnels through, whichever branch above set it.
            // Strategies B and C call this method twice per job, but pass 1 leaves an active status
            // behind, so a job is tallied exactly once — on the pass that finishes it.
            RecordRunOutcome(job.Status);
        }
    }

    /// <summary>Bumps the per-run tally that <see cref="QueueDrained"/> reports.</summary>
    private void RecordRunOutcome(JobStatus status)
    {
        switch (status)
        {
            case JobStatus.Completed:
                Interlocked.Increment(ref _runCompleted);
                break;
            case JobStatus.Failed:
                Interlocked.Increment(ref _runFailed);
                break;
            case JobStatus.Cancelled:
                Interlocked.Increment(ref _runCancelled);
                break;
        }
    }

    private async Task ApplyResultAsync(
        Job job,
        AppSettings settings,
        JobPhase phase,
        JobExecutionResult result,
        CancellationToken token)
    {
        if (result.Cancelled)
        {
            var next = _pauseRequested ? JobStatus.Paused : JobStatus.Cancelled;
            if (JobStateMachine.CanTransition(job.Status, next))
            {
                job.TransitionTo(next, _timeProvider);
            }
        }
        else if (result.Success)
        {
            job.DetectedLanguage ??= result.SourceLanguage;
            job.WhisperModel = result.WhisperModel ?? job.WhisperModel;
            job.TranslationModel = result.TranslationModel ?? job.TranslationModel;
            job.TranslationEngine = result.TranslationEngine ?? job.TranslationEngine;

            if (phase == JobPhase.TranscribeOnly)
            {
                // Pass 1 finished: park the job so pass 2 can pick it up.
                job.CurrentStage = JobStage.Transcribing;
                job.StageProgress = 100d;
                job.OverallProgress = ProgressCalculator.Overall(JobStage.Transcribing, 100d);
                job.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            }
            else
            {
                job.OutputPath = result.OutputPath ?? job.OutputPath;
                CompleteSuccessfully(job);
            }
        }
        else
        {
            var code = result.ErrorCode ?? ErrorCodes.Unknown;
            var message = result.ErrorMessage ?? UserFacingErrors.Describe(code);

            var shouldAutoRetry = settings.AutoRetryOnRecoverableError
                                  && (result.Recoverable || ErrorCodes.IsAutoRetryable(code))
                                  && job.RetryCount == 0;

            if (shouldAutoRetry)
            {
                _logger.LogWarning("복구 가능한 오류로 작업을 한 번 자동 재시도합니다. {JobId} {Code}", job.Id, code);
                job.RetryCount++;

                // Back into the queue first: Pending is what clears the failed attempt's error text
                // and its stale progress. This is the transition that used to throw — the table had
                // no Probing → Pending edge, the exception was swallowed by the generic handler in
                // RunJobAsync, and the job ended up marked UNKNOWN. Automatic retry never once ran.
                job.TransitionTo(JobStatus.Pending, _timeProvider);
                await PersistQuietlyAsync(job).ConfigureAwait(false);
                RaiseJobChanged(job);

                // ...and straight back out again, so the row shows the stage the retry is really in
                // instead of sitting on "대기 중" for however long the second attempt takes.
                var retryStage = phase == JobPhase.TranslateAndWrite ? JobStage.Translating : JobStage.Probing;
                job.EnterStage(retryStage, _timeProvider);
                await PersistQuietlyAsync(job).ConfigureAwait(false);
                RaiseJobChanged(job);

                var processor = _processorSelector.Select(settings);
                var retryGate = new ProgressGate();
                var retryProgress = new InlineProgress<JobProgress>(update =>
                {
                    if (!retryGate.IsOpen)
                    {
                        return;
                    }

                    job.ReportProgress(update.Stage, update.StageProgress, _timeProvider);
                    RaiseJobChanged(job);
                });

                var retryResult = await processor
                    .ProcessAsync(job, settings, phase, retryProgress, token)
                    .ConfigureAwait(false);

                retryGate.Close();

                if (retryResult.Success || retryResult.Cancelled)
                {
                    await ApplyResultAsync(job, settings, phase, retryResult, token).ConfigureAwait(false);
                    return;
                }

                code = retryResult.ErrorCode ?? code;
                message = retryResult.ErrorMessage ?? message;
            }

            job.MarkFailed(code, message, _timeProvider);
        }

        await PersistQuietlyAsync(job).ConfigureAwait(false);
        RaiseJobChanged(job);
    }

    /// <summary>
    /// Walks a successful job to <see cref="JobStatus.Completed"/> by way of
    /// <see cref="JobStatus.WritingSubtitle"/>.
    ///
    /// <para>Two steps, not one, and both unguarded: <see cref="JobStatus.Completed"/> is only
    /// reachable from <see cref="JobStatus.WritingSubtitle"/>, so a job can never be marked done
    /// while its status still claims it is transcribing. Every non-terminal status can legally reach
    /// 자막 저장 중, which is why neither call needs a <c>CanTransition</c> guard — and a guard is
    /// exactly what used to hide the fact that the step was being skipped.</para>
    ///
    /// <para>A 취소 that landed while the processor was returning is the user's decision and wins: a
    /// late success must not resurrect a row the user has already given up on.</para>
    /// </summary>
    private void CompleteSuccessfully(Job job)
    {
        if (JobStateMachine.IsTerminal(job.Status))
        {
            _logger.LogInformation(
                "이미 종료된 작업이라 완료 처리를 건너뜁니다: {JobId} ({Status})", job.Id, job.Status);
            return;
        }

        job.TransitionTo(JobStatus.WritingSubtitle, _timeProvider);
        job.TransitionTo(JobStatus.Completed, _timeProvider);

        ReclaimAudio(job);
    }

    /// <summary>
    /// Drops the extracted wav once the subtitle exists.
    ///
    /// <para>The wav is the only large artefact a finished job leaves behind — about 115MB per hour
    /// of video — and nothing will ever read it again: a 재시도 that changes the translation engine
    /// resumes from <c>transcription.json</c>, and one that changes the ASR settings re-extracts
    /// anyway because the audio fingerprint no longer matches. Keeping it turned a 147-file folder
    /// into tens of gigabytes of dead audio.</para>
    ///
    /// <para>Fire-and-forget, and silent on failure: this is housekeeping running immediately after
    /// a success, and a locked file must not turn a completed job into a failed one.</para>
    /// </summary>
    private void ReclaimAudio(Job job)
    {
        var jobId = job.Id;
        var logger = _logger;
        var store = _checkpointStore;

        _ = Task.Run(async () =>
        {
            try
            {
                var reclaimed = await store.DeleteAudioAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                if (reclaimed > 0)
                {
                    logger.LogInformation(
                        "완료된 작업의 추출 음성을 정리했습니다: {FileName} ({Megabytes:0.#}MB)",
                        job.FileName,
                        reclaimed / 1024d / 1024d);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "추출 음성 정리에 실패했습니다: {JobId}", jobId);
            }
        });
    }

    private Job? NextPending()
    {
        return _jobs.Values
            .Where(IsRunnable)
            .OrderBy(j => j.QueueOrder)
            .ThenBy(j => j.CreatedAtUtc)
            .FirstOrDefault();
    }

    private IReadOnlyList<Job> PendingSnapshot() =>
        _jobs.Values
            .Where(IsRunnable)
            .OrderBy(j => j.QueueOrder)
            .ThenBy(j => j.CreatedAtUtc)
            .ToArray();

    private bool IsRunnable(Job job)
    {
        if (_restrictTo.Count > 0 && !_restrictTo.Contains(job.Id))
        {
            return false;
        }

        return job.Status is JobStatus.Pending or JobStatus.Paused;
    }

    private async Task PersistQuietlyAsync(Job job)
    {
        // A job removed while the pump was mid-flight is gone; a straggling save would write it
        // straight back into the database and it would reappear on the next start.
        if (!_jobs.ContainsKey(job.Id))
        {
            return;
        }

        try
        {
            await _repository.UpdateAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "작업 상태 저장에 실패했습니다: {JobId}", job.Id);
        }
    }

    /// <summary>
    /// Announces a job the queue still owns. The membership check is the other half of the removal
    /// guard: the grid creates a row for any id it hears about, so announcing a removed job would put
    /// its row straight back on screen.
    /// </summary>
    private void RaiseJobChanged(Job job)
    {
        if (!_jobs.ContainsKey(job.Id))
        {
            return;
        }

        JobChanged?.Invoke(this, new JobChangedEventArgs(job));
    }

    private void RaiseState(QueueState state, string? message)
    {
        _state = state;
        StateChanged?.Invoke(this, new QueueStateChangedEventArgs(state, message));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disposal must not throw.
        }

        _runCts?.Dispose();
        _mutationLock.Dispose();
    }

    /// <summary>
    /// One job the pump is executing right now: how to stop just that job, and when it actually
    /// stopped.
    ///
    /// The token is linked to the run token, so 중단 and 일시정지 still cancel everything, while
    /// <see cref="RequestCancel"/> reaches a single job. <see cref="Finished"/> is what makes
    /// removal safe — it completes only after the pump has written the job's final state, so the row
    /// can be deleted with nothing left in flight behind it.
    /// </summary>
    private sealed class ActiveJob(CancellationToken runToken) : IDisposable
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);

        private readonly TaskCompletionSource _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token => _cts.Token;

        public Task Finished => _finished.Task;

        public void RequestCancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The pump finished between the lookup and here. Nothing left to cancel, and the
                // caller's wait on Finished has already completed.
            }
        }

        public void Complete() => _finished.TrySetResult();

        public void Dispose() => _cts.Dispose();
    }
}
