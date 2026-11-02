using CommunityToolkit.Mvvm.ComponentModel;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Jobs;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// One row of the job grid.
///
/// The row is a projection of a <see cref="Job"/>, never the job itself: the queue pump mutates the
/// domain object from a background thread, and binding a <c>DataGrid</c> straight to it would mean
/// the UI reading fields while they are being written. <see cref="Update"/> is the only way values
/// come in, and it always runs on the dispatcher thread.
/// </summary>
public sealed partial class JobRowViewModel : ObservableObject
{
    public JobRowViewModel(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        Id = job.Id;
        _fileName = job.FileName;
        _fullPath = job.VideoPath;
        Update(job);
    }

    /// <summary>Stable key used to find this row again when the queue reports a change.</summary>
    public string Id { get; }

    /// <summary>Checkbox state of the 선택 column. Not part of the domain model.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private double _durationSeconds;

    /// <summary>Source file size in bytes; the 용량 column sorts on this and formats with BytesToString.</summary>
    [ObservableProperty]
    private long _fileSizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private JobStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageText))]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private JobStage _stage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverallText))]
    private double _overallProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double _stageProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double _processingSpeed;

    [ObservableProperty]
    private TimeSpan? _estimatedTimeRemaining;

    [ObservableProperty]
    private string? _detectedLanguage;

    [ObservableProperty]
    private string? _model;

    [ObservableProperty]
    private string? _outputPath;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Kept so 상태 can tell a silent track (음성 없음) from an ordinary 건너뜀.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string? _errorCode;

    /// <summary>Free-text 메모 column; edited through the row's right-click menu.</summary>
    [ObservableProperty]
    private string? _note;

    // ---- 자막 원본 --------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleSourceText))]
    private JobSourceOverride _sourceOverride;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleSourceText))]
    private int? _selectedAudioTrackIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleSourceText))]
    private int? _selectedSubtitleTrackIndex;

    [ObservableProperty]
    private string? _selectedSubtitleLanguage;

    /// <summary>Korean label for the 자막 원본 column.</summary>
    public string SubtitleSourceText =>
        DisplayText.SubtitleSourceName(SourceOverride, SelectedAudioTrackIndex, SelectedSubtitleTrackIndex);

    /// <summary>Korean label for <see cref="Status"/>.</summary>
    public string StatusText => DisplayText.StatusName(Status);

    /// <summary>Korean label for <see cref="Stage"/>.</summary>
    public string StageText => DisplayText.StageName(Stage);

    /// <summary>
    /// What the 상태 column shows. While a job runs, 상태 and 단계 say the same thing, so only the
    /// status is shown. The one case where they differ is a job that stopped part-way — 실패 or
    /// 일시정지 — and there the stage it reached is appended (실패 · 한국어 번역까지), which is the
    /// information the separate 현재 단계 column used to carry.
    /// </summary>
    public string StatusSummary =>
        Status == JobStatus.Skipped && ErrorCode == ErrorCodes.NoSpeech
            ? Strings.JobStatusNoSpeech
            : Status is JobStatus.Failed or JobStatus.Paused
        && Stage is not (JobStage.None or JobStage.Done)
            ? $"{StatusText} · {StageText}까지"
            : StatusText;

    /// <summary>
    /// The words on the 진행 bar. The bar is the whole job (it is full only when the subtitle is
    /// written); the words are the stage under way and how far into it — the figure that is actually
    /// measured, and the one that shows a long 음성 인식 is still moving while the bar barely does.
    /// Whole percents: the bar is an estimate, and a decimal point would claim otherwise.
    /// </summary>
    public string ProgressText
    {
        get
        {
            if (Status == JobStatus.Completed)
            {
                return Strings.JobStatusCompleted;
            }

            // Audio prefetched for a job still waiting its turn: done ahead, nothing running.
            if (Status == JobStatus.Pending && Stage == JobStage.ExtractingAudio && StageProgress >= 100d)
            {
                return Strings.ProgressAudioReady;
            }

            // Finished without a subtitle — 음성 없음, 건너뜀: the bar is grey, the words say why.
            if (Status == JobStatus.Skipped)
            {
                return StatusSummary;
            }

            if (Stage is JobStage.None or JobStage.Done)
            {
                return Status == JobStatus.Cancelled ? StatusText : string.Empty;
            }

            var stage = $"{StageText} {Math.Floor(StageProgress):0}%";

            // Stopped part-way: say so, and where.
            return Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Paused
                ? $"{StatusText} · {stage}"
                : stage;
        }
    }

    /// <summary>Tooltip on the 진행 bar: the whole-job figure the bar draws.</summary>
    public string OverallText => $"전체 약 {Math.Floor(OverallProgress):0}%";

    /// <summary>Media seconds per wall-clock second, or "-" while nothing is running.</summary>
    public string SpeedText => DisplayText.Speed(ProcessingSpeed);

    // Delegated rather than restated: the row's idea of what 시작, 재시도 and 취소 accept has to be the
    // same one the commands use, or a button and its handler can disagree about the same row.

    public bool IsRunnable => JobSelectionResolver.IsEligible(JobAction.Start, Status);

    public bool IsRetryable => JobSelectionResolver.IsEligible(JobAction.Retry, Status);

    public bool IsCancellable => JobSelectionResolver.IsEligible(JobAction.Cancel, Status);

    /// <summary>
    /// Copies the current state of <paramref name="job"/> onto this row.
    ///
    /// Every assignment goes through a generated setter that compares first, so a progress tick that
    /// only moved <see cref="StageProgress"/> raises exactly one property-changed event instead of
    /// fifteen. Each field is read into a local once, because the pump may write to the job while
    /// this runs and a value read twice could otherwise be inconsistent between two properties.
    /// </summary>
    public void Update(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var status = job.Status;
        var stage = job.CurrentStage;
        var overall = job.OverallProgress;
        var stageProgress = job.StageProgress;
        var speed = job.ProcessingSpeed;
        var eta = job.EstimatedTimeRemaining;

        FileName = job.FileName;
        FullPath = job.VideoPath;
        DurationSeconds = job.DurationSeconds;
        FileSizeBytes = job.FileSize;

        Status = status;
        Stage = stage;
        OverallProgress = double.IsFinite(overall) ? Math.Clamp(overall, 0d, 100d) : 0d;
        StageProgress = double.IsFinite(stageProgress) ? Math.Clamp(stageProgress, 0d, 100d) : 0d;
        ProcessingSpeed = double.IsFinite(speed) ? speed : 0d;
        EstimatedTimeRemaining = eta;

        DetectedLanguage = job.DetectedLanguage;
        Model = job.WhisperModel ?? job.TranslationModel;
        OutputPath = job.OutputPath;
        ErrorMessage = job.ErrorMessage;
        ErrorCode = job.ErrorCode;
        Note = job.Note;

        SourceOverride = job.SourceOverride;
        SelectedAudioTrackIndex = job.SelectedAudioTrackIndex;
        SelectedSubtitleTrackIndex = job.SelectedSubtitleTrackIndex;
        SelectedSubtitleLanguage = job.SelectedSubtitleLanguage;
    }

    partial void OnStatusChanged(JobStatus value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsRunnable));
        OnPropertyChanged(nameof(IsRetryable));
        OnPropertyChanged(nameof(IsCancellable));
    }
}
