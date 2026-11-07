using System.ComponentModel;
using System.Windows;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.App.ViewModels;

namespace KSubMaker.App.Views;

/// <summary>
/// 자막 보기. The only view logic is the close guard: closing with unsaved changes offers to save,
/// and saving is asynchronous while <see cref="Window.Closing"/> is not.
/// </summary>
public partial class SubtitleEditorWindow : Window
{
    private readonly SubtitleEditorViewModel _viewModel;
    private readonly IDialogService _dialogs;
    private bool _closeConfirmed;

    public SubtitleEditorWindow(SubtitleEditorViewModel viewModel, IDialogService dialogs)
    {
        _viewModel = viewModel;
        _dialogs = dialogs;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Esc closes, as in a dialog — through <see cref="OnClosing"/>, so unsaved edits are asked about.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || !_viewModel.IsDirty)
        {
            return;
        }

        // Yes saves and closes; No closes without saving.
        if (!_dialogs.Confirm(Strings.SubtitleUnsavedConfirm, Title))
        {
            return;
        }

        e.Cancel = true;
        if (await _viewModel.SaveAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
