using System.Windows;
using KSubMaker.App.Views;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace KSubMaker.App.Services;

/// <summary>
/// Resolves each window (and therefore its view model and the whole dependency graph behind it) from
/// the container at the moment it is shown, so a closed window and everything it owned is collectable.
/// </summary>
public sealed class WindowService(IServiceProvider services) : IWindowService
{
    private readonly IServiceProvider _services = services;

    /// <summary>
    /// The log window is modeless, so without this a user hammering "로그 보기" would end up with a
    /// stack of identical windows each polling the same file every two seconds.
    /// </summary>
    private LogWindow? _logWindow;

    public bool ShowSettings()
    {
        var window = _services.GetRequiredService<SettingsWindow>();
        window.Owner = ActiveOwner();
        return window.ShowDialog() == true;
    }

    public bool ShowSubtitleEditor(string subtitlePath)
    {
        var viewModel = new ViewModels.SubtitleEditorViewModel(
            subtitlePath,
            _services.GetRequiredService<KSubMaker.Application.Abstractions.ISubtitleWriter>(),
            _services.GetRequiredService<IShellService>(),
            _services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ViewModels.SubtitleEditorViewModel>>());

        if (!viewModel.Load())
        {
            _services.GetRequiredService<IDialogService>().ShowError(viewModel.StatusMessage);
            return false;
        }

        var window = new SubtitleEditorWindow(viewModel, _services.GetRequiredService<IDialogService>())
        {
            Owner = ActiveOwner()
        };
        window.Show();
        return true;
    }

    public void ShowModels()
    {
        var window = _services.GetRequiredService<ModelsWindow>();
        window.Owner = ActiveOwner();
        window.ShowDialog();
    }

    public void ShowLogs()
    {
        if (_logWindow is not null)
        {
            if (_logWindow.WindowState == WindowState.Minimized)
            {
                _logWindow.WindowState = WindowState.Normal;
            }

            _logWindow.Activate();
            return;
        }

        var window = _services.GetRequiredService<LogWindow>();
        window.Owner = ActiveOwner();
        window.Closed += OnLogWindowClosed;
        _logWindow = window;
        window.Show();
    }

    public bool ConfirmPostQueueAction(PostQueueAction action)
    {
        // Created directly: it takes a runtime string and owns nothing that needs the container.
        var window = new PostQueueActionWindow(DisplayText.PostQueueActionName(action))
        {
            Owner = ActiveOwner()
        };

        return window.ShowDialog() == true;
    }

    private void OnLogWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Closed -= OnLogWindowClosed;
        }

        _logWindow = null;
    }

    private static Window? ActiveOwner()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return null;
        }

        var main = application.MainWindow;
        return main is not null && main.IsLoaded ? main : null;
    }
}
