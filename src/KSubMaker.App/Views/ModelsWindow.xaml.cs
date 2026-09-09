using System.Windows;
using System.Windows.Data;
using KSubMaker.App.ViewModels;
using KSubMaker.Domain.Settings;

namespace KSubMaker.App.Views;

/// <summary>
/// 모델 관리 dialog. Loads the catalog once the window is up and cancels any in-flight download when
/// it closes, so a half-finished transfer leaves only a resumable <c>.part</c> file behind.
///
/// Splits the model list into an 음성 인식 tab and a 번역 tab rather than one grouped list: the two
/// steps have nothing in common but "a model file to manage", and a shared list kept showing 번역
/// twice (once as the tab-level concept, once as NLLB's own group name) when they were merged.
/// </summary>
public partial class ModelsWindow : Window
{
    private readonly ModelsViewModel _viewModel;

    public ModelsWindow(ModelsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // CollectionViewSource.Filter has no XAML surface, so the predicate is wired here once.
        ((CollectionViewSource)Resources["RecognitionModels"]).Filter +=
            (_, e) => e.Accepted = e.Item is ModelRowViewModel { Kind: ModelKind.Whisper };

        ((CollectionViewSource)Resources["TranslationModels"]).Filter +=
            (_, e) => e.Accepted = e.Item is ModelRowViewModel { Kind: not ModelKind.Whisper };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// <see cref="async void"/> is forced by the event signature; the view model reports its own
    /// failures, and the catch here is the backstop that keeps one from escaping to the dispatcher.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Already logged and surfaced by the view model.
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.Dispose();
    }
}
