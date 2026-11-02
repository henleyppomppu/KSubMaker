using KSubMaker.Domain.Settings;

namespace KSubMaker.App.Services;

/// <summary>
/// Opens the secondary windows. Keeps <c>MainViewModel</c> free of any reference to a concrete
/// <c>Window</c> type, which is what lets the whole navigation surface be swapped in a test.
/// </summary>
public interface IWindowService
{
    /// <summary>Modal 설정 dialog. True when the user saved.</summary>
    bool ShowSettings();

    /// <summary>Modal 모델 관리 dialog.</summary>
    void ShowModels();

    /// <summary>Modeless 로그 보기 window; brings the existing one forward when already open.</summary>
    void ShowLogs();

    /// <summary>Modeless 자막 보기 for one subtitle file. False when the file could not be read.</summary>
    bool ShowSubtitleEditor(string subtitlePath);

    /// <summary>
    /// Modal countdown before a 큐 완료 후 동작. Returns true to go ahead (the timer ran out, or the
    /// user pressed 지금 실행) and false when the user called it off.
    /// </summary>
    bool ConfirmPostQueueAction(PostQueueAction action);
}
