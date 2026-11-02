using System.Collections.Concurrent;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;

namespace KSubMaker.UnitTests.Fakes;

/// <summary>An <see cref="IJobRepository"/> backed by a dictionary. Records nothing it is not asked to.</summary>
public sealed class InMemoryJobRepository(params Job[] jobs) : IJobRepository
{
    private readonly ConcurrentDictionary<string, Job> _jobs =
        new(jobs.ToDictionary(j => j.Id, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Ids handed to <see cref="UpdateAsync"/>, in call order.</summary>
    public List<string> Updated { get; } = [];

    public Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Job>>(_jobs.Values.ToArray());

    public Task<Job?> FindAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);

    public Task<Job?> FindByPathAsync(string videoPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.Values.FirstOrDefault(j =>
            string.Equals(j.VideoPath, videoPath, StringComparison.OrdinalIgnoreCase)));

    public Task AddAsync(Job job, CancellationToken cancellationToken = default)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task AddRangeAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default)
    {
        foreach (var job in jobs)
        {
            _jobs[job.Id] = job;
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Job job, CancellationToken cancellationToken = default)
    {
        Updated.Add(job.Id);
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task UpdateRangeAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default)
    {
        foreach (var job in jobs)
        {
            Updated.Add(job.Id);
            _jobs[job.Id] = job;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        _jobs.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task RemoveRangeAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        foreach (var id in ids)
        {
            _jobs.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    public Task<int> ResetOrphanedActiveJobsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

/// <summary>
/// A selector whose processor must never be reached. Used by tests that drive the queue's bookkeeping
/// without starting it, so an accidental run fails loudly instead of silently doing work.
/// </summary>
public sealed class NeverRunsProcessorSelector : IJobProcessorSelector
{
    public IJobProcessor Select(AppSettings settings) =>
        throw new InvalidOperationException("이 테스트는 큐를 실행하지 않습니다.");
}

/// <summary>The simplest usable <see cref="IHardwareDetector"/>: a CPU-only machine.</summary>
public sealed class CpuOnlyHardwareDetector : IHardwareDetector
{
    public Task<HardwareProfile> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new HardwareProfile { CpuName = "Test CPU", LogicalCoreCount = 4 });
}

/// <summary>An <see cref="ICheckpointStore"/> that records the orphan sweep and can be told to fail.</summary>
public sealed class RecordingCheckpointStore : ICheckpointStore
{
    public List<string[]> CleanupCalls { get; } = [];

    /// <summary>Job ids handed to <see cref="ClearAsync"/>, in call order.</summary>
    public List<string> Cleared { get; } = [];

    /// <summary>
    /// Ids whose cache delete must fail, standing in for a locked <c>audio.wav</c> or a directory an
    /// antivirus scanner still has open.
    /// </summary>
    public HashSet<string> FailClearFor { get; } = new(StringComparer.Ordinal);

    public long Reclaimed { get; set; } = 4096L;

    public Exception? ThrowOnCleanup { get; set; }

    public Task<long> CleanupOrphansAsync(
        IReadOnlyCollection<string> knownJobIds,
        CancellationToken cancellationToken = default)
    {
        CleanupCalls.Add([.. knownJobIds]);

        return ThrowOnCleanup is not null
            ? Task.FromException<long>(ThrowOnCleanup)
            : Task.FromResult(Reclaimed);
    }

    public Task<JobCheckpoint?> LoadAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<JobCheckpoint?>(null);

    public Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<TranscriptionResult?> LoadTranscriptionAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<TranscriptionResult?>(null);

    public Task SaveTranscriptionAsync(string jobId, TranscriptionResult result, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyDictionary<int, string>> LoadPartialTranslationAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());

    public Task SavePartialTranslationAsync(string jobId, IReadOnlyDictionary<int, string> translations, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Job ids whose extracted audio was reclaimed, in call order.</summary>
    public List<string> AudioDeleted { get; } = [];

    public Task<long> DeleteAudioAsync(string jobId, CancellationToken cancellationToken = default)
    {
        lock (AudioDeleted)
        {
            AudioDeleted.Add(jobId);
        }

        return Task.FromResult(1024L);
    }

    public Task ClearAsync(string jobId, CancellationToken cancellationToken = default)
    {
        Cleared.Add(jobId);

        return FailClearFor.Contains(jobId)
            ? Task.FromException(new IOException($"캐시 폴더가 잠겨 있습니다: {jobId}"))
            : Task.CompletedTask;
    }
}

/// <summary>Always hands out the same processor.</summary>
public sealed class SingleProcessorSelector(IJobProcessor processor) : IJobProcessorSelector
{
    public IJobProcessor Select(AppSettings settings) => processor;
}

/// <summary>
/// Replays a scripted list of outcomes, one per call, reporting the stages a real processor would
/// report along the way.
///
/// Written for the automatic-retry path: "fail recoverably, then succeed" is the exact shape that
/// used to throw <c>InvalidJobTransitionException</c> inside the queue and end up relabelled UNKNOWN.
/// Reporting progress matters as much as the outcome does — it is what drags the job's status
/// forward, which is what the retry then has to transition back out of.
/// </summary>
public sealed class ScriptedJobProcessor(params JobExecutionResult[] outcomes) : IJobProcessor
{
    private readonly JobExecutionResult[] _outcomes = outcomes;
    private int _calls;

    public string Name => "테스트용 시나리오 프로세서";

    /// <summary>How many times the pump entered this processor.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Job status observed on entry to each call, in call order.</summary>
    public List<JobStatus> StatusOnEntry { get; } = [];

    /// <summary>Ids the prefetch lane asked to extract, in the order it asked.</summary>
    public List<string> Prefetched { get; } = [];

    public Task<AudioPrefetchOutcome> PrefetchAudioAsync(
        Job job,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (Prefetched)
        {
            Prefetched.Add(job.Id);
        }

        return Task.FromResult(AudioPrefetchOutcome.Extracted);
    }

    /// <summary>Stages reported before each outcome. Empty means "report nothing at all".</summary>
    public JobStage[] StagesToReport { get; init; } =
        [JobStage.ExtractingAudio, JobStage.Transcribing, JobStage.Translating];

    public Task<JobExecutionResult> ProcessAsync(
        Job job,
        AppSettings settings,
        JobPhase phase,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var index = Interlocked.Increment(ref _calls) - 1;
        StatusOnEntry.Add(job.Status);

        foreach (var stage in StagesToReport)
        {
            progress.Report(new JobProgress
            {
                JobId = job.Id,
                Stage = stage,
                StageProgress = 100d,
                OverallProgress = ProgressCalculator.Overall(stage, 100d)
            });
        }

        // Past the end of the script the processor keeps succeeding, so a test that only cares about
        // the first two calls does not have to spell out a third.
        var outcome = index < _outcomes.Length
            ? _outcomes[index]
            : JobExecutionResult.Ok($"/videos/{job.Id}.ko.srt", cueCount: 1);

        return Task.FromResult(outcome);
    }
}

/// <summary>
/// A processor that parks until the test lets it go, so a job can be held in the running state while
/// something else happens to it.
/// </summary>
public sealed class BlockingJobProcessor : IJobProcessor
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Name => "테스트용 대기 프로세서";

    /// <summary>
    /// False makes the processor deaf to cancellation, which is how "멈추지 않는 작업" — a worker
    /// wedged inside a native call — is simulated.
    /// </summary>
    public bool HonoursCancellation { get; init; } = true;

    /// <summary>Completes once the pump has actually entered the processor.</summary>
    public Task Started => _started.Task;

    private int _calls;

    /// <summary>How many times the pump entered this processor.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Lets the parked job finish. Safe to call more than once.</summary>
    public void Release() => _release.TrySetResult();

    /// <summary>Ids the prefetch lane asked to extract, in the order it asked.</summary>
    public List<string> Prefetched { get; } = [];

    public Task<AudioPrefetchOutcome> PrefetchAudioAsync(
        Job job,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (Prefetched)
        {
            Prefetched.Add(job.Id);
        }

        return Task.FromResult(AudioPrefetchOutcome.Extracted);
    }

    public async Task<JobExecutionResult> ProcessAsync(
        Job job,
        AppSettings settings,
        JobPhase phase,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        _started.TrySetResult();

        if (HonoursCancellation)
        {
            await using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _release);

            await _release.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            await _release.Task.ConfigureAwait(false);
        }

        return JobExecutionResult.Ok($"/videos/{job.Id}.ko.srt", cueCount: 1);
    }
}
