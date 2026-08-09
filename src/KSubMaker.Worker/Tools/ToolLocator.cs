using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Errors;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Worker.Tools;

/// <summary>How the worker process is going to be launched. Purely for logging and diagnostics.</summary>
public enum WorkerLaunchKind
{
    /// <summary>Nothing usable was found; <see cref="WorkerLaunchInfo.Executable"/> is a best guess.</summary>
    NotFound,

    /// <summary>The PyInstaller/Nuitka build shipped next to the app (<c>worker/ksubmaker-worker.exe</c>).</summary>
    FrozenExecutable,

    /// <summary>The embedded CPython under <c>tools/python</c> running <c>-m ksubmaker_worker</c>.</summary>
    BundledPython,

    /// <summary>An interpreter named by the <c>KSUBMAKER_WORKER_PYTHON</c> environment variable.</summary>
    EnvironmentPython,

    /// <summary>A <c>python3</c>/<c>python</c> found on PATH. Development fallback only.</summary>
    PathPython
}

/// <summary>
/// Everything needed to start the worker, including the pieces that do not fit on
/// <see cref="IToolLocator.WorkerCommandLine"/>.
/// </summary>
/// <remarks>
/// <see cref="IToolLocator"/> lives in the Application layer and deliberately exposes only
/// (executable, arguments). Launching an unfrozen Python worker additionally needs PYTHONPATH and a
/// working directory, so that extra metadata is published through this host-local interface instead of
/// widening the shared contract.
/// </remarks>
public interface IWorkerLaunchDescriptor
{
    WorkerLaunchInfo DescribeWorkerLaunch();

    /// <summary>Same check as <see cref="IToolLocator.TryValidate"/> but with a machine-readable code.</summary>
    ToolValidationResult ValidateTools();
}

/// <param name="Ok">False when at least one required executable is missing.</param>
/// <param name="ErrorCode">One of <see cref="ErrorCodes"/>; null when <paramref name="Ok"/>.</param>
/// <param name="Message">Korean message naming the first missing tool; null when <paramref name="Ok"/>.</param>
public readonly record struct ToolValidationResult(bool Ok, string? ErrorCode, string? Message);

/// <summary>Resolved worker command line plus the environment it needs.</summary>
public sealed record WorkerLaunchInfo
{
    public required WorkerLaunchKind Kind { get; init; }
    public required string Executable { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Extra environment variables (PYTHONPATH for the source-tree launch modes).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? WorkingDirectory { get; init; }

    /// <summary>True when the executable was actually found on disk (or is expected to come from PATH).</summary>
    public bool Resolved { get; init; }

    /// <summary>Korean one-liner for the log file.</summary>
    public required string Description { get; init; }
}

/// <summary>
/// Finds the bundled ffmpeg / ffprobe / Python worker.
///
/// The single rule that drives the whole class: <b>never assume PATH</b>. A user with a random ffmpeg
/// build somewhere on PATH must still get the copy we shipped and tested against, otherwise audio
/// extraction fails in ways we cannot reproduce. PATH is consulted last, and only so that developers
/// (and this Linux CI host) can run without a bundled tools directory.
/// </summary>
public sealed class ToolLocator : IToolLocator, IWorkerLaunchDescriptor
{
    private const string FrozenWorkerStem = "ksubmaker-worker";
    private const string WorkerPythonEnvironmentVariable = "KSUBMAKER_WORKER_PYTHON";
    private const string WorkerModuleName = "ksubmaker_worker";

    private readonly IAppPaths _paths;
    private readonly ILogger<ToolLocator> _logger;

    private readonly Lazy<string?> _ffmpeg;
    private readonly Lazy<string?> _ffprobe;
    private readonly Lazy<WorkerLaunchInfo> _worker;

    public ToolLocator(IAppPaths paths, ILogger<ToolLocator> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _ffmpeg = new Lazy<string?>(() => Probe("ffmpeg"), LazyThreadSafetyMode.ExecutionAndPublication);
        _ffprobe = new Lazy<string?>(() => Probe("ffprobe"), LazyThreadSafetyMode.ExecutionAndPublication);
        _worker = new Lazy<WorkerLaunchInfo>(ResolveWorker, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string FfmpegPath =>
        _ffmpeg.Value ?? throw new FileNotFoundException(
            "FFmpeg 실행 파일을 찾을 수 없습니다. 설치 폴더의 tools/ffmpeg 디렉터리를 확인하세요.",
            ExecutableName("ffmpeg"));

    public string FfprobePath =>
        _ffprobe.Value ?? throw new FileNotFoundException(
            "FFprobe 실행 파일을 찾을 수 없습니다. 설치 폴더의 tools/ffmpeg 디렉터리를 확인하세요.",
            ExecutableName("ffprobe"));

    public (string Executable, IReadOnlyList<string> Arguments) WorkerCommandLine
    {
        get
        {
            var info = _worker.Value;
            return (info.Executable, info.Arguments);
        }
    }

    public WorkerLaunchInfo DescribeWorkerLaunch() => _worker.Value;

    public bool TryValidate(out string? error)
    {
        var result = ValidateTools();
        error = result.Message;
        return result.Ok;
    }

    /// <summary>Reports the <b>first</b> missing tool so the user gets one actionable sentence, not a list.</summary>
    public ToolValidationResult ValidateTools()
    {
        if (_ffmpeg.Value is null)
        {
            return new ToolValidationResult(
                false,
                ErrorCodes.FfmpegNotFound,
                $"FFmpeg 실행 파일({ExecutableName("ffmpeg")})을 찾을 수 없습니다. 설치가 손상되었을 수 있습니다.");
        }

        if (_ffprobe.Value is null)
        {
            return new ToolValidationResult(
                false,
                ErrorCodes.FfmpegNotFound,
                $"FFprobe 실행 파일({ExecutableName("ffprobe")})을 찾을 수 없습니다. 설치가 손상되었을 수 있습니다.");
        }

        var worker = _worker.Value;
        if (!worker.Resolved)
        {
            return new ToolValidationResult(
                false,
                ErrorCodes.WorkerCrashed,
                "AI 작업 프로세스(Python worker)를 찾을 수 없습니다. 설치가 손상되었거나 Python 런타임이 누락되었습니다.");
        }

        return new ToolValidationResult(true, null, null);
    }

    // -----------------------------------------------------------------------
    // ffmpeg / ffprobe
    // -----------------------------------------------------------------------

    /// <summary>
    /// Probe order: <c>tools/ffmpeg/bin</c> → <c>tools</c> → app base directory → PATH.
    /// The first three are all "ours"; PATH is the last resort.
    /// </summary>
    private string? Probe(string stem)
    {
        var fileName = ExecutableName(stem);
        var toolsDirectory = SafeToolsDirectory();

        var candidates = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(toolsDirectory))
        {
            candidates.Add(Path.Combine(toolsDirectory, "ffmpeg", "bin", fileName));
            candidates.Add(Path.Combine(toolsDirectory, fileName));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, fileName));

        foreach (var candidate in candidates)
        {
            if (FileExists(candidate))
            {
                _logger.LogDebug("{Tool} 경로: {Path} (번들)", stem, candidate);
                return Path.GetFullPath(candidate);
            }
        }

        var fromPath = SearchPath(fileName);
        if (fromPath is not null)
        {
            // Reaching here in production means the bundle is broken; the app still works but the
            // FFmpeg build is untested, so say so loudly in the log.
            _logger.LogWarning("{Tool}을(를) 번들에서 찾지 못해 PATH의 {Path}을(를) 사용합니다.", stem, fromPath);
            return fromPath;
        }

        _logger.LogError("{Tool} 실행 파일을 찾지 못했습니다. (파일명 {FileName})", stem, fileName);
        return null;
    }

    // -----------------------------------------------------------------------
    // worker
    // -----------------------------------------------------------------------

    private WorkerLaunchInfo ResolveWorker()
    {
        var info = ResolveWorkerCore();
        _logger.LogInformation("Worker 실행 방식: {Description}", info.Description);
        return info;
    }

    private WorkerLaunchInfo ResolveWorkerCore()
    {
        var toolsDirectory = SafeToolsDirectory();
        var moduleArguments = new[] { "-m", WorkerModuleName };

        // (a) frozen build shipped next to the app -- the production path.
        var frozen = Path.Combine(AppContext.BaseDirectory, "worker", ExecutableName(FrozenWorkerStem));
        if (FileExists(frozen))
        {
            return new WorkerLaunchInfo
            {
                Kind = WorkerLaunchKind.FrozenExecutable,
                Executable = Path.GetFullPath(frozen),
                Arguments = [],
                WorkingDirectory = AppContext.BaseDirectory,
                Resolved = true,
                Description = $"동봉된 실행 파일 ({frozen})"
            };
        }

        var sourceDirectory = FindWorkerSourceDirectory();

        // (b) embedded CPython under tools/python, running the module from the repo source tree.
        if (!string.IsNullOrWhiteSpace(toolsDirectory))
        {
            var bundledPython = Path.Combine(toolsDirectory, "python", ExecutableName("python"));
            if (FileExists(bundledPython))
            {
                return new WorkerLaunchInfo
                {
                    Kind = WorkerLaunchKind.BundledPython,
                    Executable = Path.GetFullPath(bundledPython),
                    Arguments = moduleArguments,
                    Environment = PythonPath(sourceDirectory),
                    WorkingDirectory = sourceDirectory ?? AppContext.BaseDirectory,
                    Resolved = true,
                    Description = $"동봉된 Python ({bundledPython}) -m {WorkerModuleName}"
                };
            }
        }

        // (c) explicit override, used by the integration tests and by developers with a venv.
        var overridePython = System.Environment.GetEnvironmentVariable(WorkerPythonEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePython))
        {
            var resolved = FileExists(overridePython)
                ? Path.GetFullPath(overridePython)
                : SearchPath(overridePython);

            return new WorkerLaunchInfo
            {
                Kind = WorkerLaunchKind.EnvironmentPython,
                Executable = resolved ?? overridePython,
                Arguments = moduleArguments,
                Environment = PythonPath(sourceDirectory),
                WorkingDirectory = sourceDirectory ?? AppContext.BaseDirectory,
                Resolved = resolved is not null,
                Description = $"{WorkerPythonEnvironmentVariable}={overridePython} -m {WorkerModuleName}"
            };
        }

        // (d) PATH. Development fallback only -- never the production path.
        foreach (var stem in new[] { "python3", "python" })
        {
            var found = SearchPath(ExecutableName(stem));
            if (found is null)
            {
                continue;
            }

            return new WorkerLaunchInfo
            {
                Kind = WorkerLaunchKind.PathPython,
                Executable = found,
                Arguments = moduleArguments,
                Environment = PythonPath(sourceDirectory),
                WorkingDirectory = sourceDirectory ?? AppContext.BaseDirectory,
                Resolved = true,
                Description = $"PATH의 {found} -m {WorkerModuleName}"
            };
        }

        return new WorkerLaunchInfo
        {
            Kind = WorkerLaunchKind.NotFound,
            Executable = ExecutableName("python3"),
            Arguments = moduleArguments,
            Environment = PythonPath(sourceDirectory),
            WorkingDirectory = sourceDirectory ?? AppContext.BaseDirectory,
            Resolved = false,
            Description = "실행 가능한 worker를 찾지 못했습니다."
        };
    }

    private static IReadOnlyDictionary<string, string> PythonPath(string? sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var existing = System.Environment.GetEnvironmentVariable("PYTHONPATH");
        var value = string.IsNullOrWhiteSpace(existing)
            ? sourceDirectory
            : sourceDirectory + Path.PathSeparator + existing;

        return new Dictionary<string, string>(StringComparer.Ordinal) { ["PYTHONPATH"] = value };
    }

    /// <summary>
    /// Locates the repository <c>worker/</c> directory (the parent of the <c>ksubmaker_worker</c>
    /// package) by walking up from the app base directory. Only used by the non-frozen launch modes;
    /// in production the frozen executable carries its own code.
    /// </summary>
    private string? FindWorkerSourceDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory;
            try
            {
                directory = new DirectoryInfo(start);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
            {
                continue;
            }

            for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "worker");
                if (DirectoryExists(Path.Combine(candidate, WorkerModuleName)))
                {
                    return candidate;
                }
            }
        }

        _logger.LogDebug("저장소의 worker/ 디렉터리를 찾지 못했습니다. PYTHONPATH를 설정하지 않습니다.");
        return null;
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    /// <summary>Windows ships <c>.exe</c>; everywhere else the extension-less name is correct.</summary>
    private static string ExecutableName(string stem) =>
        OperatingSystem.IsWindows() ? stem + ".exe" : stem;

    private string? SafeToolsDirectory()
    {
        try
        {
            var directory = _paths.ToolsDirectory;
            return string.IsNullOrWhiteSpace(directory) ? null : directory;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "도구 디렉터리를 확인하지 못했습니다.");
            return null;
        }
    }

    private static bool FileExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool DirectoryExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Last-resort PATH scan. Returns an absolute path, never a bare file name.</summary>
    private static string? SearchPath(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            return FileExists(fileName) ? Path.GetFullPath(fileName) : null;
        }

        var path = System.Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(entry.Trim('"'), fileName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (OperatingSystem.IsWindows() && candidate.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                // Windows Store 앱 실행 별칭(0바이트 마이크로소프트 스토어 연결 더미 python.exe/python3.exe)은 실제 파이썬이 아니므로 스킵
                continue;
            }

            if (FileExists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
