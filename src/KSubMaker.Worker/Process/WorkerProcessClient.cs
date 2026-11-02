using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Errors;
using KSubMaker.Worker.Tools;
using KSubMaker.WorkerProtocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SysProcess = System.Diagnostics.Process;

namespace KSubMaker.Worker.Process;

/// <summary>
/// Owns the Python worker process and the JSON-Lines channel to it.
///
/// Design notes for the three parts that are easy to get wrong:
///
/// <para><b>Reader-loop resilience.</b> One malformed stdout line must never take the pipeline down.
/// A Python warning, a stray <c>print</c>, a tqdm bar or a half-flushed line all arrive here. The
/// serializer turns them into <see cref="UnknownEvent"/>; we log at Warning and drop them. Likewise
/// every subscriber of <see cref="EventReceived"/> is invoked inside its own try/catch, because an
/// exception escaping a UI handler onto the reader task would kill the loop and silently freeze every
/// job.</para>
///
/// <para><b>Request/response correlation.</b> The channel is fully asynchronous and interleaved:
/// progress events for a running job arrive while a <c>listModels</c> reply is outstanding. Every
/// command carries a <c>requestId</c> that the worker echoes, so a reply is matched by id, not by
/// arrival order. The <see cref="TaskCompletionSource{TResult}"/> is registered <i>before</i> the
/// command is written, otherwise a very fast worker could answer before we are listening.</para>
///
/// <para><b>No orphaned processes.</b> The process is put into a Job Object with
/// KILL_ON_JOB_CLOSE the instant it starts, so even a hard kill of the UI takes the whole tree with
/// it. On the ordinary path we additionally ask nicely (<c>shutdown</c>) and then escalate to
/// <see cref="ProcessTree.KillTree(SysProcess)"/>.</para>
/// </summary>
public sealed class WorkerProcessClient : IWorkerClient
{
    /// <summary>Read by <c>model_manager.models_root()</c> in the worker.</summary>
    internal const string ModelsDirectoryVariable = "KSUBMAKER_MODELS_DIR";

    /// <summary>Read by <c>ffmpeg_service._candidate_roots()</c> and <c>llm_translator</c>.</summary>
    internal const string ToolsDirectoryVariable = "KSUBMAKER_TOOLS_DIR";

    /// <summary>Hugging Face's cache root. Kept inside the models tree so relocating it moves everything.</summary>
    internal const string HuggingFaceHomeVariable = "HF_HOME";

    private readonly IToolLocator _toolLocator;
    private readonly IAppPaths _paths;
    private readonly IWorkerLaunchDescriptor? _launchDescriptor;
    private readonly WorkerOptions _options;
    private readonly ILogger<WorkerProcessClient> _logger;

    /// <summary>One writer at a time, so two commands can never interleave inside one stdin line.</summary>
    private readonly SemaphoreSlim _stdinLock = new(1, 1);

    /// <summary>Serialises <see cref="StartAsync"/> and <see cref="StopAsync"/> against each other.</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private readonly ConcurrentDictionary<string, IPendingRequest> _pending = new(StringComparer.Ordinal);

    /// <summary>Bounded tail of stderr, surfaced on <see cref="Exited"/> so a crash can be diagnosed.</summary>
    private readonly Queue<string> _standardErrorRing = new();

    private SysProcess? _process;
    private WindowsJobObject? _jobObject;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _exitWatchTask;
    private Task? _watchdogTask;
    private TaskCompletionSource<ReadyEvent>? _readyTcs;
    private ReadyEvent? _ready;

    private long _lastEventTicks;
    private string? _inFlightJobId;
    private int _stopRequested;
    private int _exitRaised;

    /// <summary>
    /// Set when the watchdog has decided this worker instance must die. <c>Process.Kill</c> returns
    /// before the process is gone, so for a moment <c>HasExited</c> is still false. Without this flag
    /// the queue's automatic retry — which starts the instant the watchdog reports the failure —
    /// called <see cref="StartAsync"/>, got the dying instance back, subscribed to it, and was then
    /// failed in 0.0s by that instance's own exit event (2026-11-02, a 7-minute lecture).
    /// </summary>
    private int _condemned;
    private volatile bool _disposed;

    public WorkerProcessClient(
        IToolLocator toolLocator,
        IAppPaths paths,
        IOptions<WorkerOptions> options,
        ILogger<WorkerProcessClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _toolLocator = toolLocator ?? throw new ArgumentNullException(nameof(toolLocator));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value ?? new WorkerOptions();

        // IToolLocator (Application layer) cannot express PYTHONPATH / launch-mode; the concrete
        // locator publishes that through a host-local interface. Optional so a test double that only
        // implements IToolLocator still works.
        _launchDescriptor = toolLocator as IWorkerLaunchDescriptor;
    }

    public event EventHandler<WorkerEvent>? EventReceived;

    public event EventHandler<WorkerExitedEventArgs>? Exited;

    public bool IsRunning
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            var process = _process;
            if (process is null || _ready is null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Which launch mode was chosen. Informational; null before the first start.</summary>
    public WorkerLaunchKind LaunchKind { get; private set; } = WorkerLaunchKind.NotFound;

    // -----------------------------------------------------------------------
    // lifetime
    // -----------------------------------------------------------------------

    public async Task<ReadyEvent> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not null && _ready is not null && !HasExitedSafe(_process)
                && Volatile.Read(ref _condemned) == 0)
            {
                return _ready;
            }

            // A condemned instance falls through to CleanupAsync, which kills it, waits for its pumps
            // and raises its Exited event now — before the caller has subscribed to anything.

            // A previous worker crashed (or the watchdog killed it). Tear the corpse down first so a
            // restart cannot leak handles or leave a stale reader task attached.
            await CleanupAsync().ConfigureAwait(false);

            var validation = Validate();
            if (!validation.Ok)
            {
                throw new WorkerStartupException(
                    validation.ErrorCode ?? ErrorCodes.WorkerCrashed,
                    validation.Message ?? "필요한 실행 파일을 찾을 수 없습니다.");
            }

            return await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<ReadyEvent> StartCoreAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopRequested, 0);
        Volatile.Write(ref _exitRaised, 0);
        Volatile.Write(ref _condemned, 0);
        Volatile.Write(ref _inFlightJobId, null);

        var launch = DescribeLaunch();
        LaunchKind = launch.Kind;

        var startInfo = BuildStartInfo(launch);
        var process = new SysProcess { StartInfo = startInfo };

        _readyTcs = new TaskCompletionSource<ReadyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lifetimeCts = new CancellationTokenSource();
        var lifetimeToken = _lifetimeCts.Token;

        lock (_standardErrorRing)
        {
            _standardErrorRing.Clear();
        }

        try
        {
            if (!process.Start())
            {
                throw new WorkerStartupException(ErrorCodes.WorkerCrashed, "AI 작업 프로세스를 시작하지 못했습니다.");
            }
        }
        catch (Exception ex) when (ex is not WorkerException)
        {
            process.Dispose();
            throw new WorkerStartupException(
                ErrorCodes.WorkerCrashed,
                $"AI 작업 프로세스를 시작하지 못했습니다: {launch.Executable}",
                ex);
        }

        _process = process;

        // Assign immediately: the window between Start() and this call is the only moment in which a
        // hard kill of the host could orphan the worker.
        _jobObject = new WindowsJobObject(_logger);
        _jobObject.TryAssign(process);

        // stdin is written as complete lines and flushed explicitly; auto-flush would let a partial
        // line reach the worker's readline() and desynchronise the protocol.
        process.StandardInput.AutoFlush = false;

        MarkAlive();

        _stdoutTask = Task.Factory.StartNew(
            () => ReadStandardOutputAsync(process, lifetimeToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();

        _stderrTask = Task.Factory.StartNew(
            () => ReadStandardErrorAsync(process, lifetimeToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();

        _exitWatchTask = Task.Run(() => WatchExitAsync(process), CancellationToken.None);
        _watchdogTask = Task.Run(() => WatchdogAsync(lifetimeToken), CancellationToken.None);

        _logger.LogInformation(
            "Worker 프로세스를 시작했습니다. (PID {Pid}, 방식 {Kind}, {Description})",
            SafePid(process), launch.Kind, launch.Description);

        try
        {
            var ready = await WaitForReadyAsync(process, lifetimeToken, cancellationToken).ConfigureAwait(false);
            _ready = ready;
            return ready;
        }
        catch
        {
            // Never leave a half-started worker behind.
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ReadyEvent> WaitForReadyAsync(
        SysProcess process,
        CancellationToken lifetimeToken,
        CancellationToken cancellationToken)
    {
        var readyTcs = _readyTcs ?? throw new InvalidOperationException("ready 대기 상태가 초기화되지 않았습니다.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        timeoutCts.CancelAfter(_options.StartupTimeout);

        // Best effort: some worker builds announce `ready` unprompted at boot, others answer `hello`.
        // Sending it always is harmless (the extra reply is an `ack`, which is discarded).
        try
        {
            await SendAsync(new HelloCommand { HostVersion = _options.HostVersion }, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Including cancellation: the ready wait below turns a cancelled/timed-out start into the
            // proper Korean startup exception, which is a better message than a raw OperationCanceled.
            _logger.LogDebug(ex, "hello 명령 전송에 실패했습니다. ready 이벤트를 계속 기다립니다.");
        }

        ReadyEvent ready;
        try
        {
            await using var registration = timeoutCts.Token
                .Register(static state => ((TaskCompletionSource<ReadyEvent>)state!).TrySetCanceled(), readyTcs)
                .ConfigureAwait(false);

            ready = await readyTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (HasExitedSafe(process))
            {
                throw new WorkerStartupException(
                    ErrorCodes.WorkerCrashed,
                    $"AI 작업 프로세스가 시작 직후 종료되었습니다. {LastStandardError() ?? string.Empty}".TrimEnd());
            }

            throw new WorkerStartupException(
                ErrorCodes.WorkerCrashed,
                $"AI 작업 프로세스가 {_options.StartupTimeout.TotalSeconds:0}초 안에 준비되지 않았습니다. " +
                "설치가 손상되었거나 백신 프로그램이 실행을 차단했을 수 있습니다.");
        }

        // A different major protocol version means the two sides disagree about field meanings; running
        // anyway would corrupt output in ways that are very hard to diagnose. Refuse.
        if (!WorkerProtocolSerializer.IsCompatible(ready.ProtocolVersion, out var warning))
        {
            throw new WorkerProtocolException(
                $"{warning} 프로그램을 다시 설치하세요.");
        }

        if (warning is not null)
        {
            _logger.LogWarning("{Warning} 계속 진행합니다.", warning);
        }

        _logger.LogInformation(
            "Worker 준비 완료. (프로토콜 {Protocol}, worker {Worker}, python {Python}, 기능 {Capabilities})",
            ready.ProtocolVersion,
            ready.WorkerVersion ?? "?",
            ready.PythonVersion ?? "?",
            ready.Capabilities.Count == 0 ? "-" : string.Join(", ", ready.Capabilities));

        return ready;
    }

    // -----------------------------------------------------------------------
    // sending
    // -----------------------------------------------------------------------

    public async Task SendAsync(WorkerCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Deliberately no _disposed check: DisposeAsync sends `shutdown` through this method, and a
        // disposed client with no process already fails below with a clear message.
        var process = _process
            ?? throw new InvalidOperationException("AI 작업 프로세스가 실행 중이 아닙니다.");

        if (HasExitedSafe(process))
        {
            throw new WorkerCrashedException(SafeExitCode(process), LastStandardError());
        }

        // Serialise outside the lock: JSON conversion can throw and must not hold up the channel.
        var line = WorkerProtocolSerializer.SerializeCommand(command) + "\n";

        await _stdinLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (command is ProcessCommand processCommand)
            {
                // Remember what the watchdog is guarding, and restart its window from the send.
                Volatile.Write(ref _inFlightJobId, processCommand.JobId);
                MarkAlive();
            }

            await process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new WorkerCrashedException(SafeExitCode(process), LastStandardError(), ex);
        }
        finally
        {
            _stdinLock.Release();
        }

        _logger.LogTrace("→ worker: {Command} ({RequestId})", command.Command, command.RequestId);
    }

    public async Task<TEvent> RequestAsync<TEvent>(WorkerCommand command, CancellationToken cancellationToken = default)
        where TEvent : WorkerEvent
    {
        ArgumentNullException.ThrowIfNull(command);

        var pending = new PendingRequest<TEvent>();

        // Registered *before* the write: the worker can answer faster than the continuation after
        // SendAsync would run, and an unmatched reply would be dropped as an orphan.
        if (!_pending.TryAdd(command.RequestId, pending))
        {
            throw new InvalidOperationException($"이미 처리 중인 요청 ID입니다: {command.RequestId}");
        }

        try
        {
            await using var registration = cancellationToken
                .Register(static state => ((IPendingRequest)state!).Cancel(), pending)
                .ConfigureAwait(false);

            await SendAsync(command, cancellationToken).ConfigureAwait(false);
            return await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            // Always: a leaked entry would keep the watchdog convinced a request is in flight.
            _pending.TryRemove(command.RequestId, out _);
        }
    }

    // -----------------------------------------------------------------------
    // stdout / stderr pumps
    // -----------------------------------------------------------------------

    private async Task ReadStandardOutputAsync(SysProcess process, CancellationToken cancellationToken)
    {
        var reader = process.StandardOutput;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "worker stdout 읽기가 중단되었습니다.");
                break;
            }

            if (line is null)
            {
                break; // EOF: the worker closed stdout, i.e. it is exiting.
            }

            // Any output at all proves the worker is alive, even a line we cannot parse.
            MarkAlive();

            try
            {
                Dispatch(line);
            }
            catch (Exception ex)
            {
                // Belt and braces. Dispatch already guards each subscriber; this catch guarantees that
                // *nothing* can terminate the reader loop and silently freeze the app.
                _logger.LogError(ex, "worker 이벤트 처리 중 예외가 발생했습니다. 이 줄은 무시합니다.");
            }
        }

        _logger.LogDebug("worker stdout 리더가 종료되었습니다.");
    }

    private async Task ReadStandardErrorAsync(SysProcess process, CancellationToken cancellationToken)
    {
        var reader = process.StandardError;
        var capacity = Math.Max(1, _options.StandardErrorBufferLines);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "worker stderr 읽기가 중단되었습니다.");
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            _logger.LogDebug("worker stderr: {Line}", line);

            lock (_standardErrorRing)
            {
                _standardErrorRing.Enqueue(line);
                while (_standardErrorRing.Count > capacity)
                {
                    _standardErrorRing.Dequeue();
                }
            }
        }
    }

    private void Dispatch(string line)
    {
        var workerEvent = WorkerProtocolSerializer.DeserializeEvent(line);

        if (workerEvent is UnknownEvent unknown)
        {
            // Discarded on purpose. A stray print or a truncated line is a diagnostic curiosity, not a
            // pipeline failure, and must never fault a pending request.
            _logger.LogWarning(
                "worker의 알 수 없는 출력을 무시했습니다. ({Reason}) {Raw}",
                unknown.Reason ?? "-",
                Truncate(unknown.Raw, 400));
            return;
        }

        switch (workerEvent)
        {
            case ReadyEvent ready:
                _readyTcs?.TrySetResult(ready);
                break;

            case LogEvent log:
                _logger.Log(MapLogLevel(log.Level), "worker: {Message}", log.Message);
                break;

            case CompletedEvent or ErrorEvent or CancelledEvent:
                ClearInFlightJob(workerEvent.JobId);
                break;
        }

        if (workerEvent.RequestId is { Length: > 0 } requestId &&
            _pending.TryGetValue(requestId, out var pending))
        {
            // Returns false for interleaved events that are not the answer (an `ack`, a `progress`);
            // those stay pending and also flow on to EventReceived below.
            pending.TryComplete(workerEvent);
        }

        RaiseEventReceived(workerEvent);
    }

    private void RaiseEventReceived(WorkerEvent workerEvent)
    {
        var handler = EventReceived;
        if (handler is null)
        {
            return;
        }

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<WorkerEvent>)target)(this, workerEvent);
            }
            catch (Exception ex)
            {
                // One faulty subscriber must not stop the others, and must never reach the reader loop.
                _logger.LogError(ex, "worker 이벤트 구독자에서 예외가 발생했습니다. ({Type})", workerEvent.Type);
            }
        }
    }

    // -----------------------------------------------------------------------
    // watchdog
    // -----------------------------------------------------------------------

    /// <summary>
    /// Polls (never busy-waits) for a worker that has gone silent. A hung CUDA call or a deadlocked
    /// Python thread produces no output at all and no exit, so nothing else in this class would ever
    /// notice it.
    /// </summary>
    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        if (_options.IdleTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.IdleTimeout.TotalSeconds / 4d, 5d, 60d));

        try
        {
            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var jobId = Volatile.Read(ref _inFlightJobId);
                if (jobId is null && _pending.IsEmpty)
                {
                    continue; // Nothing in flight: silence is expected.
                }

                var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref _lastEventTicks));
                if (idle < _options.IdleTimeout)
                {
                    continue;
                }

                TripWatchdog(jobId, idle);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "worker 감시 타이머에서 예외가 발생했습니다.");
        }
    }

    private void TripWatchdog(string? jobId, TimeSpan idle)
    {
        var duration = idle.TotalMinutes >= 1d
            ? idle.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + "분"
            : idle.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "초";

        var message = $"장시간 응답 없음: {duration} 동안 아무 진행 상황도 보고되지 않았습니다.";

        _logger.LogWarning("{Message} (작업 {JobId})", message, jobId ?? "-");

        if (_options.TerminateOnIdleTimeout)
        {
            // Before anyone hears about the failure: the synthesised error below is what triggers
            // the retry, and the retry must not be handed this instance.
            Volatile.Write(ref _condemned, 1);
        }

        FaultPending(new WorkerTimeoutException(message));

        if (jobId is not null)
        {
            // Synthesised so the job processor -- which waits on job-scoped events, not on a pending
            // request -- also unblocks with the real reason instead of a generic crash.
            RaiseEventReceived(new ErrorEvent
            {
                JobId = jobId,
                Code = ErrorCodes.WorkerCrashed,
                Message = message,
                Recoverable = true,
                Detail = $"idleSeconds={idle.TotalSeconds:0}"
            });

            Volatile.Write(ref _inFlightJobId, null);
        }

        if (!_options.TerminateOnIdleTimeout)
        {
            return;
        }

        // A wedged interpreter never recovers. Kill it so the next job gets a fresh worker instead of
        // waiting out another full idle window.
        _logger.LogWarning("응답 없는 worker 프로세스를 강제 종료합니다.");
        ProcessTree.KillTree(_process, _logger);
    }

    // -----------------------------------------------------------------------
    // exit handling
    // -----------------------------------------------------------------------

    private async Task WatchExitAsync(SysProcess process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        // Give the pumps a moment to drain whatever was still buffered, so the last stderr lines make
        // it into the crash report.
        var pumps = Task.WhenAll(_stdoutTask ?? Task.CompletedTask, _stderrTask ?? Task.CompletedTask);
        await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        HandleExited(SafeExitCode(process));
    }

    /// <summary>Raises <see cref="Exited"/> exactly once, whatever path got us here.</summary>
    private void HandleExited(int exitCode)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0)
        {
            return;
        }

        var expected = Volatile.Read(ref _stopRequested) != 0;
        var standardError = LastStandardError();

        if (expected)
        {
            _logger.LogInformation("Worker 프로세스가 종료되었습니다. (종료 코드 {ExitCode})", exitCode);
            CancelPending();
        }
        else
        {
            _logger.LogError(
                "Worker 프로세스가 예기치 않게 종료되었습니다. (종료 코드 {ExitCode}) {StandardError}",
                exitCode,
                standardError ?? "-");

            FaultPending(new WorkerCrashedException(exitCode, standardError));
        }

        Volatile.Write(ref _inFlightJobId, null);

        var handler = Exited;
        if (handler is null)
        {
            return;
        }

        var args = new WorkerExitedEventArgs(exitCode, expected, standardError);
        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<WorkerExitedEventArgs>)target)(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "worker 종료 이벤트 구독자에서 예외가 발생했습니다.");
            }
        }
    }

    // -----------------------------------------------------------------------
    // stopping
    // -----------------------------------------------------------------------

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Set before taking the gate so a StartAsync that is still waiting for `ready` aborts at once
        // instead of making shutdown wait out the full startup timeout. Only the handshake is
        // cancelled here, not the whole lifetime: the stdout/stderr pumps must keep running until the
        // worker is really gone, otherwise its last words (and a `goodbye`) are lost, and a worker
        // that keeps writing could block on a full stdout pipe instead of exiting.
        Volatile.Write(ref _stopRequested, 1);
        _readyTcs?.TrySetCanceled();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = _process;
            if (process is null)
            {
                return; // Never started, or already stopped. Safe to call twice.
            }

            if (!HasExitedSafe(process))
            {
                // A zero timeout means "kill now". Asking politely first would be actively harmful:
                // a worker that still reads stdin would exit on its own, KillTree would then find
                // nothing to kill, and any FFmpeg child it failed to reap would be orphaned.
                if (timeout > TimeSpan.Zero)
                {
                    await RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!await WaitForExitAsync(process, timeout, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "Worker가 {Seconds:0}초 안에 종료되지 않아 프로세스 트리를 강제 종료합니다.",
                        timeout.TotalSeconds);

                    ProcessTree.KillTree(process, _logger);
                    await WaitForExitAsync(process, TimeSpan.FromSeconds(5), CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            await CleanupAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendCts.CancelAfter(TimeSpan.FromSeconds(2));
            await SendAsync(new ShutdownCommand(), sendCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The worker may already be gone; escalating to the kill below is the correct answer.
            _logger.LogDebug(ex, "shutdown 명령을 보내지 못했습니다. 강제 종료로 진행합니다.");
        }
    }

    /// <summary>
    /// Tears down everything owned by one worker instance. Idempotent; callers must hold
    /// <see cref="_lifecycleGate"/>.
    /// </summary>
    private async Task CleanupAsync()
    {
        var process = _process;
        CancelLifetime();

        if (process is not null)
        {
            // Last resort: whatever brought us here, the tree must not survive this method.
            ProcessTree.KillTree(process, _logger);

            var pumps = Task.WhenAll(_stdoutTask ?? Task.CompletedTask, _stderrTask ?? Task.CompletedTask);
            await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

            HandleExited(SafeExitCode(process));
        }

        var background = Task.WhenAll(
            _watchdogTask ?? Task.CompletedTask,
            _exitWatchTask ?? Task.CompletedTask);

        await Task.WhenAny(background, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        // Closing the job object is the kernel-level guarantee: anything still inside it dies now.
        _jobObject?.Dispose();
        _jobObject = null;

        _lifetimeCts?.Dispose();
        _lifetimeCts = null;

        _readyTcs?.TrySetCanceled();
        _readyTcs = null;
        _ready = null;

        _stdoutTask = null;
        _stderrTask = null;
        _exitWatchTask = null;
        _watchdogTask = null;

        try
        {
            process?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // Nothing useful to do; the handle is going away regardless.
        }

        _process = null;
        CancelPending();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // Reuses the whole graceful-then-kill path, so DisposeAsync has the same no-orphan
            // guarantee as an explicit StopAsync.
            await StopAsync(_options.ShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "worker 정리 중 오류가 발생했습니다.");
        }

        _stdinLock.Dispose();
        _lifecycleGate.Dispose();
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private ProcessStartInfo BuildStartInfo(WorkerLaunchInfo launch)
    {
        // UTF-8 without a BOM in both directions: Korean paths and Korean log text otherwise arrive as
        // mojibake on a cp949 Windows console.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = encoding,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            WorkingDirectory = launch.WorkingDirectory ?? AppContext.BaseDirectory
        };

        foreach (var argument in launch.Arguments)
        {
            // ArgumentList quotes each element correctly; string concatenation would break on the
            // spaces in "C:\Program Files\...".
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        foreach (var (key, value) in launch.Environment)
        {
            startInfo.Environment[key] = value;
        }

        ApplyPathEnvironment(startInfo);

        return startInfo;
    }

    /// <summary>
    /// Tells the worker where the relocatable directories are.
    ///
    /// This is done through the environment rather than a protocol field because the worker needs
    /// the models directory <i>before</i> any job arrives: <c>CommandHandlers.__init__</c> builds the
    /// model manager, the transcriber and the translator from <c>models_root()</c> at start-up, and
    /// <c>listModels</c> / <c>verifyModel</c> run outside a job entirely. A per-job field would leave
    /// all of those still looking in the default location.
    ///
    /// The host's value always wins over an ambient variable of the same name: the settings screen is
    /// the single source of truth for these paths, and a stale shell variable silently pointing the
    /// worker somewhere else is exactly the bug this fixes.
    /// </summary>
    private void ApplyPathEnvironment(ProcessStartInfo startInfo)
    {
        var models = SafePath(() => _paths.ModelsDirectory, nameof(IAppPaths.ModelsDirectory));
        if (models is not null)
        {
            startInfo.Environment[ModelsDirectoryVariable] = models;

            // Keeps a Hugging Face fallback download inside the folder the user chose instead of
            // %USERPROFILE%\.cache, which is the one place they cannot relocate from the UI.
            startInfo.Environment[HuggingFaceHomeVariable] = Path.Combine(models, ".hf-cache");
        }

        var tools = SafePath(() => _paths.ToolsDirectory, nameof(IAppPaths.ToolsDirectory));
        if (tools is not null)
        {
            startInfo.Environment[ToolsDirectoryVariable] = tools;
        }

        _logger.LogDebug(
            "worker 환경: {ModelsVariable}={Models}, {ToolsVariable}={Tools}",
            ModelsDirectoryVariable, models ?? "-", ToolsDirectoryVariable, tools ?? "-");
    }

    /// <summary>
    /// A path override that cannot be resolved must not stop the worker from starting — the Python
    /// side has its own default, which is strictly better than refusing to run.
    /// </summary>
    private string? SafePath(Func<string> read, string what)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{What} 경로를 확인하지 못해 worker에 전달하지 않습니다.", what);
            return null;
        }
    }

    private WorkerLaunchInfo DescribeLaunch()
    {
        if (_launchDescriptor is not null)
        {
            return _launchDescriptor.DescribeWorkerLaunch();
        }

        var (executable, arguments) = _toolLocator.WorkerCommandLine;
        return new WorkerLaunchInfo
        {
            Kind = WorkerLaunchKind.NotFound,
            Executable = executable,
            Arguments = arguments,
            Resolved = true,
            Description = executable
        };
    }

    private ToolValidationResult Validate()
    {
        if (_launchDescriptor is not null)
        {
            return _launchDescriptor.ValidateTools();
        }

        return _toolLocator.TryValidate(out var error)
            ? new ToolValidationResult(true, null, null)
            : new ToolValidationResult(false, ErrorCodes.WorkerCrashed, error);
    }

    private void MarkAlive() => Volatile.Write(ref _lastEventTicks, DateTime.UtcNow.Ticks);

    private void ClearInFlightJob(string? jobId)
    {
        var current = Volatile.Read(ref _inFlightJobId);
        if (current is not null && (jobId is null || string.Equals(current, jobId, StringComparison.Ordinal)))
        {
            Volatile.Write(ref _inFlightJobId, null);
        }
    }

    private void CancelLifetime()
    {
        try
        {
            _lifetimeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }
    }

    private void FaultPending(Exception exception)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending))
            {
                pending.Fault(exception);
            }
        }
    }

    private void CancelPending()
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending))
            {
                pending.Cancel();
            }
        }
    }

    private string? LastStandardError()
    {
        lock (_standardErrorRing)
        {
            return _standardErrorRing.Count == 0 ? null : string.Join('\n', _standardErrorRing);
        }
    }

    private static async Task<bool> WaitForExitAsync(SysProcess process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (HasExitedSafe(process))
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return HasExitedSafe(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static bool HasExitedSafe(SysProcess process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static int SafeExitCode(SysProcess process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotSupportedException)
        {
            return -1;
        }
    }

    private static string SafePid(SysProcess process)
    {
        try
        {
            return process.Id.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static LogLevel MapLogLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" or "fatal" => LogLevel.Critical,
        _ => LogLevel.Information
    };

    // -----------------------------------------------------------------------
    // pending-request bookkeeping
    // -----------------------------------------------------------------------

    private interface IPendingRequest
    {
        /// <summary>True when this event answered the request.</summary>
        bool TryComplete(WorkerEvent workerEvent);

        void Fault(Exception exception);

        void Cancel();
    }

    private sealed class PendingRequest<TEvent> : IPendingRequest
        where TEvent : WorkerEvent
    {
        private readonly TaskCompletionSource<TEvent> _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TEvent> Task => _source.Task;

        public bool TryComplete(WorkerEvent workerEvent)
        {
            if (workerEvent is TEvent typed)
            {
                return _source.TrySetResult(typed);
            }

            // An `error` with our requestId is the negative answer to this very request.
            if (workerEvent is ErrorEvent errorEvent)
            {
                return _source.TrySetException(new WorkerRequestFailedException(errorEvent));
            }

            return false;
        }

        public void Fault(Exception exception) => _source.TrySetException(exception);

        public void Cancel() => _source.TrySetCanceled();
    }
}
