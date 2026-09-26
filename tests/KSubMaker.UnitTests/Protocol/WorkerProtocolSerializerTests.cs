using System.Text.Json;
using FluentAssertions;
using KSubMaker.WorkerProtocol;
using Xunit;

namespace KSubMaker.UnitTests.Protocol;

/// <summary>Covers "Worker JSON 파싱": every command and event round-trips through the codec.</summary>
public sealed class WorkerProtocolSerializerTests
{
    private const string RequestId = "req-0123456789";

    public static TheoryData<WorkerCommand> AllCommands => new(CommandSamples);

    private static readonly WorkerCommand[] CommandSamples =
    [
        new HelloCommand { RequestId = RequestId, HostVersion = "0.1.0" },
        new DetectHardwareCommand { RequestId = RequestId },
        new ProbeCommand { RequestId = RequestId, VideoPath = @"C:\영상 폴더\한국어 제목.mkv" },
        new ProcessCommand
        {
            RequestId = RequestId,
            JobId = "job-1",
            VideoPath = "/videos/영화.mkv",
            OutputPath = "/videos/영화.ko.srt",
            CheckpointDir = "/cache/job-1",
            SourceMode = SourceModes.EmbeddedSubtitle,
            AudioTrackIndex = 2,
            SubtitleTrackIndex = 1,
            SubtitleLanguage = "ja",
            Resume = false,
            Phase = "transcribe",
            Settings = new WorkerJobSettings
            {
                Language = "en",
                WhisperModel = "whisper-large-v3",
                ComputeType = "int8_float16",
                Device = "cuda",
                BeamSize = 3,
                VadFilter = false,
                WordTimestamps = false,
                ConditionOnPreviousText = true,
                InitialPrompt = "登場人物: 佐藤, 鈴木。",
                TranslationEngine = "local-llm",
                TranslationModel = "nllb-200-distilled-1.3B",
                LlmModel = "qwen2.5-7b-instruct-q4km",
                TranslationStyle = "polite",
                BatchMaxItems = 12,
                BatchMaxChars = 900,
                BatchMaxSeconds = 60,
                ContextLines = 5,
                Glossary = new Dictionary<string, string> { ["Seoul"] = "서울", ["Han River"] = "한강" },
                MaxLinesPerCue = 3,
                MaxCharsPerLine = 18,
                MinCueDurationSeconds = 1.25d,
                MaxCueDurationSeconds = 6.5d,
                MinCueGapMilliseconds = 80,
                MergeShortCues = false,
                OutputConflictPolicy = OutputConflictPolicies.Numbered,
                AutoRetryOnRecoverableError = false
            }
        },
        new CancelCommand { RequestId = RequestId, JobId = "job-1" },
        new CancelCommand { RequestId = RequestId, JobId = null },
        new ListModelsCommand { RequestId = RequestId },
        new DownloadModelCommand
        {
            RequestId = RequestId,
            ModelId = "whisper-small",
            RepositoryId = "Systran/faster-whisper-small",
            Files = ["config.json", "model.bin"],
            TargetDir = "/models/whisper-small"
        },
        new CancelDownloadCommand { RequestId = RequestId, ModelId = "whisper-small" },
        new VerifyModelCommand { RequestId = RequestId, ModelId = "whisper-small", TargetDir = "/models/whisper-small" },
        new DeleteModelCommand { RequestId = RequestId, ModelId = "whisper-small", TargetDir = "/models/whisper-small" },
        new ShutdownCommand { RequestId = RequestId }
    ];

    public static TheoryData<WorkerEvent> AllEvents => new(EventSamples);

    private static readonly WorkerEvent[] EventSamples =
    [
        new ReadyEvent
        {
            RequestId = RequestId,
            ProtocolVersion = ProtocolConstants.Version,
            WorkerVersion = "0.1.0",
            PythonVersion = "3.11.9",
            Capabilities = ["cuda", "llm"]
        },
        new AckEvent { RequestId = RequestId, Command = ProtocolConstants.Commands.Probe },
        new StartedEvent { RequestId = RequestId, JobId = "job-1", ResumedFromStage = "transcribing" },
        new ProgressEvent
        {
            RequestId = RequestId,
            JobId = "job-1",
            Stage = ProtocolConstants.Stages.Transcribing,
            StageProgress = 42.5d,
            OverallProgress = 31.75d,
            Speed = 8.25d,
            Message = "음성 인식 중"
        },
        new LanguageDetectedEvent { RequestId = RequestId, JobId = "job-1", Language = "en", Probability = 0.98d },
        new StageCompletedEvent { RequestId = RequestId, JobId = "job-1", Stage = ProtocolConstants.Stages.ExtractingAudio },
        new CompletedEvent
        {
            RequestId = RequestId,
            JobId = "job-1",
            OutputPath = "/videos/영화.ko.srt",
            CueCount = 412,
            SourceLanguage = "en",
            WhisperModel = "whisper-large-v3",
            TranslationEngine = "local-translation",
            TranslationModel = "nllb-200-distilled-600M",
            ElapsedSeconds = 123.456d,
            Skipped = true
        },
        new ErrorEvent
        {
            RequestId = RequestId,
            JobId = "job-1",
            Code = "CUDA_OUT_OF_MEMORY",
            Message = "GPU 메모리가 부족합니다.",
            Recoverable = true,
            Detail = "tried float16 on 4GB"
        },
        new CancelledEvent { RequestId = RequestId, JobId = "job-1" },
        new LogEvent { RequestId = RequestId, Level = "warning", Message = "모델을 다시 불러옵니다." },
        new HardwareEvent
        {
            RequestId = RequestId,
            Gpus =
            [
                new GpuDto
                {
                    Index = 0,
                    Name = "NVIDIA GeForce RTX 4070",
                    TotalVramBytes = 12_884_901_888L,
                    FreeVramBytes = 11_811_160_064L,
                    DriverVersion = "560.00",
                    ComputeCapability = "8.9"
                }
            ],
            CudaAvailable = true,
            CudaDeviceDetected = true,
            CudaLibrariesAvailable = true,
            MissingCudaLibraries = [],
            CudaVersion = "12.4",
            CpuName = "AMD Ryzen 7",
            LogicalCores = 16,
            TotalRamBytes = 34_359_738_368L,
            AvailableRamBytes = 21_474_836_480L,
            Warnings = ["nvidia-smi를 찾지 못했습니다."]
        },
        new ProbeResultEvent
        {
            RequestId = RequestId,
            VideoPath = "/videos/영화.mkv",
            DurationSeconds = 7261.482d,
            AudioTracks =
            [
                new AudioTrackDto { Index = 0, Language = "eng", Title = "Main", Codec = "aac", Channels = 6, IsDefault = true }
            ],
            SubtitleTracks =
            [
                new SubtitleTrackDto { Index = 0, Language = "kor", Title = "한국어", Codec = "subrip", IsForced = false, IsDefault = true }
            ],
            Container = "matroska,webm",
            Error = null
        },
        new ModelListEvent
        {
            RequestId = RequestId,
            Models =
            [
                new InstalledModelDto
                {
                    ModelId = "whisper-small",
                    Path = "/models/whisper-small",
                    Installed = true,
                    Verified = true,
                    SizeBytes = 507_510_784L,
                    DownloadedBytes = 507_510_784L,
                    Message = "정상"
                }
            ]
        },
        new DownloadProgressEvent
        {
            RequestId = RequestId,
            ModelId = "whisper-small",
            ReceivedBytes = 1_024L,
            TotalBytes = 4_096L,
            Percent = 25d,
            CurrentFile = "model.bin",
            SpeedBytesPerSecond = 512d
        },
        new DownloadCompletedEvent
        {
            RequestId = RequestId,
            ModelId = "whisper-small",
            Path = "/models/whisper-small",
            Verified = true,
            TotalBytes = 4_096L,
            Cancelled = false
        },
        new GoodbyeEvent { RequestId = RequestId }
    ];

    // -----------------------------------------------------------------------
    // commands
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Every_command_round_trips(WorkerCommand command)
    {
        var line = WorkerProtocolSerializer.SerializeCommand(command);

        var parsed = WorkerProtocolSerializer.DeserializeCommand(line);

        parsed.Should().NotBeNull();
        parsed.Should().BeOfType(command.GetType());
        parsed.Should().BeEquivalentTo(command, options => options.RespectingRuntimeTypes());
    }

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Every_command_writes_its_discriminator(WorkerCommand command)
    {
        using var document = JsonDocument.Parse(WorkerProtocolSerializer.SerializeCommand(command));

        document.RootElement.TryGetProperty("command", out var discriminator).Should().BeTrue();
        discriminator.GetString().Should().Be(command.Command);
    }

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Every_command_carries_its_requestId_and_protocol_version(WorkerCommand command)
    {
        using var document = JsonDocument.Parse(WorkerProtocolSerializer.SerializeCommand(command));

        document.RootElement.GetProperty("requestId").GetString().Should().Be(RequestId);
        document.RootElement.GetProperty("protocolVersion").GetString().Should().Be(ProtocolConstants.Version);
    }

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Every_command_serialises_to_exactly_one_line(WorkerCommand command)
    {
        var line = WorkerProtocolSerializer.SerializeCommand(command);

        line.Should().NotContain("\n").And.NotContain("\r");
    }

    [Fact]
    public void Command_ids_are_unique_across_the_whole_command_set()
    {
        var names = CommandSamples.Select(c => c.Command).Distinct().ToArray();

        names.Should().BeEquivalentTo(
        [
            ProtocolConstants.Commands.Hello,
            ProtocolConstants.Commands.DetectHardware,
            ProtocolConstants.Commands.Probe,
            ProtocolConstants.Commands.Process,
            ProtocolConstants.Commands.Cancel,
            ProtocolConstants.Commands.ListModels,
            ProtocolConstants.Commands.DownloadModel,
            ProtocolConstants.Commands.CancelDownload,
            ProtocolConstants.Commands.VerifyModel,
            ProtocolConstants.Commands.DeleteModel,
            ProtocolConstants.Commands.Shutdown
        ]);
    }

    [Fact]
    public void A_freshly_created_command_gets_a_unique_requestId()
    {
        var first = new ListModelsCommand();
        var second = new ListModelsCommand();

        first.RequestId.Should().NotBeNullOrWhiteSpace();
        second.RequestId.Should().NotBe(first.RequestId);
    }

    [Fact]
    public void Process_command_settings_survive_the_round_trip_field_by_field()
    {
        var original = CommandSamples.OfType<ProcessCommand>().Single();

        var line = WorkerProtocolSerializer.SerializeCommand(original);
        var parsed = (ProcessCommand)WorkerProtocolSerializer.DeserializeCommand(line)!;

        parsed.JobId.Should().Be(original.JobId);
        parsed.VideoPath.Should().Be(original.VideoPath);
        parsed.OutputPath.Should().Be(original.OutputPath);
        parsed.CheckpointDir.Should().Be(original.CheckpointDir);
        parsed.SourceMode.Should().Be(SourceModes.EmbeddedSubtitle);
        parsed.AudioTrackIndex.Should().Be(2);
        parsed.SubtitleTrackIndex.Should().Be(1);
        parsed.SubtitleLanguage.Should().Be("ja");
        parsed.Resume.Should().BeFalse();
        parsed.Phase.Should().Be("transcribe");
        parsed.Settings.Glossary.Should().BeEquivalentTo(original.Settings.Glossary);
        parsed.Settings.MinCueDurationSeconds.Should().Be(1.25d);
        parsed.Settings.MergeShortCues.Should().BeFalse();
        parsed.Settings.OutputConflictPolicy.Should().Be(OutputConflictPolicies.Numbered);
    }

    /// <summary>
    /// The 1.1 fields have to be written with exactly the names <c>commands.py</c> reads, and both
    /// have to keep defaulting to the 1.0 behaviour when a message omits them.
    /// </summary>
    [Fact]
    public void The_protocol_1_1_fields_use_the_names_the_worker_reads()
    {
        var line = WorkerProtocolSerializer.SerializeCommand(CommandSamples.OfType<ProcessCommand>().Single());

        line.Should().Contain("\"outputConflictPolicy\":\"numbered\"");
        line.Should().Contain("\"subtitleLanguage\":\"ja\"");
    }

    [Fact]
    public void A_process_command_without_the_1_1_fields_still_parses_with_the_old_behaviour()
    {
        const string line =
            """
            {"command":"process","requestId":"r1","protocolVersion":"1.0","jobId":"job-1",
             "videoPath":"/videos/a.mkv","outputPath":"/videos/a.ko.srt","checkpointDir":"/cache/job-1",
             "settings":{"language":"auto"}}
            """;

        var parsed = WorkerProtocolSerializer.DeserializeCommand(line.ReplaceLineEndings(string.Empty))
            .Should().BeOfType<ProcessCommand>().Subject;

        parsed.SubtitleLanguage.Should().BeNull();
        parsed.Settings.OutputConflictPolicy.Should().Be(OutputConflictPolicies.Skip, "skip never overwrites");
    }

    [Fact]
    public void The_conflict_policy_defaults_to_skip()
    {
        new WorkerJobSettings().OutputConflictPolicy.Should().Be(OutputConflictPolicies.Skip);
    }

    // -----------------------------------------------------------------------
    // Protocol 1.3 — the extractAudio command
    // -----------------------------------------------------------------------

    private static ExtractAudioCommand SampleExtract() => new()
    {
        RequestId = RequestId,
        JobId = "job-7",
        VideoPath = "/videos/영화.mkv",
        CheckpointDir = "/cache/job-7",
        AudioTrackIndex = 2,
        SourceMode = SourceModes.Audio,
        Settings = new WorkerJobSettings { Language = "ja" }
    };

    [Fact]
    public void An_extract_audio_command_round_trips()
    {
        var original = SampleExtract();

        var line = WorkerProtocolSerializer.SerializeCommand(original);
        var parsed = WorkerProtocolSerializer.DeserializeCommand(line)
            .Should().BeOfType<ExtractAudioCommand>().Subject;

        parsed.JobId.Should().Be(original.JobId);
        parsed.VideoPath.Should().Be(original.VideoPath);
        parsed.CheckpointDir.Should().Be(original.CheckpointDir);
        parsed.AudioTrackIndex.Should().Be(2);
        parsed.SourceMode.Should().Be(SourceModes.Audio);
        parsed.Settings.Language.Should().Be("ja");
    }

    /// <summary>
    /// AGENTS.md §6 step 4: a command missing from <c>ResolveCommandType</c> deserialises to null
    /// and is dropped without a word. This is the test that would have caught that.
    /// </summary>
    [Fact]
    public void The_extract_audio_command_is_registered_in_the_type_resolver()
    {
        var line = WorkerProtocolSerializer.SerializeCommand(SampleExtract());

        line.Should().Contain("\"command\":\"extractAudio\"");
        WorkerProtocolSerializer.DeserializeCommand(line).Should().NotBeNull();
    }

    [Fact]
    public void The_extract_audio_command_uses_the_field_names_the_worker_reads()
    {
        // commands.py reads these exact keys; a rename here is silently a no-op prefetch.
        var line = WorkerProtocolSerializer.SerializeCommand(SampleExtract());

        line.Should().Contain("\"jobId\":\"job-7\"");
        line.Should().Contain("\"checkpointDir\":\"/cache/job-7\"");
        line.Should().Contain("\"audioTrackIndex\":2");
        line.Should().Contain("\"sourceMode\":\"audio\"");
    }

    [Fact]
    public void An_extract_audio_command_defaults_to_the_audio_source_mode()
    {
        const string line =
            """
            {"command":"extractAudio","requestId":"r1","jobId":"job-1",
             "videoPath":"/videos/a.mkv","checkpointDir":"/cache/job-1","settings":{"language":"auto"}}
            """;

        var parsed = WorkerProtocolSerializer.DeserializeCommand(line.ReplaceLineEndings(string.Empty))
            .Should().BeOfType<ExtractAudioCommand>().Subject;

        parsed.SourceMode.Should().Be(SourceModes.Audio);
        parsed.AudioTrackIndex.Should().BeNull("null means 'let FFmpeg choose'");
    }

    // -----------------------------------------------------------------------
    // Protocol 1.2 — the CUDA split
    // -----------------------------------------------------------------------

    [Fact]
    public void The_protocol_version_is_1_6()
    {
        // 1.2 was the hardware event's three CUDA fields; 1.3 added the extractAudio command;
        // 1.4 added settings.initialPrompt; 1.5 added the externalSubtitle source mode;
        // 1.6 added settings.saveSourceSubtitle.
        // The Python mirror asserts the same literal (worker/tests/test_protocol.py).
        ProtocolConstants.Version.Should().Be("1.6");
        SourceModes.ExternalSubtitle.Should().Be("externalSubtitle");
    }

    [Fact]
    public void An_unset_initial_prompt_is_left_out_of_the_json_entirely()
    {
        // The worker records this field in the transcription fingerprint, and has since before any
        // host sent it. It reads it with `.get()`, so an absent key and an explicit null are the
        // same None — but "" is not, and would make every checkpoint a 1.3 host wrote mismatch and
        // re-run ASR that was already correct. Hence null-in, absent-out, never empty string.
        var line = WorkerProtocolSerializer.SerializeCommand(new ProcessCommand
        {
            JobId = "job-1",
            VideoPath = "/videos/movie.mkv",
            OutputPath = "/videos/movie.ko.srt",
            CheckpointDir = "/cache/job-1",
            Settings = new WorkerJobSettings()
        });

        line.Should().NotContain("initialPrompt");
    }

    [Fact]
    public void A_hardware_event_from_a_machine_missing_cublas_round_trips()
    {
        var original = new HardwareEvent
        {
            RequestId = RequestId,
            Gpus = [new GpuDto { Index = 0, Name = "NVIDIA GeForce RTX 3080 Ti", TotalVramBytes = 12L * 1024 * 1024 * 1024 }],
            CudaAvailable = false,
            CudaDeviceDetected = true,
            CudaLibrariesAvailable = false,
            MissingCudaLibraries = ["cublas64_12.dll", "cudnn64_9.dll"],
            CudaVersion = "13.1"
        };

        var line = WorkerProtocolSerializer.SerializeEvent(original);
        var parsed = WorkerProtocolSerializer.DeserializeEvent(line).Should().BeOfType<HardwareEvent>().Subject;

        parsed.CudaAvailable.Should().BeFalse();
        parsed.CudaDeviceDetected.Should().BeTrue();
        parsed.CudaLibrariesAvailable.Should().BeFalse();
        parsed.MissingCudaLibraries.Should().Equal("cublas64_12.dll", "cudnn64_9.dll");
    }

    [Fact]
    public void The_protocol_1_2_cuda_fields_use_the_names_the_worker_writes()
    {
        var line = WorkerProtocolSerializer.SerializeEvent(new HardwareEvent
        {
            CudaDeviceDetected = true,
            CudaLibrariesAvailable = false,
            MissingCudaLibraries = ["cublas64_12.dll"]
        });

        line.Should().Contain("\"cudaDeviceDetected\":true");
        line.Should().Contain("\"cudaLibrariesAvailable\":false");
        line.Should().Contain("\"missingCudaLibraries\":[\"cublas64_12.dll\"]");
    }

    /// <summary>
    /// A 1.1 worker never sends the new fields. Reading the absence as "libraries missing" would
    /// invent a CUDA failure on a machine that has none, so the default has to be optimistic while
    /// <c>cudaAvailable</c> stays exactly what the old worker said.
    /// </summary>
    [Fact]
    public void A_hardware_event_from_an_older_worker_defaults_the_new_fields_safely()
    {
        const string Line =
            """
            {"type":"hardware","gpus":[],"cudaAvailable":true,"cudaVersion":"12.4","cpuName":"x","logicalCores":8,"totalRamBytes":1,"availableRamBytes":1,"warnings":[]}
            """;

        var parsed = WorkerProtocolSerializer.DeserializeEvent(Line).Should().BeOfType<HardwareEvent>().Subject;

        parsed.CudaAvailable.Should().BeTrue();
        parsed.CudaLibrariesAvailable.Should().BeTrue("an absent field must not fabricate a failure");
        parsed.CudaDeviceDetected.Should().BeFalse("the 1.1 worker genuinely did not report it");
        parsed.MissingCudaLibraries.Should().BeEmpty();
    }

    [Fact]
    public void Korean_and_windows_paths_survive_unescaped()
    {
        var command = new ProbeCommand { VideoPath = @"C:\영상 폴더\한국어 제목.mkv" };

        var line = WorkerProtocolSerializer.SerializeCommand(command);

        line.Should().Contain("한국어 제목", "the relaxed encoder must not escape Hangul into \\uXXXX");
        WorkerProtocolSerializer.DeserializeCommand(line)
            .Should().BeOfType<ProbeCommand>()
            .Which.VideoPath.Should().Be(command.VideoPath);
    }

    [Fact]
    public void Null_optional_fields_are_omitted_from_the_wire_format()
    {
        var line = WorkerProtocolSerializer.SerializeCommand(new CancelCommand { JobId = null });

        line.Should().NotContain("jobId");
    }

    [Fact]
    public void SerializeCommand_rejects_null()
    {
        var act = () => WorkerProtocolSerializer.SerializeCommand(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"nope\":1}")]
    [InlineData("{\"command\":\"noSuchCommand\"}")]
    [InlineData("{\"command\":123}")]
    [InlineData("{ truncated")]
    public void DeserializeCommand_returns_null_for_anything_it_cannot_understand(string line)
    {
        WorkerProtocolSerializer.DeserializeCommand(line).Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // events
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Every_event_round_trips(WorkerEvent workerEvent)
    {
        var line = WorkerProtocolSerializer.SerializeEvent(workerEvent);

        var parsed = WorkerProtocolSerializer.DeserializeEvent(line);

        parsed.Should().BeOfType(workerEvent.GetType());
        parsed.Should().BeEquivalentTo(workerEvent, options => options.RespectingRuntimeTypes());
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Every_event_writes_its_type_discriminator(WorkerEvent workerEvent)
    {
        using var document = JsonDocument.Parse(WorkerProtocolSerializer.SerializeEvent(workerEvent));

        document.RootElement.TryGetProperty("type", out var discriminator).Should().BeTrue();
        discriminator.GetString().Should().Be(workerEvent.Type);
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Every_event_preserves_its_requestId(WorkerEvent workerEvent)
    {
        var line = WorkerProtocolSerializer.SerializeEvent(workerEvent);

        WorkerProtocolSerializer.DeserializeEvent(line).RequestId.Should().Be(RequestId);
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Every_event_serialises_to_exactly_one_line(WorkerEvent workerEvent)
    {
        var line = WorkerProtocolSerializer.SerializeEvent(workerEvent);

        line.Should().NotContain("\n").And.NotContain("\r");
    }

    [Fact]
    public void Event_types_are_unique_across_the_whole_event_set()
    {
        var types = EventSamples.Select(e => e.Type).ToArray();

        types.Should().OnlyHaveUniqueItems();
        types.Should().BeEquivalentTo(
        [
            ProtocolConstants.Events.Ready,
            ProtocolConstants.Events.Ack,
            ProtocolConstants.Events.Started,
            ProtocolConstants.Events.Progress,
            ProtocolConstants.Events.LanguageDetected,
            ProtocolConstants.Events.StageCompleted,
            ProtocolConstants.Events.Completed,
            ProtocolConstants.Events.Error,
            ProtocolConstants.Events.Cancelled,
            ProtocolConstants.Events.Log,
            ProtocolConstants.Events.Hardware,
            ProtocolConstants.Events.ProbeResult,
            ProtocolConstants.Events.ModelList,
            ProtocolConstants.Events.DownloadProgress,
            ProtocolConstants.Events.DownloadCompleted,
            ProtocolConstants.Events.Goodbye
        ]);
    }

    [Fact]
    public void Numbers_sent_as_json_strings_are_still_read_as_numbers()
    {
        // The Python side occasionally stringifies floats; NumberHandling.AllowReadingFromString covers it.
        const string Line = """
            {"type":"progress","stage":"transcribing","stageProgress":"42.5","overallProgress":"31.75","speed":"8.25"}
            """;

        var parsed = WorkerProtocolSerializer.DeserializeEvent(Line);

        parsed.Should().BeOfType<ProgressEvent>();
        var progress = (ProgressEvent)parsed;
        progress.StageProgress.Should().Be(42.5d);
        progress.OverallProgress.Should().Be(31.75d);
        progress.Speed.Should().Be(8.25d);
    }

    [Fact]
    public void Property_names_are_matched_case_insensitively()
    {
        const string Line = """
            {"type":"languageDetected","Language":"ko","PROBABILITY":0.87,"JobId":"job-9"}
            """;

        var parsed = WorkerProtocolSerializer.DeserializeEvent(Line);

        parsed.Should().BeOfType<LanguageDetectedEvent>();
        ((LanguageDetectedEvent)parsed).Language.Should().Be("ko");
        ((LanguageDetectedEvent)parsed).Probability.Should().Be(0.87d);
        parsed.JobId.Should().Be("job-9");
    }

    [Fact]
    public void Unknown_extra_fields_are_ignored_so_a_newer_worker_stays_compatible()
    {
        const string Line = """
            {"type":"cancelled","jobId":"job-1","somethingFromTheFuture":{"a":[1,2,3]}}
            """;

        WorkerProtocolSerializer.DeserializeEvent(Line)
            .Should().BeOfType<CancelledEvent>()
            .Which.JobId.Should().Be("job-1");
    }

    [Fact]
    public void SerializeEvent_rejects_null()
    {
        var act = () => WorkerProtocolSerializer.SerializeEvent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------
    // version compatibility
    // -----------------------------------------------------------------------

    [Fact]
    public void An_identical_protocol_version_is_compatible_without_a_warning()
    {
        WorkerProtocolSerializer.IsCompatible(ProtocolConstants.Version, out var warning).Should().BeTrue();
        warning.Should().BeNull();
    }

    [Fact]
    public void A_different_minor_version_is_tolerated_with_a_warning()
    {
        WorkerProtocolSerializer.IsCompatible("1.7", out var warning).Should().BeTrue();
        warning.Should().NotBeNullOrWhiteSpace();
        warning.Should().Contain("부 버전");
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.9")]
    public void A_different_major_version_is_incompatible(string workerVersion)
    {
        WorkerProtocolSerializer.IsCompatible(workerVersion, out var warning).Should().BeFalse();
        warning.Should().Contain("호환되지 않습니다");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_protocol_version_is_incompatible(string? workerVersion)
    {
        WorkerProtocolSerializer.IsCompatible(workerVersion, out var warning).Should().BeFalse();
        warning.Should().NotBeNullOrWhiteSpace();
    }
}
