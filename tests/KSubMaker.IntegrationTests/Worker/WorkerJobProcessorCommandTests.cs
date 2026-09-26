using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.Infrastructure.Paths;
using KSubMaker.IntegrationTests.Infrastructure;
using KSubMaker.Worker;
using KSubMaker.Worker.Processing;
using KSubMaker.WorkerProtocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KSubMaker.IntegrationTests.Worker;

/// <summary>
/// What actually goes over the wire for one job.
///
/// Everything the user chooses on the settings screen or the job row only matters if it survives the
/// trip into <see cref="ProcessCommand"/>; each of the fields covered here was, at some point, simply
/// not sent, and the symptom was always silent wrong behaviour rather than an error.
/// </summary>
public sealed class WorkerJobProcessorCommandTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TempWorkspace _workspace = new("ksubmaker-command");

    public void Dispose() => _workspace.Dispose();

    // -----------------------------------------------------------------------
    // a worker client that records commands and answers immediately
    // -----------------------------------------------------------------------

    private sealed class RecordingWorkerClient : IWorkerClient
    {
        public List<WorkerCommand> Sent { get; } = [];

        /// <summary>Raised before the reply, to stand in for another job's events.</summary>
        public List<WorkerEvent> Precede { get; } = [];

        /// <summary>Reply produced for each <c>process</c> command; the default is a plain success.</summary>
        public Func<ProcessCommand, WorkerEvent> Reply { get; set; } = command => new CompletedEvent
        {
            JobId = command.JobId,
            RequestId = command.RequestId,
            OutputPath = command.OutputPath,
            CueCount = 12
        };

        public bool IsRunning { get; private set; }

        public event EventHandler<WorkerEvent>? EventReceived;

        public event EventHandler<WorkerExitedEventArgs>? Exited;

        public Task<ReadyEvent> StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.FromResult(new ReadyEvent { WorkerVersion = "fake", PythonVersion = "3.11.0" });
        }

        public Task SendAsync(WorkerCommand command, CancellationToken cancellationToken = default)
        {
            Sent.Add(command);

            if (command is ProcessCommand process)
            {
                // Events the worker emits for *other* jobs before this one's answer. The prefetch
                // lane is the real source of these.
                foreach (var stray in Precede)
                {
                    EventReceived?.Invoke(this, stray);
                }

                // Raised on a pool thread, like the real client's reader task, so the processor's
                // completion source is exercised the same way.
                var reply = Reply(process);
                _ = Task.Run(() => EventReceived?.Invoke(this, reply), CancellationToken.None);
            }

            return Task.CompletedTask;
        }

        public Task<TEvent> RequestAsync<TEvent>(WorkerCommand command, CancellationToken cancellationToken = default)
            where TEvent : WorkerEvent =>
            throw new NotSupportedException("이 테스트는 요청/응답 명령을 쓰지 않습니다.");

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            _ = Exited;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // harness
    // -----------------------------------------------------------------------

    private async Task<(ProcessCommand Command, JobExecutionResult Result)> RunAsync(
        Job job,
        AppSettings settings,
        JobPhase phase = JobPhase.Full,
        Func<ProcessCommand, WorkerEvent>? reply = null,
        IEnumerable<WorkerEvent>? precede = null)
    {
        var client = new RecordingWorkerClient();
        if (reply is not null)
        {
            client.Reply = reply;
        }

        if (precede is not null)
        {
            client.Precede.AddRange(precede);
        }

        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));

        var processor = new WorkerJobProcessor(
            client,
            paths,
            Options.Create(new WorkerOptions()),
            NullLogger<WorkerJobProcessor>.Instance);

        var result = await processor
            .ProcessAsync(job, settings, phase, new Progress<JobProgress>(_ => { }), CancellationToken.None)
            .WaitAsync(Timeout);

        var command = client.Sent.OfType<ProcessCommand>().Should().ContainSingle().Subject;
        return (command, result);
    }

    private static Job NewJob(string name = "clip.mkv") => new()
    {
        Id = "job-1",
        VideoPath = "/videos/" + name,
        FileName = name,
        DurationSeconds = 60d
    };

    // -----------------------------------------------------------------------
    // wordTimestamps — the flag that keeps a broken conversion from killing the worker
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Word_timestamps_are_forced_off_for_a_model_that_cannot_survive_them()
    {
        // From a real run: selecting kotoba-whisper-v2.0 with word timestamps on ended the worker
        // with exit code -1073741819 (ACCESS_VIOLATION) seconds into transcription. Its
        // alignment_heads name decoder layers 7..25 while the distilled decoder has two, so
        // CTranslate2 reads past the end of the array — in native code, where no handler exists.
        var settings = new AppSettings
        {
            WhisperModel = ModelIds.WhisperKotobaV2,
            WordTimestamps = true
        };

        var (command, _) = await RunAsync(NewJob(), settings);

        command.Settings.WordTimestamps.Should().BeFalse(
            "asking this conversion for word timings crashes the worker process outright");
    }

    [Fact]
    public async Task Word_timestamps_are_left_alone_for_every_other_model()
    {
        var settings = new AppSettings
        {
            WhisperModel = ModelIds.WhisperLargeV3,
            WordTimestamps = true
        };

        var (command, _) = await RunAsync(NewJob(), settings);

        command.Settings.WordTimestamps.Should().BeTrue();
    }

    [Fact]
    public async Task Turning_word_timestamps_off_stays_off_regardless_of_the_model()
    {
        var settings = new AppSettings
        {
            WhisperModel = ModelIds.WhisperLargeV3,
            WordTimestamps = false
        };

        var (command, _) = await RunAsync(NewJob(), settings);

        command.Settings.WordTimestamps.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // outputConflictPolicy
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(OutputConflictPolicy.Skip, OutputConflictPolicies.Skip)]
    [InlineData(OutputConflictPolicy.Overwrite, OutputConflictPolicies.Overwrite)]
    [InlineData(OutputConflictPolicy.CreateNumberedCopy, OutputConflictPolicies.Numbered)]
    public async Task The_output_conflict_policy_reaches_the_worker(
        OutputConflictPolicy policy,
        string expected)
    {
        var (command, _) = await RunAsync(NewJob(), new AppSettings { OutputConflictPolicy = policy });

        command.Settings.OutputConflictPolicy.Should().Be(expected);
    }

    [Fact]
    public async Task The_conflict_policy_is_serialised_onto_the_wire()
    {
        var (command, _) = await RunAsync(
            NewJob(),
            new AppSettings { OutputConflictPolicy = OutputConflictPolicy.Overwrite });

        var line = WorkerProtocolSerializer.SerializeCommand(command);

        line.Should().Contain("\"outputConflictPolicy\":\"overwrite\"");

        var parsed = WorkerProtocolSerializer.DeserializeCommand(line).Should().BeOfType<ProcessCommand>().Subject;
        parsed.Settings.OutputConflictPolicy.Should().Be(OutputConflictPolicies.Overwrite);
    }

    [Fact]
    public async Task A_worker_that_declined_to_write_reports_the_job_as_skipped()
    {
        var (_, result) = await RunAsync(
            NewJob(),
            new AppSettings(),
            reply: command => new CompletedEvent
            {
                JobId = command.JobId,
                RequestId = command.RequestId,
                OutputPath = command.OutputPath,
                CueCount = 0,
                Skipped = true
            });

        result.Success.Should().BeTrue();
        result.Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task A_worker_that_wrote_the_file_does_not_report_a_skip()
    {
        var (_, result) = await RunAsync(NewJob(), new AppSettings());

        result.Skipped.Should().BeFalse();
        result.CueCount.Should().Be(12);
    }

    // -----------------------------------------------------------------------
    // the MVP core path stays the default
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_job_with_no_override_takes_the_core_audio_path()
    {
        var (command, _) = await RunAsync(NewJob(), new AppSettings());

        command.SourceMode.Should().Be(SourceModes.Audio);
        command.AudioTrackIndex.Should().BeNull("null lets FFmpeg pick the default stream");
        command.SubtitleTrackIndex.Should().BeNull();
        command.SubtitleLanguage.Should().BeNull();
    }

    [Fact]
    public async Task An_embedded_subtitle_track_is_ignored_without_an_override_or_a_policy()
    {
        var job = NewJob();
        job.HasEmbeddedSubtitle = true;

        var (command, _) = await RunAsync(job, new AppSettings());

        command.SourceMode.Should().Be(SourceModes.Audio);
    }

    // -----------------------------------------------------------------------
    // the per-file override
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_embedded_subtitle_override_sends_the_track_index_and_its_language()
    {
        var job = NewJob("애니.mkv");
        job.HasEmbeddedSubtitle = true;
        job.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        job.SelectedSubtitleTrackIndex = 4;
        job.SelectedSubtitleLanguage = "ja";

        var (command, _) = await RunAsync(job, new AppSettings());

        command.SourceMode.Should().Be(SourceModes.EmbeddedSubtitle);
        command.SubtitleTrackIndex.Should().Be(4);
        command.SubtitleLanguage.Should().Be("ja");
        command.AudioTrackIndex.Should().BeNull();
    }

    [Fact]
    public async Task An_audio_track_override_sends_the_chosen_stream()
    {
        var job = NewJob();
        job.SourceOverride = JobSourceOverride.Audio;
        job.SelectedAudioTrackIndex = 2;

        var (command, _) = await RunAsync(job, new AppSettings());

        command.SourceMode.Should().Be(SourceModes.Audio);
        command.AudioTrackIndex.Should().Be(2);
        command.SubtitleTrackIndex.Should().BeNull();
        command.SubtitleLanguage.Should().BeNull();
    }

    /// <summary>
    /// The whole point of the per-file picker: the user looked at the tracks, so their answer beats
    /// the application-wide setting.
    /// </summary>
    [Fact]
    public async Task The_override_wins_over_the_application_wide_policy()
    {
        var job = NewJob();
        job.HasEmbeddedSubtitle = true;
        job.SourceOverride = JobSourceOverride.Audio;

        var settings = new AppSettings { SubtitleSource = SubtitleSourcePreference.PreferEmbeddedTrack };

        var (command, _) = await RunAsync(job, settings);

        command.SourceMode.Should().Be(SourceModes.Audio);
    }

    [Fact]
    public async Task The_use_embedded_track_policy_still_works_without_a_per_file_override()
    {
        var job = NewJob();
        job.HasEmbeddedSubtitle = true;
        job.SelectedSubtitleTrackIndex = 1;
        job.SelectedSubtitleLanguage = "fr";

        var settings = new AppSettings { SubtitleSource = SubtitleSourcePreference.PreferEmbeddedTrack };

        var (command, _) = await RunAsync(job, settings);

        command.SourceMode.Should().Be(SourceModes.EmbeddedSubtitle);
        command.SubtitleTrackIndex.Should().Be(1);
        command.SubtitleLanguage.Should().Be("fr");
    }

    // -----------------------------------------------------------------------
    // externalSubtitle (v1.5) — the sidecar is read off disk at dispatch time
    // -----------------------------------------------------------------------

    /// <summary>Lays out a video with the given sidecars and returns the video's path.</summary>
    private static string StageSidecars(string videoName, params string[] sidecars)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ksm-sidecar-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        var video = Path.Combine(directory, videoName);
        File.WriteAllText(video, "not really a video");

        foreach (var sidecar in sidecars)
        {
            File.WriteAllText(Path.Combine(directory, sidecar), "1\n00:00:01,000 --> 00:00:02,000\n대사\n");
        }

        return video;
    }

    [Fact]
    public async Task The_use_external_subtitle_policy_sends_the_ranked_sidecar()
    {
        var video = StageSidecars("clip.mkv", "clip.en.srt", "clip.ja.srt", "clip.ko.srt");

        var job = NewJob();
        job.VideoPath = video;

        var settings = new AppSettings { SubtitleSource = SubtitleSourcePreference.PreferExternalFile };

        var (command, _) = await RunAsync(job, settings);

        command.SourceMode.Should().Be(SourceModes.ExternalSubtitle);
        Path.GetFileName(command.SubtitlePath).Should().Be("clip.ja.srt");
        command.SubtitleLanguage.Should().Be("ja");
        command.AudioTrackIndex.Should().BeNull();
    }

    [Fact]
    public async Task A_video_with_no_usable_sidecar_falls_back_to_speech_recognition()
    {
        // "Use the sidecar when there is one" — not "fail when there is not". A ko.srt is the
        // pipeline's own output and must not count as a source.
        var video = StageSidecars("clip.mkv", "clip.ko.srt");

        var job = NewJob();
        job.VideoPath = video;

        var settings = new AppSettings { SubtitleSource = SubtitleSourcePreference.PreferExternalFile };

        var (command, _) = await RunAsync(job, settings);

        command.SourceMode.Should().Be(SourceModes.Audio);
        command.SubtitlePath.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_directory_degrades_to_speech_recognition_instead_of_failing()
    {
        var job = NewJob();
        var settings = new AppSettings { SubtitleSource = SubtitleSourcePreference.PreferExternalFile };

        var (command, _) = await RunAsync(job, settings);

        command.SourceMode.Should().Be(SourceModes.Audio);
    }

    [Fact]
    public async Task The_per_file_external_subtitle_override_works_without_the_policy()
    {
        var video = StageSidecars("clip.mkv", "clip.ja.srt");

        var job = NewJob();
        job.VideoPath = video;
        job.SourceOverride = JobSourceOverride.ExternalSubtitle;

        var (command, _) = await RunAsync(job, new AppSettings());

        command.SourceMode.Should().Be(SourceModes.ExternalSubtitle);
        Path.GetFileName(command.SubtitlePath).Should().Be("clip.ja.srt");
    }

    [Fact]
    public async Task An_external_subtitle_job_is_never_prefetched()
    {
        // Prefetch demuxes audio. There is no audio in this path, so a prefetch would extract a wav
        // the job then ignores — the checkpoint directory pays for it and nothing reads it.
        var video = StageSidecars("clip.mkv", "clip.ja.srt");

        var job = NewJob();
        job.VideoPath = video;
        job.SourceOverride = JobSourceOverride.ExternalSubtitle;

        var (command, _) = await RunAsync(job, new AppSettings());

        command.SourceMode.Should().NotBe(SourceModes.Audio);
    }

    // -----------------------------------------------------------------------
    // subtitleLanguage fallbacks
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_untagged_track_falls_back_to_the_configured_source_language()
    {
        var job = NewJob();
        job.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        job.SelectedSubtitleTrackIndex = 0;

        var (command, _) = await RunAsync(job, new AppSettings { SourceLanguage = "de" });

        command.SubtitleLanguage.Should().Be("de");
    }

    [Fact]
    public async Task An_untagged_track_with_auto_detection_leaves_the_field_off_the_wire()
    {
        var job = NewJob();
        job.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        job.SelectedSubtitleTrackIndex = 0;

        var (command, _) = await RunAsync(job, new AppSettings { SourceLanguage = "auto" });

        // There is nothing to auto-detect in an already-written subtitle file, so the worker's own
        // documented fallback is the honest answer rather than the string "auto".
        command.SubtitleLanguage.Should().BeNull();
        WorkerProtocolSerializer.SerializeCommand(command).Should().NotContain("subtitleLanguage");
    }

    [Fact]
    public async Task The_track_language_beats_the_configured_source_language()
    {
        var job = NewJob();
        job.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        job.SelectedSubtitleTrackIndex = 0;
        job.SelectedSubtitleLanguage = "ja";

        var (command, _) = await RunAsync(job, new AppSettings { SourceLanguage = "en" });

        command.SubtitleLanguage.Should().Be("ja");
    }

    [Fact]
    public async Task The_subtitle_language_round_trips_through_the_serializer()
    {
        var job = NewJob();
        job.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        job.SelectedSubtitleTrackIndex = 7;
        job.SelectedSubtitleLanguage = "ja";

        var (command, _) = await RunAsync(job, new AppSettings());

        var line = WorkerProtocolSerializer.SerializeCommand(command);
        var parsed = WorkerProtocolSerializer.DeserializeCommand(line).Should().BeOfType<ProcessCommand>().Subject;

        parsed.SubtitleLanguage.Should().Be("ja");
        parsed.SubtitleTrackIndex.Should().Be(7);
        parsed.SourceMode.Should().Be(SourceModes.EmbeddedSubtitle);
    }

    // -----------------------------------------------------------------------
    // glossary, which the settings screen can now actually populate
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_glossary_reaches_the_worker()
    {
        var settings = new AppSettings
        {
            Glossary = GlossaryRules.Build(
            [
                new KeyValuePair<string, string>("Sherlock", "셜록"),
                new KeyValuePair<string, string>("Baker Street", "베이커가")
            ])
        };

        var (command, _) = await RunAsync(NewJob(), settings);

        command.Settings.Glossary.Should().HaveCount(2);
        command.Settings.Glossary["Sherlock"].Should().Be("셜록");
    }


    // -----------------------------------------------------------------------
    // 남의 작업 이벤트가 이 작업을 끝내면 안 된다
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Another_jobs_completed_event_does_not_finish_this_one()
    {
        // 실기 신고: 인식 못 하는 파일 뒤의 파일이 "아무 작업도 하지 않고 완료" 로 표시됐다.
        //
        // 워커 이벤트는 구독자 전원에게 방송되고, 미리 추출 레인은 extractAudio 를 끝낼 때
        // output_path="" · cue_count=0 인 completed 를 낸다. 그것이 실행 중인 작업을 성공으로
        // 해소해 버려, 자막이 한 줄도 쓰이지 않았는데 완료가 됐다.
        var stray = new CompletedEvent
        {
            JobId = "another-job",
            RequestId = "other-request",
            OutputPath = string.Empty,
            CueCount = 0,
            Skipped = true
        };

        var (_, result) = await RunAsync(NewJob(), new AppSettings(), precede: [stray]);

        result.Success.Should().BeTrue();
        result.CueCount.Should().Be(12, "이 작업의 진짜 결과라야 한다");
        result.OutputPath.Should().NotBeNullOrEmpty();
        result.Skipped.Should().BeFalse("남의 미리 추출 결과를 물려받으면 안 된다");
    }

    [Fact]
    public async Task Another_jobs_error_does_not_fail_this_one()
    {
        var stray = new ErrorEvent
        {
            JobId = "another-job",
            RequestId = "other-request",
            Code = ErrorCodes.VideoUnreadable,
            Message = "남의 작업이 실패했습니다."
        };

        var (_, result) = await RunAsync(NewJob(), new AppSettings(), precede: [stray]);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task A_worker_wide_event_without_a_job_id_still_reaches_the_job()
    {
        // jobId 가 없는 것은 워커 전체 이벤트(로그, 하드웨어 보고)다. 걸러내면 안 된다.
        var broadcast = new LogEvent { Message = "worker 전역 메시지", Level = "info" };

        var (_, result) = await RunAsync(NewJob(), new AppSettings(), precede: [broadcast]);

        result.Success.Should().BeTrue();
    }
}
