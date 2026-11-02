using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Infrastructure.Paths;
using KSubMaker.IntegrationTests.Infrastructure;
using KSubMaker.Worker;
using KSubMaker.Worker.Process;
using KSubMaker.Worker.Tools;
using KSubMaker.WorkerProtocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KSubMaker.IntegrationTests.Worker;

/// <summary>
/// Drives the real <see cref="WorkerProcessClient"/> against a tiny Python stub: handshake,
/// request/response correlation, resilience to garbage on stdout, and a shutdown that leaves no
/// orphan process behind.
/// </summary>
public sealed class WorkerProtocolHandshakeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly TempWorkspace _workspace = new("ksubmaker-worker");

    public void Dispose() => _workspace.Dispose();

    private static string StubScript
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Stubs", "worker_stub.py");
            File.Exists(path).Should().BeTrue($"the stub script must be copied to the output ({path})");
            return path;
        }
    }

    /// <summary>Points every relocatable directory at the throwaway workspace.</summary>
    private AppPaths Paths => _paths ??= new AppPaths(_workspace.Combine("appdata"));

    private AppPaths? _paths;

    private WorkerProcessClient NewClient(
        string? protocolOverride = null,
        string? pidFile = null,
        string? environmentFile = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        if (protocolOverride is not null)
        {
            environment["KSUBMAKER_STUB_PROTOCOL"] = protocolOverride;
        }

        if (pidFile is not null)
        {
            environment["KSUBMAKER_STUB_PID_FILE"] = pidFile;
        }

        if (environmentFile is not null)
        {
            environment["KSUBMAKER_STUB_ENV_FILE"] = environmentFile;
        }

        var locator = new StubToolLocator(StubScript, environment, _workspace.Root);

        var options = Options.Create(new WorkerOptions
        {
            StartupTimeout = TimeSpan.FromSeconds(30),
            ShutdownTimeout = TimeSpan.FromSeconds(10),
            // The watchdog is a wall-clock timer; it has nothing to prove here and would only make
            // the test slower and less deterministic.
            IdleTimeout = TimeSpan.Zero,
            HostVersion = "integration-test"
        });

        return new WorkerProcessClient(locator, Paths, options, NullLogger<WorkerProcessClient>.Instance);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        // The stub writes its pid before anything else; by the time `ready` has arrived it is there.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(pidFile))
            {
                var text = (await File.ReadAllTextAsync(pidFile)).Trim();
                if (text.Length > 0)
                {
                    return int.Parse(text, CultureInfo.InvariantCulture);
                }
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException($"스텁이 PID 파일을 쓰지 않았습니다: {pidFile}");
    }

    // -----------------------------------------------------------------------
    // handshake
    // -----------------------------------------------------------------------

    [RequiresPythonFact]
    public async Task StartAsync_completes_the_ready_handshake()
    {
        await using var client = NewClient();

        var ready = await client.StartAsync().WaitAsync(Timeout);

        ready.Type.Should().Be(ProtocolConstants.Events.Ready);
        ready.ProtocolVersion.Should().Be(ProtocolConstants.Version);
        ready.WorkerVersion.Should().Be("stub-0.1");
        ready.PythonVersion.Should().NotBeNullOrWhiteSpace();
        ready.Capabilities.Should().Contain("stub");

        client.IsRunning.Should().BeTrue();

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
    }

    /// <summary>
    /// The worker resolves models through <c>KSUBMAKER_MODELS_DIR</c> before any job arrives, so the
    /// variable has to be on the child process's environment, not in a per-job message.
    /// </summary>
    [RequiresPythonFact]
    public async Task StartAsync_tells_the_worker_where_the_relocatable_directories_are()
    {
        var relocated = _workspace.CreateSubdirectory("elsewhere-models");
        Paths.ApplyOverrides(cacheDirectory: null, modelDirectory: relocated, logDirectory: null);

        var environmentFile = _workspace.Combine("worker-env.json");

        await using (var client = NewClient(environmentFile: environmentFile))
        {
            await client.StartAsync().WaitAsync(Timeout);
            await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(environmentFile));
        var root = document.RootElement;

        root.GetProperty("KSUBMAKER_MODELS_DIR").GetString().Should().Be(Paths.ModelsDirectory);
        root.GetProperty("KSUBMAKER_TOOLS_DIR").GetString().Should().Be(Paths.ToolsDirectory);
        root.GetProperty("HF_HOME").GetString()
            .Should().Be(Path.Combine(Paths.ModelsDirectory, ".hf-cache"));
    }

    [RequiresPythonFact]
    public async Task StartAsync_is_idempotent()
    {
        await using var client = NewClient();

        var first = await client.StartAsync().WaitAsync(Timeout);
        var second = await client.StartAsync().WaitAsync(Timeout);

        second.Should().BeSameAs(first, "a second start must reuse the running worker");

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
    }

    [RequiresPythonFact]
    public async Task An_incompatible_major_protocol_version_is_refused()
    {
        await using var client = NewClient(protocolOverride: "2.0");

        var act = async () => await client.StartAsync().WaitAsync(Timeout);

        (await act.Should().ThrowAsync<WorkerProtocolException>())
            .Which.Message.Should().Contain("호환되지 않습니다");

        client.IsRunning.Should().BeFalse();
    }

    [RequiresPythonFact]
    public async Task A_different_minor_protocol_version_is_tolerated()
    {
        await using var client = NewClient(protocolOverride: "1.9");

        var ready = await client.StartAsync().WaitAsync(Timeout);

        ready.ProtocolVersion.Should().Be("1.9");

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
    }

    // -----------------------------------------------------------------------
    // request / response
    // -----------------------------------------------------------------------

    [RequiresPythonFact]
    public async Task A_request_is_matched_to_its_reply_by_requestId()
    {
        await using var client = NewClient();
        await client.StartAsync().WaitAsync(Timeout);

        var command = new ProbeCommand { VideoPath = "/videos/한국어 영상.mkv" };

        var reply = await client.RequestAsync<ProbeResultEvent>(command).WaitAsync(Timeout);

        reply.RequestId.Should().Be(command.RequestId);
        reply.VideoPath.Should().Be(command.VideoPath);
        reply.DurationSeconds.Should().Be(8d);
        reply.AudioTracks.Should().ContainSingle().Which.Language.Should().Be("eng");

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
    }

    [RequiresPythonFact]
    public async Task Several_requests_in_a_row_each_get_their_own_reply()
    {
        await using var client = NewClient();
        await client.StartAsync().WaitAsync(Timeout);

        var probe = new ProbeCommand { VideoPath = "/videos/a.mkv" };
        var models = new ListModelsCommand();
        var hardware = new DetectHardwareCommand();

        var probeReply = await client.RequestAsync<ProbeResultEvent>(probe).WaitAsync(Timeout);
        var modelReply = await client.RequestAsync<ModelListEvent>(models).WaitAsync(Timeout);
        var hardwareReply = await client.RequestAsync<HardwareEvent>(hardware).WaitAsync(Timeout);

        probeReply.RequestId.Should().Be(probe.RequestId);
        modelReply.RequestId.Should().Be(models.RequestId);
        hardwareReply.RequestId.Should().Be(hardware.RequestId);

        modelReply.Models.Should().ContainSingle().Which.ModelId.Should().Be("whisper-small");
        hardwareReply.CudaAvailable.Should().BeFalse();
        hardwareReply.Warnings.Should().ContainSingle();

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
    }

    [RequiresPythonFact]
    public async Task An_error_reply_carrying_the_requestId_faults_the_request()
    {
        await using var client = NewClient();
        await client.StartAsync().WaitAsync(Timeout);

        var command = new ProcessCommand
        {
            JobId = "job-1",
            VideoPath = "/videos/a.mkv",
            OutputPath = "/videos/a.ko.srt",
            CheckpointDir = "/cache/job-1",
            Settings = new WorkerJobSettings()
        };

        var act = async () => await client.RequestAsync<CompletedEvent>(command).WaitAsync(Timeout);

        (await act.Should().ThrowAsync<WorkerRequestFailedException>())
            .Which.ErrorCode.Should().Be("TRANSCRIPTION_FAILED");

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
    }

    // -----------------------------------------------------------------------
    // resilience
    // -----------------------------------------------------------------------

    /// <summary>
    /// The queue retries a recoverable failure the instant it is reported. When the watchdog is the
    /// reporter, the worker it has just killed may not have exited yet — and StartAsync used to hand
    /// that dying instance to the retry, whose job was then failed in 0.0s by the old exit event.
    /// </summary>
    [RequiresPythonFact]
    public async Task A_start_requested_as_the_watchdog_fires_gets_a_fresh_worker()
    {
        var locator = new StubToolLocator(
            StubScript,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["KSUBMAKER_STUB_HANG_PROCESS"] = "1" },
            _workspace.Root);

        await using var client = new WorkerProcessClient(
            locator,
            Paths,
            Options.Create(new WorkerOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(30),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
                IdleTimeout = TimeSpan.FromSeconds(1), // the poll interval clamps to 5s
                TerminateOnIdleTimeout = true,
                HostVersion = "integration-test"
            }),
            NullLogger<WorkerProcessClient>.Instance);

        var original = await client.StartAsync().WaitAsync(Timeout);

        var restart = new TaskCompletionSource<Task<ReadyEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (_, e) =>
        {
            // Exactly what the queue's auto-retry does: react to the synthesised error by starting.
            if (e is ErrorEvent { Code: "WORKER_CRASHED" })
            {
                restart.TrySetResult(client.StartAsync());
            }
        };

        await client.SendAsync(new ProcessCommand
        {
            JobId = "job-hang",
            VideoPath = "/videos/a.mkv",
            OutputPath = "/videos/a.ko.srt",
            CheckpointDir = "/cache/job-hang",
            Settings = new WorkerJobSettings()
        }).WaitAsync(Timeout);

        var restarted = await (await restart.Task.WaitAsync(Timeout)).WaitAsync(Timeout);

        restarted.Should().NotBeSameAs(original, "the condemned instance must never be reused");

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
    }

    [RequiresPythonFact]
    public async Task Garbage_on_stdout_never_reaches_a_subscriber_and_never_breaks_the_channel()
    {
        await using var client = NewClient();

        var received = new ConcurrentQueue<WorkerEvent>();
        client.EventReceived += (_, e) => received.Enqueue(e);

        await client.StartAsync().WaitAsync(Timeout);

        // The stub emits two junk lines before every single reply.
        var first = await client.RequestAsync<ProbeResultEvent>(new ProbeCommand { VideoPath = "/a.mkv" }).WaitAsync(Timeout);
        var second = await client.RequestAsync<ModelListEvent>(new ListModelsCommand()).WaitAsync(Timeout);

        first.Should().NotBeNull();
        second.Should().NotBeNull();

        received.Should().NotBeEmpty();
        received.Should().NotContain(e => e is UnknownEvent,
            "unparseable lines are logged and dropped, never surfaced to subscribers");

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
    }

    [RequiresPythonFact]
    public async Task A_subscriber_that_throws_does_not_kill_the_reader_loop()
    {
        await using var client = NewClient();

        client.EventReceived += (_, _) => throw new InvalidOperationException("구독자 예외");

        await client.StartAsync().WaitAsync(Timeout);

        var reply = await client.RequestAsync<ProbeResultEvent>(new ProbeCommand { VideoPath = "/a.mkv" }).WaitAsync(Timeout);

        reply.Should().NotBeNull();
        client.IsRunning.Should().BeTrue();

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);
    }

    [RequiresPythonFact]
    public async Task Sending_a_command_before_the_worker_starts_is_rejected_clearly()
    {
        await using var client = NewClient();

        var act = async () => await client.SendAsync(new ListModelsCommand());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // -----------------------------------------------------------------------
    // shutdown
    // -----------------------------------------------------------------------

    [RequiresPythonFact]
    public async Task StopAsync_shuts_the_worker_down_cleanly_and_leaves_no_orphan_process()
    {
        var pidFile = Path.Combine(_workspace.Root, "stub.pid");

        await using var client = NewClient(pidFile: pidFile);

        var exited = new TaskCompletionSource<WorkerExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Exited += (_, args) => exited.TrySetResult(args);

        await client.StartAsync().WaitAsync(Timeout);

        var pid = await ReadPidAsync(pidFile);
        IsProcessAlive(pid).Should().BeTrue();

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);

        client.IsRunning.Should().BeFalse();

        var args = await exited.Task.WaitAsync(Timeout);
        args.Expected.Should().BeTrue("a graceful shutdown must not be reported as a crash");
        args.ExitCode.Should().Be(0);

        await WaitUntilAsync(() => !IsProcessAlive(pid), TimeSpan.FromSeconds(15));

        IsProcessAlive(pid).Should().BeFalse("the worker process must not survive StopAsync");
    }

    [RequiresPythonFact]
    public async Task DisposeAsync_alone_is_enough_to_kill_the_worker()
    {
        var pidFile = Path.Combine(_workspace.Root, "stub-dispose.pid");

        var client = NewClient(pidFile: pidFile);
        await client.StartAsync().WaitAsync(Timeout);

        var pid = await ReadPidAsync(pidFile);
        IsProcessAlive(pid).Should().BeTrue();

        await client.DisposeAsync();

        await WaitUntilAsync(() => !IsProcessAlive(pid), TimeSpan.FromSeconds(15));
        IsProcessAlive(pid).Should().BeFalse();
    }

    [RequiresPythonFact]
    public async Task StopAsync_can_be_called_twice()
    {
        await using var client = NewClient();
        await client.StartAsync().WaitAsync(Timeout);

        await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);

        var act = async () => await client.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(Timeout);

        await act.Should().NotThrowAsync();
    }

    [RequiresPythonFact]
    public async Task A_worker_that_cannot_be_launched_reports_a_startup_failure()
    {
        var locator = new StubToolLocator(
            Path.Combine(_workspace.Root, "no-such-script.py"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            _workspace.Root);

        await using var client = new WorkerProcessClient(
            locator,
            Paths,
            Options.Create(new WorkerOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(15),
                IdleTimeout = TimeSpan.Zero
            }),
            NullLogger<WorkerProcessClient>.Instance);

        var act = async () => await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));

        await act.Should().ThrowAsync<WorkerStartupException>();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    /// <summary>
    /// An <see cref="IToolLocator"/> that launches <c>python3 &lt;stub&gt;</c>. It also implements
    /// <see cref="IWorkerLaunchDescriptor"/> so the extra environment variables the stub needs reach
    /// the child process — the same seam the production locator uses for PYTHONPATH.
    /// </summary>
    private sealed class StubToolLocator(
        string scriptPath,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory) : IToolLocator, IWorkerLaunchDescriptor
    {
        public string FfmpegPath => "ffmpeg";

        public string FfprobePath => "ffprobe";

        public (string Executable, IReadOnlyList<string> Arguments) WorkerCommandLine =>
            ("python3", [scriptPath]);

        public bool TryValidate(out string? error)
        {
            error = null;
            return true;
        }

        public WorkerLaunchInfo DescribeWorkerLaunch() => new()
        {
            Kind = WorkerLaunchKind.PathPython,
            Executable = "python3",
            Arguments = [scriptPath],
            Environment = environment,
            WorkingDirectory = workingDirectory,
            Resolved = true,
            Description = $"테스트 스텁 ({scriptPath})"
        };

        public ToolValidationResult ValidateTools() => new(true, null, null);
    }
}
