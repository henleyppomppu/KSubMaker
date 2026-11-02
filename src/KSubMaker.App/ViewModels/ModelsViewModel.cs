using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KSubMaker.App.Collections;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// 모델 관리 screen: catalog listing plus download / pause / resume / delete / verify.
///
/// Downloads are driven from here rather than from the row so that the view model can keep the list
/// consistent (refresh after completion, single status line) while the row keeps ownership of its own
/// cancellation token.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject, IDisposable
{
    private readonly IModelManager _modelManager;
    private readonly SettingsService _settingsService;
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly ILogger<ModelsViewModel> _logger;

    private readonly Dictionary<string, Task> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public ModelsViewModel(
        IModelManager modelManager,
        SettingsService settingsService,
        IAppPaths paths,
        IDialogService dialogs,
        IShellService shell,
        ILogger<ModelsViewModel> logger)
    {
        _modelManager = modelManager;
        _settingsService = settingsService;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _paths = paths;
        _dialogs = dialogs;
        _shell = shell;
        _logger = logger;

        Models = [];
    }

    public BulkObservableCollection<ModelRowViewModel> Models { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private ModelRowViewModel? _selectedModel;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// The highlighted row of the 음성 인식 list. Each list has its own, because two grids bound to
    /// one SelectedItem push null into it whenever the other list's row is picked.
    /// </summary>
    [ObservableProperty]
    private ModelRowViewModel? _selectedRecognitionModel;

    /// <summary>The highlighted row of the 번역 list.</summary>
    [ObservableProperty]
    private ModelRowViewModel? _selectedTranslationModel;

    partial void OnSelectedRecognitionModelChanged(ModelRowViewModel? value)
    {
        if (value is not null)
        {
            SelectedModel = value;
            SelectedTranslationModel = null;
        }
    }

    partial void OnSelectedTranslationModelChanged(ModelRowViewModel? value)
    {
        if (value is not null)
        {
            SelectedModel = value;
            SelectedRecognitionModel = null;
        }
    }

    /// <summary>
    /// 사용 radio: makes this installed model the one its step uses — the same change, and the same
    /// question when the translation engine would switch, as the offer after a download.
    /// </summary>
    [RelayCommand]
    private async Task UseModelAsync(ModelRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            if (!row.IsInstalled)
            {
                _dialogs.ShowWarning(
                    string.Format(CultureInfo.CurrentCulture, Strings.ModelNotInstalledForUseFormat, row.DisplayName),
                    Strings.ModelUseAfterDownloadTitle);
                return;
            }

            await OfferToUseAsync(row, explicitRequest: true).ConfigureAwait(true);
        }
        finally
        {
            UpdateInUse();
        }
    }

    /// <summary>Marks, in each list, the model the settings point at.</summary>
    private void UpdateInUse()
    {
        var settings = _settingsService.Current;
        var translation = TranslationChoice.Selected(settings);
        var whisperAuto = settings.WhisperModel.Equals(ModelSelectionValidator.AutoModelId, StringComparison.OrdinalIgnoreCase);

        foreach (var row in Models)
        {
            var inUse = row.Kind switch
            {
                ModelKind.Whisper => whisperAuto
                    ? row.IsRecommended
                    : row.Id.Equals(settings.WhisperModel, StringComparison.OrdinalIgnoreCase),
                ModelKind.Translation => translation == TranslationChoice.AutoTranslationId
                    ? row.IsRecommended
                    : row.Id.Equals(translation, StringComparison.OrdinalIgnoreCase),
                ModelKind.Llm => translation == TranslationChoice.AutoLlmId
                    ? row.IsRecommended
                    : row.Id.Equals(translation, StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            row.SetInUse(inUse);
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings e) =>
        _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(UpdateInUse);

    [ObservableProperty]
    private bool _isBusy;

    private bool CanDownload => SelectedModel is { IsDownloading: false };

    private bool CanPause => SelectedModel is { IsDownloading: true };

    /// <summary>삭제 and 검증 act on files on disk: a model that is not installed has none.</summary>
    private bool CanMutateInstalled => SelectedModel is { IsDownloading: false, IsInstalled: true };

    /// <summary>
    /// The buttons follow the highlighted row's own state too: a download finishing or a delete
    /// completing changes what they can do without the selection moving.
    /// </summary>
    partial void OnSelectedModelChanged(ModelRowViewModel? oldValue, ModelRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnSelectedRowChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSelectedRowChanged;
        }
    }

    private void OnSelectedRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelRowViewModel.IsInstalled) or nameof(ModelRowViewModel.IsDownloading))
        {
            DownloadCommand.NotifyCanExecuteChanged();
            PauseDownloadCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            VerifyCommand.NotifyCanExecuteChanged();
        }
    }

    // -----------------------------------------------------------------------
    // Loading
    // -----------------------------------------------------------------------

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await RefreshAsync(cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsBusy = true;
            var statuses = await _modelManager.GetStatusAsync(cancellationToken).ConfigureAwait(true);

            if (Models.Count == 0)
            {
                var rows = statuses.Select(s => new ModelRowViewModel(s)).ToArray();
                Models.Reset(rows);
                SelectedRecognitionModel ??= rows.FirstOrDefault(r => r.Kind == ModelKind.Whisper);
            }
            else
            {
                // Update in place so an in-flight download keeps its row (and its token source).
                foreach (var status in statuses)
                {
                    var row = Models.FirstOrDefault(m =>
                        m.Id.Equals(status.Descriptor.Id, StringComparison.OrdinalIgnoreCase));

                    if (row is null)
                    {
                        Models.Add(new ModelRowViewModel(status));
                        continue;
                    }

                    row.Apply(status);
                }
            }

            UpdateInUse();
            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.ModelsLoadedFormat, Models.Count);
        }
        catch (OperationCanceledException)
        {
            // The window closed while the list was loading.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "모델 목록을 불러오지 못했습니다.");
            var message = string.Format(CultureInfo.CurrentCulture, Strings.ModelsRefreshFailedFormat, ex.Message);
            StatusMessage = message;
            _dialogs.ShowError(message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -----------------------------------------------------------------------
    // Download / pause / resume
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        var row = SelectedModel;
        if (row is null)
        {
            StatusMessage = Strings.ModelNotSelected;
            return;
        }

        if (_activeDownloads.ContainsKey(row.Id))
        {
            StatusMessage = Strings.ModelAlreadyDownloading;
            return;
        }

        var token = row.BeginDownload();
        DownloadCommand.NotifyCanExecuteChanged();
        PauseDownloadCommand.NotifyCanExecuteChanged();

        StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.ModelDownloadStartedFormat, row.DisplayName);

        // Created on the dispatcher thread, so every Report hops back onto it automatically.
        var progress = new Progress<ModelDownloadProgress>(update =>
        {
            row.DownloadPercent = update.Percent;
            row.ProgressDetail = string.Format(
                CultureInfo.CurrentCulture,
                Strings.ModelDownloadProgressFormat,
                DisplayText.Bytes(update.ReceivedBytes),
                DisplayText.Bytes(update.TotalBytes),
                DisplayText.Bytes((long)update.SpeedBytesPerSecond) + "/s");
        });

        var task = RunDownloadAsync(row, progress, token);
        _activeDownloads[row.Id] = task;

        try
        {
            await task.ConfigureAwait(true);
        }
        finally
        {
            _activeDownloads.Remove(row.Id);
            DownloadCommand.NotifyCanExecuteChanged();
            PauseDownloadCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            VerifyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Offers to point the matching settings slot at the model that just finished downloading.
    ///
    /// <para><b>Asks rather than applies.</b> Downloading a model is not the same as choosing it —
    /// someone comparing engines will fetch several, and silently repointing the settings on each
    /// completion would change what the next job runs without them noticing. The prompt is the
    /// cheap part; an unnoticed settings change is the expensive one.</para>
    ///
    /// <para>It stays quiet when the choice would not actually change: when the slot already names
    /// this model, and when the slot is <c>"자동"</c> and this is the recommended model, because
    /// <c>"auto"</c> already resolves here. That keeps the prompt meaningful instead of routine.</para>
    ///
    /// <para><b>The translation engine moves with the model.</b> Picking an LLM while the engine is
    /// still 전용 번역 모델 sets a field nothing reads: the run keeps using NLLB and the download
    /// changes nothing the user can see. So a translation-kind model also selects
    /// <see cref="TranslationEngineKind.LocalTranslationModel"/> and an LLM selects
    /// <see cref="TranslationEngineKind.LocalLlm"/>, and the prompt says so — switching engines is a
    /// bigger change than picking a model and must not happen behind the user's back.</para>
    /// </summary>
    /// <param name="explicitRequest">
    /// True for the 사용 radio: the user asked for exactly this, so only an engine switch is
    /// confirmed, and "already in use" is said rather than silently ignored.
    /// </param>
    private async Task OfferToUseAsync(ModelRowViewModel row, bool explicitRequest = false)
    {
        var settings = _settingsService.Current;

        var configured = row.Kind switch
        {
            ModelKind.Whisper => settings.WhisperModel,
            ModelKind.Translation => settings.TranslationModel,
            ModelKind.Llm => settings.LlmModel,
            _ => null
        };

        if (configured is null)
        {
            return;
        }

        // Null for Whisper: the ASR model says nothing about which translation engine runs.
        TranslationEngineKind? targetEngine = row.Kind switch
        {
            ModelKind.Translation => TranslationEngineKind.LocalTranslationModel,
            ModelKind.Llm => TranslationEngineKind.LocalLlm,
            _ => null
        };

        var engineNeedsChange = targetEngine is { } engine && settings.TranslationEngine != engine;

        var isAuto = string.IsNullOrWhiteSpace(configured) ||
                     configured.Equals(ModelSelectionValidator.AutoModelId, StringComparison.OrdinalIgnoreCase);

        var slotNeedsChange =
            !configured.Equals(row.Id, StringComparison.OrdinalIgnoreCase) &&
            !(isAuto && row.IsRecommended);

        // Ask when *either* half would move. The engine half is why this is not just the slot
        // check: re-downloading the model already selected, while the engine points elsewhere,
        // is exactly the case that used to leave the setting inert.
        if (!slotNeedsChange && !engineNeedsChange)
        {
            if (explicitRequest)
            {
                StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.ModelAlreadyInUseFormat, row.DisplayName);
            }

            return;
        }

        var question = string.Format(
            CultureInfo.CurrentCulture,
            Strings.ModelUseAfterDownloadQuestionFormat,
            row.DisplayName,
            ModelSelectionValidator.DescribeKind(row.Kind),
            isAuto ? Strings.ModelAutoOption : configured);

        if (engineNeedsChange)
        {
            question += Environment.NewLine + Environment.NewLine + string.Format(
                CultureInfo.CurrentCulture,
                Strings.ModelUseAfterDownloadEngineNote,
                DisplayText.TranslationEngineName(targetEngine!.Value),
                DisplayText.TranslationEngineName(settings.TranslationEngine));
        }

        if ((!explicitRequest || engineNeedsChange) && !_dialogs.Confirm(question, Strings.ModelUseAfterDownloadTitle))
        {
            return;
        }

        switch (row.Kind)
        {
            case ModelKind.Whisper:
                settings.WhisperModel = row.Id;
                break;
            case ModelKind.Translation:
                settings.TranslationModel = row.Id;
                break;
            case ModelKind.Llm:
                settings.LlmModel = row.Id;
                break;
            default:
                return;
        }

        if (targetEngine is { } selected)
        {
            settings.TranslationEngine = selected;
        }

        try
        {
            await _settingsService.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The model is downloaded either way; only the selection failed to stick.
            _logger.LogError(ex, "다운로드한 모델을 설정에 적용하지 못했습니다: {ModelId}", row.Id);
            var failure = string.Format(
                CultureInfo.CurrentCulture, Strings.ModelUseAfterDownloadFailedFormat, ex.Message);
            StatusMessage = failure;
            _dialogs.ShowError(failure, Strings.ModelUseAfterDownloadTitle);
            return;
        }

        _logger.LogInformation(
            "다운로드한 모델을 설정에 적용했습니다: {Kind} = {ModelId}", row.Kind, row.Id);

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            Strings.ModelUseAfterDownloadAppliedFormat,
            ModelSelectionValidator.DescribeKind(row.Kind),
            row.DisplayName);
    }

    private async Task RunDownloadAsync(
        ModelRowViewModel row,
        IProgress<ModelDownloadProgress> progress,
        CancellationToken token)
    {
        try
        {
            await _modelManager.DownloadAsync(row.Id, progress, token).ConfigureAwait(true);

            row.EndDownload(paused: false);
            row.IsInstalled = true;
            row.DownloadPercent = 100d;
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture, Strings.ModelDownloadCompletedFormat, row.DisplayName);

            await OfferToUseAsync(row).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 일시정지: the .part file stays on disk and 재개 picks it up with a Range request.
            row.EndDownload(paused: true);
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture, Strings.ModelDownloadPausedFormat, row.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "모델 다운로드에 실패했습니다: {ModelId}", row.Id);
            row.EndDownload(paused: false);
            var message = string.Format(
                CultureInfo.CurrentCulture, Strings.ModelDownloadFailedFormat, row.DisplayName, ex.Message);
            StatusMessage = message;
            _dialogs.ShowError(message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void PauseDownload()
    {
        var row = SelectedModel;
        if (row is null)
        {
            StatusMessage = Strings.ModelNotSelected;
            return;
        }

        if (!row.RequestPause())
        {
            StatusMessage = Strings.ModelNotDownloading;
        }
    }

    // -----------------------------------------------------------------------
    // Delete / verify / open folder
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanMutateInstalled))]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var row = SelectedModel;
        if (row is null)
        {
            StatusMessage = Strings.ModelNotSelected;
            return;
        }

        var confirm = string.Format(CultureInfo.CurrentCulture, Strings.ModelDeleteConfirmFormat, row.DisplayName);
        if (!_dialogs.Confirm(confirm))
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _modelManager.DeleteAsync(row.Id, cancellationToken).ConfigureAwait(true);

            row.IsInstalled = false;
            row.IsPausedDownload = false;
            row.DownloadPercent = 0d;
            row.ProgressDetail = string.Empty;

            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.ModelDeletedFormat, row.DisplayName);
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-delete; the manager cleans up its own temp state.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "모델 삭제에 실패했습니다: {ModelId}", row.Id);
            var message = string.Format(
                CultureInfo.CurrentCulture, Strings.ModelDeleteFailedFormat, row.DisplayName, ex.Message);
            StatusMessage = message;
            _dialogs.ShowError(message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateInstalled))]
    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var row = SelectedModel;
        if (row is null)
        {
            StatusMessage = Strings.ModelNotSelected;
            return;
        }

        try
        {
            IsBusy = true;
            row.IsVerifying = true;
            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.ModelVerifyingFormat, row.DisplayName);

            var ok = await _modelManager.VerifyAsync(row.Id, cancellationToken).ConfigureAwait(true);

            var message = string.Format(
                CultureInfo.CurrentCulture,
                ok ? Strings.ModelVerifyOkFormat : Strings.ModelVerifyFailedFormat,
                row.DisplayName);

            StatusMessage = message;

            if (ok)
            {
                _dialogs.ShowInformation(message);
            }
            else
            {
                _dialogs.ShowWarning(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-verification.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "모델 검증에 실패했습니다: {ModelId}", row.Id);
            var message = string.Format(
                CultureInfo.CurrentCulture, Strings.ModelVerifyFailedFormat, row.DisplayName);
            StatusMessage = message;
            _dialogs.ShowError(message);
        }
        finally
        {
            row.IsVerifying = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenModelFolder()
    {
        var row = SelectedModel;
        var path = row is null ? _paths.ModelsDirectory : _paths.ModelDirectory(row.Id);

        if (!_shell.RevealOrOpenParent(path) && !_shell.OpenFolder(_paths.ModelsDirectory))
        {
            StatusMessage = Strings.OpenFolderFailedMessage;
        }
    }

    /// <summary>Cancels every in-flight download; called when the window closes.</summary>
    public void CancelAllDownloads()
    {
        foreach (var row in Models)
        {
            row.RequestPause();
        }
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;

        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAllDownloads();

        foreach (var row in Models)
        {
            row.Dispose();
        }

        _activeDownloads.Clear();
    }
}
