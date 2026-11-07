using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// 자막 보기: a finished subtitle as a table — number, start, end, text. Only the text can be
/// changed. Timings come from speech recognition and nothing after it moves them (the translation
/// never does either), so the viewer keeps that rule and a slip of the keyboard cannot break the file.
/// Saving rewrites the same file through the same atomic writer the pipeline uses.
/// </summary>
public sealed partial class SubtitleEditorViewModel : ObservableObject
{
    private readonly ISubtitleWriter _writer;
    private readonly IShellService _shell;
    private readonly ILogger _logger;

    public SubtitleEditorViewModel(string path, ISubtitleWriter writer, IShellService shell, ILogger logger)
    {
        Path = path;
        _writer = writer;
        _shell = shell;
        _logger = logger;
    }

    public string Path { get; }

    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.SubtitleEditorTitleFormat, System.IO.Path.GetFileName(Path));

    public ObservableCollection<SubtitleCueRowViewModel> Cues { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Reads the file. False (with the reason in <see cref="StatusMessage"/>) when it cannot.</summary>
    public bool Load()
    {
        try
        {
            var cues = SrtParser.Parse(File.ReadAllText(Path));
            Cues.Clear();
            foreach (var cue in cues)
            {
                Cues.Add(new SubtitleCueRowViewModel(cue, () => IsDirty = true));
            }

            IsDirty = false;
            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.SubtitleLoadedFormat, Cues.Count);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "자막을 읽지 못했습니다: {Path}", Path);
            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.SubtitleLoadFailedFormat, ex.Message);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    public async Task<bool> SaveAsync()
    {
        var cues = Cues.Select(c => c.ToCue()).ToArray();

        try
        {
            await _writer.WriteAsync(cues, Path, OutputConflictPolicy.Overwrite, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "자막을 저장하지 못했습니다: {Path}", Path);
            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.SubtitleSaveFailedFormat, ex.Message);
            return false;
        }

        IsDirty = false;
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture, Strings.SubtitleSavedFormat, cues.Count(c => c.Lines.Count > 0));
        _logger.LogInformation("자막을 고쳐 저장했습니다: {Path}", Path);
        return true;
    }

    /// <summary>The same file in whatever Windows opens .srt with.</summary>
    [RelayCommand]
    private void OpenExternally()
    {
        if (!_shell.OpenFile(Path))
        {
            StatusMessage = Strings.SubtitleFileNotFoundMessage;
        }
    }
}

/// <summary>One cue: timings fixed, text editable.</summary>
public sealed partial class SubtitleCueRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public SubtitleCueRowViewModel(SubtitleCue cue, Action changed)
    {
        Index = cue.Index;
        Start = cue.Start;
        End = cue.End;
        _text = cue.Text;
        _changed = changed;
    }

    public int Index { get; }

    public double Start { get; }

    public double End { get; }

    public string StartText => SrtFormatter.FormatTimestamp(Start);

    public string EndText => SrtFormatter.FormatTimestamp(End);

    [ObservableProperty]
    private string _text;

    partial void OnTextChanged(string value) => _changed();

    /// <summary>Lines as typed; a cue left empty is dropped by the writer.</summary>
    public SubtitleCue ToCue() => new()
    {
        Index = Index,
        Start = Start,
        End = End,
        Lines = Text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToArray()
    };
}
