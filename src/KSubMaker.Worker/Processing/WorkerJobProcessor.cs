using System.Diagnostics;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;
using KSubMaker.Worker.Process;
using KSubMaker.WorkerProtocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KSubMaker.Worker.Processing;

/// <summary>
/// Drives one job through the Python worker.
///
/// The worker process is shared and long-lived on purpose: starting CPython and loading a Whisper
/// model costs tens of seconds, so paying that per file would make a 200-file batch unusable. This
/// class therefore never owns the process — it only makes sure it is up, sends one
/// <see cref="ProcessCommand"/>, and translates the resulting event stream into
/// <see cref="JobProgress"/> reports and a single <see cref="JobExecutionResult"/>.
/// </summary>
public sealed class WorkerJobProcessor : IJobProcessor
{
    private readonly IWorkerClient _client;
    private readonly IAppPaths _paths;
    private readonly WorkerOptions _options;
    private readonly HardwareService? _hardware;
    private readonly ModelCatalog _catalog;
    private readonly ILogger<WorkerJobProcessor> _logger;

    /// <summary>Makes concurrent first-callers start the worker exactly once.</summary>
    private readonly SemaphoreSlim _startGate = new(1, 1);

    /// <summary>
    /// How long a prefetch waits for the pump to bring the worker up before giving up on this file.
    /// Generous, because it is covering a cold CPython start plus the handshake, and the cost of
    /// waiting too long is only a prefetch that lands late.
    /// </summary>
    private static readonly TimeSpan PrefetchWorkerWait = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan PrefetchWorkerPoll = TimeSpan.FromMilliseconds(250);

    public WorkerJobProcessor(
        IWorkerClient client,
        IAppPaths paths,
        IOptions<WorkerOptions> options,
        ILogger<WorkerJobProcessor> logger,
        HardwareService? hardware = null,
        ModelCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client ?? throw new ArgumentNullException(nameof(client));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value ?? new WorkerOptions();

        // Optional: the processor works perfectly well without it. Its only purpose is to give the
        // hardware profile a chance to pick up the worker's authoritative CUDA answer at the one
        // moment it is free — right after the worker has come up for a job.
        _hardware = hardware;

        // Immutable data, so falling back to the built-in set is always correct — unlike the
        // hardware service there is no "unwired" state to worry about.
        _catalog = catalog ?? new ModelCatalog();
    }

    public string Name => "Python AI Worker";

    public async Task<JobExecutionResult> ProcessAsync(
        Job job,
        AppSettings settings,
        JobPhase phase,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledResult();
        }

        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CancelledResult();
        }
        catch (WorkerException ex)
        {
            _logger.LogError(ex, "worker를 시작하지 못했습니다.");
            return JobExecutionResult.Fail(
                ex.ErrorCode,
                UserFacingErrors.Describe(ex.ErrorCode, ex.Message),
                ex.Recoverable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "worker를 시작하지 못했습니다.");
            return JobExecutionResult.Fail(
                ErrorCodes.WorkerCrashed,
                UserFacingErrors.Describe(ErrorCodes.WorkerCrashed),
                recoverable: true);
        }

        var command = BuildCommand(job, settings, phase);
        var stopwatch = Stopwatch.StartNew();

        var completion = new TaskCompletionSource<JobExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new JobState(job.Id);

        void OnEvent(object? sender, WorkerEvent workerEvent) =>
            HandleEvent(workerEvent, job, settings, state, progress, completion);

        void OnExited(object? sender, WorkerExitedEventArgs args)
        {
            if (args.Expected)
            {
                // Graceful shutdown while a job was running still means the job did not finish.
                completion.TrySetResult(CancelledResult());
                return;
            }

            _logger.LogError(
                "작업 도중 worker가 종료되었습니다. (작업 {JobId}, 종료 코드 {ExitCode}) {StandardError}",
                job.Id, args.ExitCode, args.LastStandardError ?? "-");

            completion.TrySetResult(JobExecutionResult.Fail(
                ErrorCodes.WorkerCrashed,
                UserFacingErrors.Describe(ErrorCodes.WorkerCrashed),
                recoverable: true));
        }

        EventHandler<WorkerEvent> eventHandler = OnEvent;
        EventHandler<WorkerExitedEventArgs> exitHandler = OnExited;

        _client.EventReceived += eventHandler;
        _client.Exited += exitHandler;

        var cancellationRegistration = default(CancellationTokenRegistration);

        try
        {
            // Sent with CancellationToken.None on purpose: a half-written command line would leave the
            // worker's readline() desynchronised. Cancellation is handled by the `cancel` command below.
            await _client.SendAsync(command, CancellationToken.None).ConfigureAwait(false);

            cancellationRegistration = cancellationToken.Register(() =>
            {
                // The registration callback runs on whoever cancelled the token (often the UI thread);
                // it must return immediately and must never throw.
                _ = Task.Run(() => RequestCancellationAsync(job.Id, completion));
            });

            var result = await completion.Task.ConfigureAwait(false);

            _logger.LogInformation(
                "worker 작업 종료: {JobId} (성공 {Success}, 취소 {Cancelled}, {Elapsed:0.0}초)",
                job.Id, result.Success, result.Cancelled, stopwatch.Elapsed.TotalSeconds);

            return result;
        }
        catch (WorkerException ex)
        {
            _logger.LogError(ex, "worker 통신 실패: {JobId}", job.Id);
            return JobExecutionResult.Fail(
                ex.ErrorCode,
                UserFacingErrors.Describe(ex.ErrorCode, ex.Message),
                ex.Recoverable);
        }
        catch (OperationCanceledException)
        {
            return CancelledResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "작업 처리 중 예기치 못한 오류: {JobId}", job.Id);
            return JobExecutionResult.Fail(ErrorCodes.Unknown, UserFacingErrors.Describe(ErrorCodes.Unknown));
        }
        finally
        {
            // Unsubscribing here is what keeps a long-lived shared client from accumulating handlers
            // (and from pushing a later job's events into this job's progress reporter).
            cancellationRegistration.Dispose();
            _client.EventReceived -= eventHandler;
            _client.Exited -= exitHandler;
        }
    }

    /// <inheritdoc />
    public async Task<AudioPrefetchOutcome> PrefetchAudioAsync(
        Job job,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(settings);

        if (cancellationToken.IsCancellationRequested)
        {
            return AudioPrefetchOutcome.NotAttempted;
        }

        // Wait for the worker rather than skipping this file.
        //
        // The lane starts at the same instant the pump does, and the pump needs seconds to boot
        // CPython and finish the handshake. Returning false here on "not running yet" meant the
        // lane walked straight past its first `depth` files and never came back to them — so the
        // deeper the user set the lookahead, the *more* files went unprefetched. Waiting is always
        // bounded in practice: the pump is starting the worker right now, and if it never does,
        // the lane's token is cancelled with the run.
        if (!await WaitForWorkerAsync(cancellationToken).ConfigureAwait(false))
        {
            return AudioPrefetchOutcome.NotAttempted;
        }

        var source = ResolveSource(job, settings);
        if (!string.Equals(source.Mode, SourceModes.Audio, StringComparison.Ordinal))
        {
            return AudioPrefetchOutcome.NotAttempted;
        }

        var command = new ExtractAudioCommand
        {
            JobId = job.Id,
            VideoPath = job.VideoPath,
            CheckpointDir = _paths.JobCacheDirectory(job.Id),
            Settings = BuildWorkerSettings(settings),
            SourceMode = source.Mode,
            AudioTrackIndex = source.AudioTrackIndex
        };

        try
        {
            var completed = await _client
                .RequestAsync<CompletedEvent>(command, cancellationToken)
                .ConfigureAwait(false);

            // `skipped` is the worker saying the work was not needed — the wav an earlier run left
            // behind is still good. Reporting that as an extraction is what made the logs unusable
            // for deciding whether the lane was doing anything.
            return completed.Skipped
                ? AudioPrefetchOutcome.AlreadyPresent
                : AudioPrefetchOutcome.Extracted;
        }
        catch (WorkerRequestFailedException ex)
        {
            // Includes PROTOCOL_ERROR from a pre-1.3 worker that has never heard of extractAudio,
            // and from a worker whose lane is already busy. Both mean "not prefetched", which is a
            // slower job rather than a broken one.
            // Information, not Debug: a pre-1.3 worker answers PROTOCOL_ERROR here and the whole
            // feature then does nothing, which must not be silent.
            _logger.LogInformation(
                "worker가 음성 미리 추출을 거절했습니다: {JobId} ({Code})", job.Id, ex.ErrorCode);
            return AudioPrefetchOutcome.NotAttempted;
        }
        catch (OperationCanceledException)
        {
            return AudioPrefetchOutcome.NotAttempted;
        }
        catch (Exception ex)
        {
            // Never fatal, by contract. The queue must not lose a run because a lookahead failed.
            _logger.LogDebug(ex, "음성 미리 추출에 실패했습니다: {JobId}", job.Id);
            return AudioPrefetchOutcome.NotAttempted;
        }
    }

    /// <summary>
    /// Waits until the worker is up, without ever starting it.
    ///
    /// <para>Starting it is the pump's job. A prefetch is never a reason to pay the CPython startup
    /// cost on its own — if no job is running there is nothing to run ahead of, and the file would
    /// be extracted by the job that eventually reaches it anyway.</para>
    /// </summary>
    /// <returns>False if the wait was cancelled or the worker never came up.</returns>
    private async Task<bool> WaitForWorkerAsync(CancellationToken cancellationToken)
    {
        if (_client.IsRunning)
        {
            return true;
        }

        var deadline = DateTimeOffset.UtcNow + PrefetchWorkerWait;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(PrefetchWorkerPoll, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (_client.IsRunning)
            {
                return true;
            }
        }

        _logger.LogDebug("worker가 아직 준비되지 않아 미리 추출을 건너뜁니다.");
        return false;
    }

    // -----------------------------------------------------------------------
    // worker lifetime
    // -----------------------------------------------------------------------

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsRunning)
        {
            return;
        }

        var started = false;

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client.IsRunning)
            {
                return;
            }

            var ready = await _client.StartAsync(cancellationToken).ConfigureAwait(false);
            started = true;
            _logger.LogInformation(
                "AI 작업 프로세스를 시작했습니다. (worker {Worker}, python {Python})",
                ready.WorkerVersion ?? "?", ready.PythonVersion ?? "?");
        }
        finally
        {
            _startGate.Release();
        }

        if (started)
        {
            await RefreshHardwareFromWorkerAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the freshly-started worker what the host could only guess at, and lets
    /// <see cref="HardwareService"/> re-raise its profile.
    ///
    /// Done here, before the <c>process</c> command goes out, rather than concurrently with the job:
    /// the worker reads stdin on one thread, so a <c>detectHardware</c> that takes ten seconds to
    /// import torch would delay a <c>cancel</c> by the same amount. Sequencing it before the job
    /// costs the same wall clock but keeps cancellation instant. It runs at most once per worker
    /// lifetime, and a failure is swallowed — the job matters, the hardware readout does not.
    /// </summary>
    private async Task RefreshHardwareFromWorkerAsync(CancellationToken cancellationToken)
    {
        if (_hardware is null || _hardware.HasWorkerAnswer)
        {
            return;
        }

        try
        {
            await _hardware.RefreshFromWorkerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "worker 하드웨어 확인을 건너뜁니다.");
        }
    }

    /// <summary>
    /// Cooperative cancellation: ask, wait, then kill. Never throws — it runs detached from the
    /// awaiting caller.
    /// </summary>
    private async Task RequestCancellationAsync(string jobId, TaskCompletionSource<JobExecutionResult> completion)
    {
        try
        {
            _logger.LogInformation("작업 취소를 요청합니다: {JobId}", jobId);
            await _client.SendAsync(new CancelCommand { JobId = jobId }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cancel 명령을 보내지 못했습니다: {JobId}", jobId);
        }

        try
        {
            using var delayCts = new CancellationTokenSource();
            var delay = Task.Delay(_options.CancellationGraceTimeout, delayCts.Token);
            var finished = await Task.WhenAny(completion.Task, delay).ConfigureAwait(false);

            if (finished == completion.Task)
            {
                await delayCts.CancelAsync().ConfigureAwait(false);
                return; // The worker acknowledged with `cancelled` (or finished anyway).
            }

            _logger.LogWarning(
                "{Seconds:0}초 안에 취소 응답이 없어 worker 프로세스를 강제 종료합니다: {JobId}",
                _options.CancellationGraceTimeout.TotalSeconds, jobId);

            // TimeSpan.Zero == skip the graceful wait and go straight to KillTree; the worker is stuck
            // inside a native call that cannot be interrupted any other way.
            await _client.StopAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "취소 처리 중 오류가 발생했습니다: {JobId}", jobId);
        }
        finally
        {
            completion.TrySetResult(CancelledResult());
        }
    }

    // -----------------------------------------------------------------------
    // event → progress translation
    // -----------------------------------------------------------------------

    private void HandleEvent(
        WorkerEvent workerEvent,
        Job job,
        AppSettings settings,
        JobState state,
        IProgress<JobProgress> progress,
        TaskCompletionSource<JobExecutionResult> completion)
    {
        // The shared client multiplexes every job; anything that is not ours (or not job-scoped) is
        // none of our business.
        if (workerEvent.JobId is not null && !string.Equals(workerEvent.JobId, state.JobId, StringComparison.Ordinal))
        {
            return;
        }

        switch (workerEvent)
        {
            case StartedEvent started:
                if (started.ResumedFromStage is { Length: > 0 } resumed)
                {
                    _logger.LogInformation("체크포인트에서 이어서 진행합니다: {Stage} ({JobId})", resumed, state.JobId);
                    state.Stage = MapStage(resumed);
                }

                Report(progress, state, state.Stage, 0d, null, null);
                break;

            case ProgressEvent progressEvent:
                state.Stage = MapStage(progressEvent.Stage);
                Report(
                    progress,
                    state,
                    state.Stage,
                    progressEvent.StageProgress,
                    // 0 is indistinguishable from "field not sent"; the calculator gives the same
                    // answer at 0 anyway, so treat it as absent.
                    progressEvent.OverallProgress > 0d ? progressEvent.OverallProgress : null,
                    progressEvent.Speed,
                    progressEvent.Message);
                break;

            case LanguageDetectedEvent language:
                state.DetectedLanguage = language.Language;
                state.LanguageProbability = language.Probability;
                _logger.LogInformation(
                    "언어 감지: {Language} ({Probability:P0}) ({JobId})",
                    language.Language, language.Probability, state.JobId);
                Report(progress, state, state.Stage, state.StageProgress, null, null);
                break;

            case StageCompletedEvent stageCompleted:
                var completed = MapStage(stageCompleted.Stage);
                Report(progress, state, completed, 100d, null, null);
                state.Stage = completed;
                break;

            case CompletedEvent done:
                completion.TrySetResult(BuildSuccess(done, settings, state));
                break;

            case ErrorEvent error:
                completion.TrySetResult(BuildFailure(error, job));
                break;

            case CancelledEvent:
                _logger.LogInformation("worker가 작업 취소를 확인했습니다: {JobId}", state.JobId);
                completion.TrySetResult(CancelledResult());
                break;
        }
    }

    private void Report(
        IProgress<JobProgress> progress,
        JobState state,
        JobStage stage,
        double stageProgress,
        double? overallProgress,
        double? speed,
        string? message = null)
    {
        state.StageProgress = stageProgress;

        try
        {
            progress.Report(new JobProgress
            {
                JobId = state.JobId,
                Stage = stage,
                StageProgress = stageProgress,
                // The worker's own overall figure wins when it sends one; otherwise fall back to the
                // host's stage weights so the bar keeps the same meaning either way.
                OverallProgress = overallProgress ?? ProgressCalculator.Overall(stage, stageProgress),
                Speed = speed,
                Message = message,
                DetectedLanguage = state.DetectedLanguage,
                LanguageProbability = state.LanguageProbability
            });
        }
        catch (Exception ex)
        {
            // This runs on the client's reader task; a throwing progress sink must not reach it.
            _logger.LogWarning(ex, "진행률 보고 중 예외가 발생했습니다: {JobId}", state.JobId);
        }
    }

    private JobExecutionResult BuildSuccess(CompletedEvent done, AppSettings settings, JobState state) => new()
    {
        Success = true,
        OutputPath = done.OutputPath,
        CueCount = done.CueCount,
        SourceLanguage = done.SourceLanguage ?? state.DetectedLanguage,
        WhisperModel = done.WhisperModel,
        TranslationModel = done.TranslationModel,
        TranslationEngine = ParseEngine(done.TranslationEngine) ?? settings.TranslationEngine,
        Skipped = done.Skipped
    };

    private JobExecutionResult BuildFailure(ErrorEvent error, Job job)
    {
        var known = ErrorCodes.All.Contains(error.Code, StringComparer.Ordinal);
        var code = known ? error.Code : ErrorCodes.Unknown;

        _logger.LogError(
            "worker 오류: {Code} {Message} (작업 {JobId}) {Detail}",
            error.Code, error.Message, job.Id, error.Detail ?? "-");

        // error.Detail is technical and never shown; error.Message is the worker's own Korean nuance
        // (which model, which segment), which is worth appending to the canonical sentence.
        var detail = string.IsNullOrWhiteSpace(error.Message) ? null : error.Message.Trim();

        return JobExecutionResult.Fail(
            code,
            UserFacingErrors.Describe(code, detail),
            error.Recoverable || ErrorCodes.IsAutoRetryable(code));
    }

    private static JobExecutionResult CancelledResult() => new()
    {
        Success = false,
        Cancelled = true,
        ErrorCode = ErrorCodes.OperationCancelled,
        ErrorMessage = UserFacingErrors.Describe(ErrorCodes.OperationCancelled)
    };

    // -----------------------------------------------------------------------
    // command construction
    // -----------------------------------------------------------------------

    private ProcessCommand BuildCommand(Job job, AppSettings settings, JobPhase phase)
    {
        var workerSettings = BuildWorkerSettings(settings);
        var source = ResolveSource(job, settings);

        return new ProcessCommand
        {
            JobId = job.Id,
            VideoPath = job.VideoPath,
            OutputPath = job.OutputPath ?? OutputPathResolver.BuildDefaultPath(job.VideoPath, settings.OutputSuffix),
            CheckpointDir = _paths.JobCacheDirectory(job.Id),
            Settings = workerSettings,
            Phase = MapPhase(phase),
            SourceMode = source.Mode,
            AudioTrackIndex = source.AudioTrackIndex,
            SubtitleTrackIndex = source.SubtitleTrackIndex,
            SubtitleLanguage = source.SubtitleLanguage,

            // Always resume: the two-pass strategies depend on the translate pass picking up the
            // transcription checkpoint the earlier pass wrote.
            Resume = true
        };
    }

    /// <summary>
    /// The settings block both <c>process</c> and <c>extractAudio</c> send.
    ///
    /// Shared rather than duplicated because the worker fingerprints parts of it to decide whether
    /// a cached artefact still matches the current settings: a prefetch that sent a differently
    /// built block would have its wav discarded by the very job it was meant to speed up.
    /// </summary>
    private WorkerJobSettings BuildWorkerSettings(AppSettings settings)
    {
        // Some conversions cannot answer this without taking the worker down with them — see
        // ModelDescriptor.SupportsWordTimestamps. Enforced here rather than in the settings screen
        // because this is the one place every run passes through, including a retry that reuses a
        // settings snapshot saved before the model was switched.
        var wordTimestamps = settings.WordTimestamps;
        if (wordTimestamps && !_catalog.SupportsWordTimestamps(settings.WhisperModel))
        {
            _logger.LogInformation(
                "{Model}은(는) 단어 단위 타임스탬프를 지원하지 않아 이 실행에서는 끕니다.",
                settings.WhisperModel);
            wordTimestamps = false;
        }

        return new WorkerJobSettings
        {
            Language = Fallback(settings.SourceLanguage, "auto"),
            WhisperModel = Fallback(settings.WhisperModel, "auto"),

            // null means "let the worker follow the hardware recommendation"; the property is omitted
            // from the JSON entirely thanks to WhenWritingNull.
            ComputeType = settings.ComputeType is { } compute
                ? HardwareRecommendationPolicy.Describe(compute)
                : null,

            Device = "auto",
            BeamSize = settings.BeamSize,
            VadFilter = settings.VadFilter,
            WordTimestamps = wordTimestamps,
            ConditionOnPreviousText = settings.ConditionOnPreviousText,

            TranslationEngine = MapEngine(settings.TranslationEngine),
            TranslationModel = Fallback(settings.TranslationModel, "auto"),
            LlmModel = Fallback(settings.LlmModel, "auto"),
            TranslationStyle = MapStyle(settings.TranslationStyle),
            SkipTranslationForSameLanguage = settings.SkipTranslationForSameLanguage,
            TestDurationSeconds = settings.TestDurationSeconds,

            BatchMaxItems = settings.TranslationBatchMaxItems,
            BatchMaxChars = settings.TranslationBatchMaxChars,
            BatchMaxSeconds = settings.TranslationBatchMaxSeconds,
            ContextLines = settings.TranslationContextLines,
            Glossary = new Dictionary<string, string>(settings.Glossary, StringComparer.Ordinal),

            MaxLinesPerCue = settings.MaxLinesPerCue,
            MaxCharsPerLine = settings.MaxCharsPerLine,
            MinCueDurationSeconds = settings.MinCueDurationSeconds,
            MaxCueDurationSeconds = settings.MaxCueDurationSeconds,
            MinCueGapMilliseconds = settings.MinCueGapMilliseconds,
            MergeShortCues = settings.MergeShortCues,
            OutputConflictPolicy = MapConflictPolicy(settings.OutputConflictPolicy),
            AutoRetryOnRecoverableError = settings.AutoRetryOnRecoverableError
        };
    }

    /// <summary>Wire values for the four source-selection fields of <see cref="ProcessCommand"/>.</summary>
    private readonly record struct SourceSelection(
        string Mode,
        int? AudioTrackIndex,
        int? SubtitleTrackIndex,
        string? SubtitleLanguage);

    /// <summary>
    /// Decides what this job translates from.
    ///
    /// A per-file override always wins — the user looked at the actual track list and chose, which is
    /// strictly better information than a global policy or a container's language tag. With no
    /// override the application-wide policy applies, and the fallback for everything else is the MVP
    /// core path: 영상 음성 → Whisper → 번역 → ko.srt.
    /// </summary>
    private static SourceSelection ResolveSource(Job job, AppSettings settings)
    {
        if (job.SourceOverride == JobSourceOverride.EmbeddedSubtitle)
        {
            return new SourceSelection(
                SourceModes.EmbeddedSubtitle,
                AudioTrackIndex: null,
                job.SelectedSubtitleTrackIndex,
                ResolveSubtitleLanguage(job.SelectedSubtitleLanguage, settings));
        }

        if (job.SourceOverride == JobSourceOverride.Audio)
        {
            return new SourceSelection(
                SourceModes.Audio,
                job.SelectedAudioTrackIndex,
                SubtitleTrackIndex: null,
                SubtitleLanguage: null);
        }

        var useEmbedded =
            settings.ExistingSubtitlePolicy == ExistingSubtitlePolicy.UseEmbeddedTrack &&
            job.HasEmbeddedSubtitle;

        return useEmbedded
            ? new SourceSelection(
                SourceModes.EmbeddedSubtitle,
                AudioTrackIndex: null,
                job.SelectedSubtitleTrackIndex,
                ResolveSubtitleLanguage(job.SelectedSubtitleLanguage, settings))
            : new SourceSelection(SourceModes.Audio, job.SelectedAudioTrackIndex, null, null);
    }

    /// <summary>
    /// The track's own language, falling back to the configured 원본 언어 when the user did not
    /// confirm one. "auto" is not sent: language *detection* is a property of ASR, and there is
    /// nothing to detect from in an already-written subtitle file — the worker's own English default
    /// is the honest answer there.
    /// </summary>
    private static string? ResolveSubtitleLanguage(string? trackLanguage, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(trackLanguage))
        {
            return trackLanguage.Trim();
        }

        var configured = settings.SourceLanguage?.Trim();

        return string.IsNullOrEmpty(configured) || configured.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : configured;
    }

    /// <summary>Wire values for <c>ProcessCommand.phase</c>.</summary>
    private static string MapPhase(JobPhase phase) => phase switch
    {
        JobPhase.TranscribeOnly => "transcribe",
        JobPhase.TranslateAndWrite => "translate",
        _ => "full"
    };

    /// <summary>
    /// Wire values for <c>settings.outputConflictPolicy</c>. Skip is the fallback so an enum member
    /// added later never silently overwrites a user's subtitle file.
    /// </summary>
    private static string MapConflictPolicy(OutputConflictPolicy policy) => policy switch
    {
        Domain.Settings.OutputConflictPolicy.Overwrite => OutputConflictPolicies.Overwrite,
        Domain.Settings.OutputConflictPolicy.CreateNumberedCopy => OutputConflictPolicies.Numbered,
        _ => OutputConflictPolicies.Skip
    };

    private static string MapEngine(TranslationEngineKind engine) => engine switch
    {
        TranslationEngineKind.LocalLlm => "local-llm",
        TranslationEngineKind.Fake => "fake",
        _ => "local-translation"
    };

    private static TranslationEngineKind? ParseEngine(string? wire) => wire switch
    {
        "local-llm" => TranslationEngineKind.LocalLlm,
        "fake" => TranslationEngineKind.Fake,
        "local-translation" => TranslationEngineKind.LocalTranslationModel,
        _ => null
    };

    private static string MapStyle(TranslationStyle style) => style switch
    {
        TranslationStyle.Literal => "literal",
        TranslationStyle.Polite => "polite",
        TranslationStyle.Casual => "casual",
        TranslationStyle.PreserveSourceRegister => "preserve",
        _ => "natural"
    };

    /// <summary>
    /// Wire stage name → <see cref="JobStage"/>. Tolerant of snake_case/kebab-case so a worker that
    /// drifts from the shared constants degrades to a wrong-looking bar instead of a failed job.
    /// </summary>
    private static JobStage MapStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return JobStage.None;
        }

        var normalised = stage.Replace("_", string.Empty, StringComparison.Ordinal)
                              .Replace("-", string.Empty, StringComparison.Ordinal)
                              .ToLowerInvariant();

        return normalised switch
        {
            "probing" or "probe" => JobStage.Probing,
            "extractingaudio" or "extractaudio" => JobStage.ExtractingAudio,
            "transcribing" or "transcribe" => JobStage.Transcribing,
            "translating" or "translate" => JobStage.Translating,
            "writingsubtitle" or "writesubtitle" => JobStage.WritingSubtitle,
            "done" or "completed" => JobStage.Done,
            _ => JobStage.None
        };
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>
    /// Per-job scratch state. Only ever touched from the client's single reader task, so no locking is
    /// needed; the fields exist because the wire protocol reports language and stage separately from
    /// progress and both have to be re-sent on every <see cref="JobProgress"/>.
    /// </summary>
    private sealed class JobState(string jobId)
    {
        public string JobId { get; } = jobId;
        public JobStage Stage { get; set; } = JobStage.Probing;
        public double StageProgress { get; set; }
        public string? DetectedLanguage { get; set; }
        public double? LanguageProbability { get; set; }
    }
}
