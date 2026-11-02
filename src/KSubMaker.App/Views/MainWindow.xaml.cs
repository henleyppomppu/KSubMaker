using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KSubMaker.App.ViewModels;

namespace KSubMaker.App.Views;

/// <summary>
/// Shell window. The logic here is closing coordination — genuinely a view concern, because the
/// queue and the Python worker have to be stopped asynchronously and WPF's
/// <see cref="Window.Closing"/> is synchronous — plus one input quirk that cannot be expressed as a
/// binding (<see cref="OnRowRightClick"/>).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    private bool _shutdownStarted;
    private bool _readyToClose;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Makes a right-click select the row under the cursor.
    ///
    /// WPF's <c>DataGrid</c> does not do this on its own, so without it the context menu acts on
    /// whichever row was last left-clicked — which is the wrong file, silently. Not expressible as a
    /// binding: it is an input-routing detail of the control.
    /// </summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row && !row.IsSelected)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    /// <summary>
    /// Shows the copy cursor for a file drop and blocks everything else. Without this WPF shows
    /// the "no entry" cursor even for drops the grid accepts, because a read-only DataGrid rejects
    /// drag events by default.
    /// </summary>
    private void OnQueueDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Hands dropped files and folders to the view model. <c>async void</c> is forced by the event
    /// signature; the view model wraps its own work in try/catch, so the only thing to guard here
    /// is the payload extraction.
    /// </summary>
    private void OnOpenDiscord(object sender, RoutedEventArgs e) => KSubMaker.App.Services.AppLinks.Open(KSubMaker.App.Services.AppLinks.Discord);

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 작업 → 재생목록에 넣기 ▸, filled each time it opens: the playlists, then 새 재생목록에 넣기….
    /// A bound ItemsSource cannot mix a list with a fixed item, and a submenu's popup is outside the
    /// window's tree, where a RelativeSource binding back to the view model does not reliably resolve.
    /// </summary>
    private void OnPlaylistSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || !ReferenceEquals(e.OriginalSource, menu))
        {
            return;
        }

        menu.Items.Clear();
        foreach (var playlist in _viewModel.Playlists)
        {
            menu.Items.Add(new MenuItem
            {
                Header = playlist.Name,
                Command = _viewModel.AddToPlaylistCommand,
                CommandParameter = playlist
            });
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem
        {
            Header = KSubMaker.App.Resources.Strings.AddToNewPlaylistMenuItem,
            Command = _viewModel.AddToNewPlaylistCommand
        });
    }

    /// <summary>
    /// Scrolls the selected folder into view. Selection usually comes from the view model (the tree
    /// following a folder picked elsewhere), and a bound IsSelected does not scroll on its own.
    /// </summary>
    private void OnFolderTreeItemSelected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item)
        {
            item.BringIntoView();
        }
    }

    private async void OnQueueDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        await _viewModel.AddDroppedPathsAsync(paths);
    }

    /// <summary>
    /// Cancels the first close, runs the asynchronous teardown, then closes for real.
    ///
    /// <see cref="async void"/> is forced by the event signature; every path is inside the try/catch
    /// and the window closes even when teardown fails, so the user can never be trapped in a window
    /// that refuses to shut.
    ///
    /// <para>The window is hidden the instant the first close is requested. Teardown is asynchronous
    /// — stopping the queue and shutting the Python worker down takes a second or three when a worker
    /// is still alive from an earlier job — and WPF's <see cref="Window.Closing"/> is synchronous, so
    /// without this the X appears dead until teardown finishes and a user reasonably concludes it
    /// needs a second click. Hiding first makes the click land immediately; the process exits on its
    /// own once cleanup is done.</para>
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_readyToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;

        // ShutdownMode is OnMainWindowClose, which keys on the window being *closed*, not hidden, so
        // this does not start a second teardown behind our back.
        Hide();

        try
        {
            await _viewModel.ShutdownAsync().ConfigureAwait(true);

            if (System.Windows.Application.Current is App app)
            {
                await app.ShutdownServicesAsync().ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // ShutdownAsync already logged; a failure here must not block the close.
        }
        finally
        {
            _viewModel.Dispose();
            _readyToClose = true;

            // Teardown is asynchronous, so the window can already be gone by the time we get here.
            // ShutdownMode is OnMainWindowClose, and every application-shutdown path (a Windows
            // session-end, a second close request) closes windows with ignoreCancel=true — which
            // overrides the e.Cancel we set above and tears this window down while our first,
            // cancelled close was still awaiting. Calling Close() on an already-closing window
            // throws ("창을 닫는 중에는 ... Close ... 호출할 수 없습니다"). If the window is already on
            // its way out, the close we were about to request has effectively already happened.
            try
            {
                Close();
            }
            catch (InvalidOperationException)
            {
                // Window was already closing/closed by a racing application shutdown; nothing to do.
            }
        }
    }
}
