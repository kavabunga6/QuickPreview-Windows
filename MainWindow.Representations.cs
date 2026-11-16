using System.Windows;
using System.Windows.Controls;

namespace QuickPreview;

public partial class MainWindow
{
    private UIElement? _visualPreviewView;
    private bool _hasSourcePreview;
    private bool _sourceSelected;
    private bool _sourceLoaded;
    private bool _updatingPreviewTabs;
    private int _representationGeneration;
    private Task? _sourceLoadTask;

    private async Task ConfigureRepresentationsAsync(string path, CancellationToken token)
    {
        if (!SourcePreviewService.IsCandidate(path)) return;
        var generation = _representationGeneration;
        var available = await SourcePreviewService.CanShowAsync(path, token);
        token.ThrowIfCancellationRequested();
        if (generation != _representationGeneration || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
        _hasSourcePreview = available;
        RepresentationBar.IsEnabled = available;
    }

    private void ResetRepresentations()
    {
        _representationGeneration++;
        _hasSourcePreview = false;
        _sourceSelected = false;
        _sourceLoaded = false;
        _sourceLoadTask = null;
        _visualPreviewView = null;
        _updatingPreviewTabs = true;
        PreviewTabs.SelectedIndex = 0;
        _updatingPreviewTabs = false;
        RepresentationBar.IsEnabled = false;
        SourceView.Visibility = Visibility.Collapsed;
        SourcePreviewText.Clear();
        SourceStatusText.Text = string.Empty;
    }

    private void PreviewTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingPreviewTabs || !ReferenceEquals(e.Source, PreviewTabs)) return;
        _sourceSelected = _hasSourcePreview && PreviewTabs.SelectedIndex == 1;
        ApplyRepresentationVisibility();
        if (_sourceSelected && !_sourceLoaded && _sourceLoadTask is null &&
            _currentPath is { } path && _previewCancellation is { } cancellation)
        {
            var loading = LoadSourcePreviewAsync(path, cancellation.Token, _representationGeneration);
            _sourceLoadTask = loading.IsCompleted ? null : loading;
        }
    }

    private async Task LoadSourcePreviewAsync(string path, CancellationToken token, int generation)
    {
        SourceStatusText.Text = "Загружаем исходный текст…";
        try
        {
            var source = await SourcePreviewService.LoadAsync(path, token);
            token.ThrowIfCancellationRequested();
            if (generation != _representationGeneration) return;
            SourcePreviewText.Text = source.Text;
            SourcePreviewText.ScrollToHome();
            SourceStatusText.Text = source.Summary;
            _sourceLoaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (generation == _representationGeneration && !token.IsCancellationRequested)
                SourceStatusText.Text = $"Не удалось прочитать исходный текст. {exception.Message}";
        }
        finally
        {
            if (generation == _representationGeneration) _sourceLoadTask = null;
        }
    }

    private void ApplyRepresentationVisibility()
    {
        var visible = _sourceSelected ? SourceView : _visualPreviewView ?? LoadingView;
        foreach (var view in new[] { LoadingView, ImageView, TextView, SourceView, PdfView, NativePdfView, ModelView,
                     TableView, SpreadsheetView, FontView, ArchiveView, VideoView, AudioView, InfoView, ErrorView })
            view.Visibility = ReferenceEquals(view, visible) ? Visibility.Visible : Visibility.Collapsed;
        QueueBrowserResourceUpdate();
    }
}
