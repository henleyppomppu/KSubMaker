using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KSubMaker.App.Resources;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// The 모델 group beside 실행: which installed model recognises speech and which translates,
/// switchable without opening 설정. A pick is saved at once, like 완료 후; it applies from the next
/// 시작, because a run keeps the settings it started with.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>음성 인식 choices: 자동 plus the installed Whisper models.</summary>
    public ObservableCollection<Option<string>> WhisperChoices { get; } = [];

    /// <summary>번역 choices: the two 자동 entries plus installed NLLB and LLM models, in one list.</summary>
    public ObservableCollection<Option<string>> TranslationChoices { get; } = [];

    [ObservableProperty]
    private string? _selectedWhisperChoice;

    [ObservableProperty]
    private string? _selectedTranslationChoice;

    /// <summary>True while the lists are being rebuilt from the settings, so that is not a pick.</summary>
    private bool _syncingModelChoices;

    partial void OnSelectedWhisperChoiceChanged(string? value)
    {
        if (_syncingModelChoices || value is null)
        {
            return;
        }

        var settings = _settingsService.Current;
        if (string.Equals(settings.WhisperModel, value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        settings.WhisperModel = value;
        _ = SaveModelChoiceAsync(settings);
    }

    partial void OnSelectedTranslationChoiceChanged(string? value)
    {
        if (_syncingModelChoices || value is null)
        {
            return;
        }

        var settings = _settingsService.Current;
        if (string.Equals(TranslationChoice.Selected(settings), value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Moves the engine with the model: an LLM picked while the engine is still NLLB would
        // otherwise be a setting nothing reads.
        TranslationChoice.Apply(settings, value, _catalog);
        _ = SaveModelChoiceAsync(settings);
    }

    private async Task SaveModelChoiceAsync(AppSettings settings)
    {
        try
        {
            await _settingsService.SaveAsync(settings).ConfigureAwait(true);
            StatusMessage = IsQueueRunning ? Strings.ModelChoiceSavedNextRunMessage : Strings.ModelChoiceSavedMessage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "모델 선택을 저장하지 못했습니다.");
            _dialogs.ShowError(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ModelUseAfterDownloadFailedFormat, ex.Message));
            await RefreshModelChoicesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Rebuilds both lists from what is installed and selects what the settings say. Run at startup,
    /// when the settings change anywhere, and when a list is opened — a model downloaded in 모델 관리
    /// appears the next time the list drops down.
    /// </summary>
    public async Task RefreshModelChoicesAsync()
    {
        HashSet<string> installed;
        try
        {
            var statuses = await _models.GetStatusAsync().ConfigureAwait(true);
            installed = statuses
                .Where(s => s.Installation.Installed)
                .Select(s => s.Descriptor.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "모델 설치 상태를 확인하지 못했습니다.");
            return;
        }

        var settings = _settingsService.Current;
        var whisper = settings.WhisperModel;
        var translation = TranslationChoice.Selected(settings);

        var whisperOptions = new List<Option<string>> { new(ModelSelectionValidator.AutoModelId, Strings.ModelAutoOption) };
        whisperOptions.AddRange(_catalog.OfKind(ModelKind.Whisper)
            .Where(m => installed.Contains(m.Id) || m.Id.Equals(whisper, StringComparison.OrdinalIgnoreCase))
            .Select(m => new Option<string>(m.Id, ShortName(m.DisplayName))));

        var translationOptions = new List<Option<string>>
        {
            new(TranslationChoice.AutoTranslationId, Strings.ModelAutoOption),
            new(TranslationChoice.AutoLlmId, Strings.LlmAutoOption)
        };
        translationOptions.AddRange(_catalog.OfKind(ModelKind.Translation)
            .Concat(_catalog.OfKind(ModelKind.Llm))
            .Where(m => installed.Contains(m.Id) || m.Id.Equals(translation, StringComparison.OrdinalIgnoreCase))
            .Select(m => new Option<string>(m.Id, ShortName(m.DisplayName))));

        _syncingModelChoices = true;
        try
        {
            Replace(WhisperChoices, whisperOptions);
            Replace(TranslationChoices, translationOptions);

            SelectedWhisperChoice = whisperOptions
                .FirstOrDefault(o => o.Value.Equals(whisper, StringComparison.OrdinalIgnoreCase))?.Value
                ?? ModelSelectionValidator.AutoModelId;

            // The diagnostic engine is not offered here; its id simply finds no entry.
            SelectedTranslationChoice = translationOptions
                .FirstOrDefault(o => o.Value.Equals(translation, StringComparison.OrdinalIgnoreCase))?.Value;
        }
        finally
        {
            _syncingModelChoices = false;
        }
    }

    private static void Replace(ObservableCollection<Option<string>> target, IReadOnlyList<Option<string>> items)
    {
        if (target.Select(o => (o.Value, o.Display)).SequenceEqual(items.Select(o => (o.Value, o.Display))))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>"Whisper large-v3-turbo (CTranslate2)" → "Whisper large-v3-turbo": the box is narrow.</summary>
    private static string ShortName(string displayName)
    {
        var cut = displayName.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? displayName[..cut] : displayName;
    }
}
