using CommunityToolkit.Mvvm.ComponentModel;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// One row of the 모델 관리 grid.
///
/// The row owns the <see cref="CancellationTokenSource"/> for its own download, which is what makes
/// 일시정지 work: cancelling leaves the partially written <c>.part</c> file in place, and 재개 starts
/// the same download again, which resumes from that offset with an HTTP Range request.
/// </summary>
public sealed partial class ModelRowViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _downloadCts;
    private bool _disposed;

    public ModelRowViewModel(ModelStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        Id = status.Descriptor.Id;
        Kind = status.Descriptor.Kind;
        _displayName = status.Descriptor.DisplayName;
        _description = status.Descriptor.Description;
        _license = status.Descriptor.License;
        _sizeBytes = status.Descriptor.ApproxSizeBytes;
        _estimatedVramGb = status.EstimatedVramGb;
        _isInstalled = status.Installation.Installed;
        _isRecommended = status.IsRecommended;
        _isDownloading = status.IsDownloading;
        _downloadPercent = status.DownloadPercent;
    }

    public string Id { get; }

    public ModelKind Kind { get; }

    public string KindText => DisplayText.ModelKindName(Kind);

    [ObservableProperty]
    private string _displayName;

    /// <summary>The model the settings point at for this step — the 사용 radio.</summary>
    [ObservableProperty]
    private bool _isInUse;

    /// <summary>
    /// Sets <see cref="IsInUse"/> and always announces it: a radio the user clicked shows checked
    /// even when the change was then declined, and only a fresh notification puts it back.
    /// </summary>
    public void SetInUse(bool value)
    {
        IsInUse = value;
        OnPropertyChanged(nameof(IsInUse));
    }

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string _license;

    [ObservableProperty]
    private long _sizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VramText))]
    private double _estimatedVramGb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isVerifying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isPausedDownload;

    [ObservableProperty]
    private bool _isRecommended;

    [ObservableProperty]
    private double _downloadPercent;

    [ObservableProperty]
    private string _progressDetail = string.Empty;

    public string VramText => DisplayText.GigabytesOrDash(EstimatedVramGb);

    public string StateText
    {
        get
        {
            if (IsVerifying)
            {
                return Strings.ModelStateVerifying;
            }

            if (IsDownloading)
            {
                return Strings.ModelStateDownloading;
            }

            if (IsPausedDownload)
            {
                return Strings.ModelStatePaused;
            }

            return IsInstalled ? Strings.ModelStateInstalled : Strings.ModelStateNotInstalled;
        }
    }

    /// <summary>Creates the token source for a new download, replacing any stale one.</summary>
    public CancellationToken BeginDownload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _downloadCts, cts)?.Dispose();

        IsDownloading = true;
        IsPausedDownload = false;
        ProgressDetail = string.Empty;
        return cts.Token;
    }

    /// <summary>Cancels the in-flight download. Returns false when nothing was running.</summary>
    public bool RequestPause()
    {
        var cts = _downloadCts;
        if (cts is null)
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Clears the in-flight state after the download task has settled.</summary>
    public void EndDownload(bool paused)
    {
        IsDownloading = false;
        IsPausedDownload = paused;

        if (!paused)
        {
            ProgressDetail = string.Empty;
        }

        Interlocked.Exchange(ref _downloadCts, null)?.Dispose();
    }

    public void Apply(ModelStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        DisplayName = status.Descriptor.DisplayName;
        Description = status.Descriptor.Description;
        License = status.Descriptor.License;
        SizeBytes = status.Installation.SizeBytes > 0
            ? status.Installation.SizeBytes
            : status.Descriptor.ApproxSizeBytes;
        EstimatedVramGb = status.EstimatedVramGb;
        IsInstalled = status.Installation.Installed;
        IsRecommended = status.IsRecommended;

        // A refresh must not clobber the live state of a download this row is driving.
        if (!IsDownloading)
        {
            DownloadPercent = status.IsDownloading ? status.DownloadPercent : (status.Installation.Installed ? 100d : 0d);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _downloadCts, null)?.Dispose();
    }
}
