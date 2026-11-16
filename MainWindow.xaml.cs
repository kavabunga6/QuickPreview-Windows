using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;
using WpfButton = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;

namespace QuickPreview;

public partial class MainWindow : Window
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoActivate = 0x0010;
    private const int SwRestore = 9;

    private static readonly double[] ImageZoomSteps =
    {
        0.05, 0.10, 0.125, 0.25, 0.33, 0.50, 0.67, 0.75, 1.0, 1.25, 1.50, 2.0, 3.0, 4.0, 6.0, 8.0
    };

    private readonly DispatcherTimer _mediaTimer;
    private readonly MediaPlayer _audioPlayer = new();
    private readonly List<string> _navigationPaths = new();
    private string? _currentPath;
    private bool _currentPathIsDirectory;
    private int _navigationIndex = -1;
    private Slider? _activeSlider;
    private TextBlock? _activeTimeText;
    private WpfButton? _activePlayPauseButton;
    private bool _mediaPlaying;
    private bool _userSeeking;
    private bool _usingAudioPlayer;
    private bool _hasActiveMedia;
    private bool _isMuted;
    private bool _updatingVolumeControls;
    private bool _pdfWebViewConfigured;
    private Task? _pdfInitializationTask;
    private Task? _modelInitializationTask;
    private Task<CoreWebView2Environment>? _webEnvironmentTask;
    private ModelPreviewSession? _modelSession;
    private NativePdfPreview? _nativePdf;
    private uint _pdfPageIndex;
    private CancellationTokenSource? _previewCancellation;
    private Task _navigationTail = Task.CompletedTask;
    private int _navigationEpoch;
    private double _volume = 0.8;
    private double _imageNaturalWidth;
    private double _imageNaturalHeight;
    private double _imageZoom = 1;
    private bool _imageFitMode = true;
    private bool _imagePanning;
    private System.Windows.Point _imagePanStart;
    private double _imagePanHorizontalOffset;
    private double _imagePanVerticalOffset;

    public MainWindow()
    {
        InitializeComponent();
        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mediaTimer.Tick += (_, _) => UpdateMediaPosition();
        _audioPlayer.MediaOpened += (_, _) => HandleMediaOpened();
        _audioPlayer.MediaEnded += (_, _) => HandleMediaEnded();
        _audioPlayer.MediaFailed += (_, args) => HandleMediaFailed(args.ErrorException);
        AudioVolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        VideoVolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        SyncVolumeControls();
        InitializePreviewResourcePolicy();
        InitializeOriginLifetimeMonitoring();
    }

    public void ShowPreview(string path, bool activateWindow = true)
    {
        _navigationEpoch++;
        _ = ShowPreviewAsync(path, rebuildNavigation: true, activateWindow: activateWindow);
    }

    internal void UpdatePreviewFromExplorer(string path)
    {
        if (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
        _ = ShowPreviewAsync(path, rebuildNavigation: false, activateWindow: false, preserveWindow: IsVisible);
    }

    private async Task ShowPreviewAsync(string path, bool rebuildNavigation, bool activateWindow = true,
        bool preserveWindow = false, bool preferSource = false)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var token = _previewCancellation.Token;

        _currentPath = path;
        _currentPathIsDirectory = false;
        _previewLoading = true;
        try
        {
            ResetViews();
            if (!preserveWindow)
            {
                SetDefaultWindowSize();
                BringPreviewToFront(activateWindow);
            }
            else
            {
                KeepAboveOriginExplorer();
            }
            FileNameText.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            FileMetaText.Text = "Читаем свойства…";
            _ = RevealLoadingIndicatorAsync(token);
            // Coalesce key repeats before starting parsers and browser navigations.
            // The header follows selection immediately; old content stays hidden.
            if (preserveWindow)
                await Task.Delay(80, token);
            var pathInfo = await FolderPreviewService.ReadPathInfoAsync(path, token);
            token.ThrowIfCancellationRequested();
            if (pathInfo is null) throw new IOException("Свойства файла временно недоступны. Откройте его в Проводнике.");
            if (!pathInfo.Exists) throw new FileNotFoundException("Файл больше не существует.", path);
            _currentPathIsDirectory = pathInfo.IsDirectory;
            PopulateHeader(path, pathInfo);
            if (rebuildNavigation && !ExplorerSelectionService.HasOriginWindow)
                await BuildNavigationAsync(path, token);
            await ConfigureRepresentationsAsync(path, token);
            token.ThrowIfCancellationRequested();
            if (preferSource && _hasSourcePreview) PreviewTabs.SelectedIndex = 1;
            if (WindowState == WindowState.Minimized)
            {
                _reloadAfterMinimize = path;
                _restoreSourceAfterMinimize = _sourceSelected;
                _previewCancellation.Cancel();
                return;
            }
            await LoadPreviewAsync(path, pathInfo, token);
            token.ThrowIfCancellationRequested();
            KeepAboveOriginExplorer();
        }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            if (exception is not OperationCanceledException)
            {
                ShowError($"Не удалось открыть предпросмотр.\n{exception.Message}");
                KeepAboveOriginExplorer();
            }
        }
        finally
        {
            if (_previewCancellation?.Token == token) _previewLoading = false;
            QueueBrowserResourceUpdate();
        }
    }

    public void HidePreview() => HidePreviewCore(restoreFocus: true);

    internal void HidePreviewWithoutRestoringOriginFocus() => HidePreviewCore(restoreFocus: false);

    private void HidePreviewCore(bool restoreFocus)
    {
        var restoreExplorerFocus = restoreFocus && IsVisible && ExplorerSelectionService.HasOriginWindow;
        _navigationEpoch++;
        ResetBrowserLoadingState();
        _previewLoading = false;
        Hide();
        _previewCancellation?.Cancel();
        ReleasePdfDocument();
        ReleaseSpreadsheetDocument();
        ResetRepresentations();
        DisposeModelPreview();
        StopMedia();
        PreviewImage.Source = null;
        InfoThumbnail.Source = null;
        _nativePdf = null;
        NativePdfImage.Source = null;
        ArchiveEntriesList.ItemsSource = null;
        TableRowsList.ItemsSource = null;
        _currentPath = null;

        if (restoreExplorerFocus)
            ExplorerSelectionService.RestoreOriginFocus();
        ExplorerSelectionService.ForgetOriginWindow();
    }

    private void BringPreviewToFront(bool activateWindow)
    {
        if (!IsVisible)
        {
            ShowActivated = activateWindow;
            Show();
        }

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        if (!activateWindow)
        {
            // HWND_TOP alone can leave a newly shown background-process window
            // behind Explorer. Return to the normal band in the same UI turn.
            _ = SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
            _ = SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
            return;
        }

        _ = ShowWindow(handle, SwRestore);

        // A background tray process is not always allowed to activate itself. Moving the
        // preview through the topmost band makes it visible, then immediately returns it
        // to the normal window band so it does not stay above every other application.
        _ = SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
        _ = SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
        _ = BringWindowToTop(handle);
        _ = SetForegroundWindow(handle);
        Activate();
        Focus();
    }

    internal bool KeepAboveOriginExplorer()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
            return false;
        var foreground = GetForegroundWindow();
        if (!ExplorerSelectionService.IsOriginWindow(foreground) || !ExplorerSelectionService.IsOriginTabActive)
            return false;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return false;

        if (IsWindowAbove(handle, foreground)) return true;
        if (GetForegroundWindow() != foreground) return false;
        _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        if (IsWindowAbove(handle, foreground)) return true;
        if (GetForegroundWindow() != foreground) return false;

        // Background-process foreground policy can ignore HWND_TOP. A transient
        // trip through the topmost band also handles that case without activation.
        _ = SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        _ = SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        return IsWindowAbove(handle, foreground);
    }

    private static bool IsWindowAbove(IntPtr window, IntPtr other)
    {
        const uint previousWindow = 3; // GW_HWNDPREV: immediately above this window
        var current = GetWindow(other, previousWindow);
        var visited = new HashSet<IntPtr>();
        for (var count = 0; current != IntPtr.Zero && count < 512 && visited.Add(current); count++)
        {
            if (current == window) return true;
            current = GetWindow(current, previousWindow);
        }
        return false;
    }

    private async Task RevealLoadingIndicatorAsync(CancellationToken token)
    {
        try
        {
            // Fast local previews should not flash a loading label between files.
            await Task.Delay(200, token);
            token.ThrowIfCancellationRequested();
            LoadingView.Opacity = 1;
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadPreviewAsync(string path, PreviewPathInfo pathInfo, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pathInfo.IsDirectory)
        {
            await LoadFolderPreviewAsync(path, token);
            return;
        }

        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await LoadPdfAsync(path, token);
            return;
        }

        if (ModelPreviewService.Supports(path))
        {
            await LoadModelAsync(path, token);
            return;
        }
        if (DrawingPreviewService.Supports(path))
        {
            await LoadRenderedImageAsync(path, drawing: true, token);
            return;
        }
        if (SvgPreviewService.Supports(path))
        {
            var image = await SvgPreviewService.LoadAsync(path, token);
            token.ThrowIfCancellationRequested();
            ShowBitmap(image, "SVG · векторное изображение");
            return;
        }
        if (FontPreviewService.Supports(path))
        {
            var font = FontPreviewService.Load(path);
            FontNameText.Text = font.Name;
            FontDetailsText.Text = font.Details;
            FontSamplesPanel.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, font.Family);
            FileMetaText.Text += "  •  образец шрифта";
            ShowOnly(FontView);
            return;
        }
        if (DocumentPreviewService.Supports(path))
        {
            var document = await DocumentPreviewService.LoadAsync(path, token);
            token.ThrowIfCancellationRequested();
            ShowTextContent(document.Text, document.Summary, document.IsMonospaced);
            return;
        }
        if (TabularPreviewService.Supports(path))
        {
            await LoadTableAsync(path, token);
            return;
        }
        if (TechnicalPreviewService.Supports(path))
        {
            await LoadTechnicalAsync(path, token);
            return;
        }

        if (ArchivePreviewService.Supports(path))
        {
            await LoadArchiveAsync(path, token);
            return;
        }

        if (SpreadsheetPreviewService.Supports(path))
        {
            await LoadSpreadsheetAsync(path, token);
            return;
        }

        if (OfficePreviewService.Supports(path))
        {
            await LoadOfficeAsync(path, token);
            return;
        }

        if (ExtendedImagePreviewService.Supports(path))
        {
            await LoadRenderedImageAsync(path, drawing: false, token);
            return;
        }

        if (FormatCatalog.ImageExtensions.Contains(extension))
        {
            await LoadNativeImageAsync(path, token);
            return;
        }

        // .ts is also the TypeScript source extension; text gets priority over media.
        var ambiguousText = extension.ToLowerInvariant() is ".ts" or ".mts" or ".sub" or ".nc" or ".out";
        if (FormatCatalog.IsKnownText(path) && (!ambiguousText || await LooksLikeTextAsync(path, pathInfo.Length, token)))
        {
            token.ThrowIfCancellationRequested();
            await LoadTextAsync(path, pathInfo.Length, token);
            return;
        }

        if (FormatCatalog.AudioExtensions.Contains(extension))
        {
            await LoadAudioAsync(path, pathInfo.Length, token);
            return;
        }

        if (FormatCatalog.VideoExtensions.Contains(extension))
        {
            LoadVideo(path);
            return;
        }

        if (await LooksLikeTextAsync(path, pathInfo.Length, token))
        {
            token.ThrowIfCancellationRequested();
            await LoadTextAsync(path, pathInfo.Length, token);
            return;
        }

        token.ThrowIfCancellationRequested();
        await LoadSystemPreviewAsync(path, token);
    }

    private async Task LoadAudioAsync(string path, long length, CancellationToken token)
    {
        ResetAudioArtwork();
        ConfigureMedia(usingAudioPlayer: true, AudioPositionSlider, AudioTimeText, AudioPlayPauseButton);
        AudioTitleText.Text = Path.GetFileNameWithoutExtension(path);
        AudioFormatText.Text = $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}  •  {FormatFileSize(length)}";
        AudioDurationText.Text = "0:00";
        AudioDurationHeroText.Text = "—:—";
        ApplyVolumeState();
        SyncVolumeControls();
        SetAudioWindowSize();
        ShowOnly(AudioView);
        _audioPlayer.Open(new Uri(path, UriKind.Absolute));
        _audioPlayer.Play();
        StartMediaTimer();

        var artwork = await Task.Run(() => ShellThumbnailService.TryGetThumbnail(path, 512, thumbnailOnly: true));
        token.ThrowIfCancellationRequested();
        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase) || artwork is null)
            return;

        AudioArtworkBorder.Background = new ImageBrush(artwork)
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
        AudioFallbackIcon.Visibility = Visibility.Collapsed;
    }

    private void LoadVideo(string path)
    {
        ConfigureMedia(usingAudioPlayer: false, VideoPositionSlider, VideoTimeText, VideoPlayPauseButton);
        ApplyVolumeState();
        SyncVolumeControls();
        ShowOnly(VideoView);
        VideoMedia.Source = new Uri(path, UriKind.Absolute);
        VideoMedia.Play();
        StartMediaTimer();
    }

    private void ConfigureMedia(bool usingAudioPlayer, Slider slider, TextBlock timeText, WpfButton playPauseButton)
    {
        _usingAudioPlayer = usingAudioPlayer;
        _hasActiveMedia = true;
        _activeSlider = slider;
        _activeTimeText = timeText;
        _activePlayPauseButton = playPauseButton;
        slider.Minimum = 0;
        slider.Maximum = 1;
        slider.Value = 0;
        timeText.Text = usingAudioPlayer ? "0:00" : "0:00 / 0:00";
        playPauseButton.Content = "\uE769";
    }

    private void StartMediaTimer()
    {
        _mediaPlaying = true;
        _mediaTimer.Start();
    }

    private async Task LoadTextAsync(string path, long length, CancellationToken token)
    {
        const int maximumBytes = 2 * 1024 * 1024;
        var buffer = await BoundedFileReader.ReadAsync(path, maximumBytes, token);
        var prepared = await Task.Run(() => PrepareText(path, buffer, length, token), token);
        token.ThrowIfCancellationRequested();
        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
        PreviewText.Text = prepared.Text;
        PreviewText.ScrollToHome();
        if (!string.IsNullOrWhiteSpace(prepared.Status)) FileMetaText.Text += $"  •  {prepared.Status}";
        ShowOnly(TextView);
    }

    private static (string Text, string Status) PrepareText(string path, byte[] buffer, long length, CancellationToken token)
    {
        const int maximumBytes = 2 * 1024 * 1024;
        token.ThrowIfCancellationRequested();
        var text = DecodeText(buffer);
        var status = string.Empty;
        if (length <= maximumBytes)
        {
            try
            {
                var extension = Path.GetExtension(path);
                if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    text = FormatJson(text);
                    status = "форматировано";
                }
                else if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    text = FormatXml(text);
                    status = "форматировано";
                }
            }
            catch (JsonException exception)
            {
                var line = exception.LineNumber is null ? string.Empty : $", строка {exception.LineNumber.Value + 1}";
                status = $"исходный текст — ошибка JSON{line}";
            }
            catch (XmlException exception)
            {
                var line = exception.LineNumber > 0 ? $", строка {exception.LineNumber}" : string.Empty;
                status = $"исходный текст — ошибка XML{line}";
            }
        }
        if (text.Length > maximumBytes)
        {
            text = text[..maximumBytes];
            status += " · форматированный текст ограничен";
        }
        if (length > maximumBytes)
            text += $"\n\n—— Превью ограничено первыми {FormatFileSize(maximumBytes)} из {FormatFileSize(length)} ——";
        token.ThrowIfCancellationRequested();
        return (text, status);
    }

    private async Task LoadPdfAsync(string path, CancellationToken token)
    {
        _pdfLoading = true;
        try
        {
            _pdfInitializationTask ??= InitializePdfWebViewAsync();
            await _pdfInitializationTask.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            ResumeBrowser(PdfWebView);
            if (!_pdfWebViewConfigured)
            {
                PdfWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                PdfWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                PdfWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                PdfWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
                PdfWebView.CoreWebView2.Settings.IsZoomControlEnabled = true;
                _pdfWebViewConfigured = true;
            }

            await NavigatePdfAsync(new Uri(path, UriKind.Absolute).AbsoluteUri, token);
            token.ThrowIfCancellationRequested();
            _pdfLoading = false;
            ShowOnly(PdfView);
            FileMetaText.Text += "  •  встроенный PDF-просмотр";
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or InvalidOperationException or
                                         COMException or DllNotFoundException or BadImageFormatException or FileNotFoundException or TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            PdfWebView.CoreWebView2?.Stop();
            var document = await NativePdfPreview.OpenAsync(path, token);
            token.ThrowIfCancellationRequested();
            _nativePdf = document;
            _pdfPageIndex = 0;
            await RenderPdfPageAsync(token);
            token.ThrowIfCancellationRequested();
            FileMetaText.Text += "  •  PDF-просмотр Windows";
        }
        finally
        {
            if (_previewCancellation?.Token == token) _pdfLoading = false;
            QueueBrowserResourceUpdate();
        }
    }

    private async Task NavigatePdfAsync(string uri, CancellationToken token)
    {
        var core = PdfWebView.CoreWebView2;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            // A cancelled A request must not cancel a newer A navigation.
            if (token.IsCancellationRequested || !string.Equals(args.Uri, uri, StringComparison.OrdinalIgnoreCase)) return;
            navigationId = args.NavigationId;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId != navigationId) return;
            if (args.IsSuccess) completed.TrySetResult();
            else completed.TrySetException(new InvalidOperationException("Не удалось загрузить PDF-просмотр."));
        }
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        try
        {
            token.ThrowIfCancellationRequested();
            core.Navigate(uri);
            // Keep the previous PDF concealed until THIS navigation has finished.
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Completed;
        }
    }

    private async Task InitializePdfWebViewAsync()
    {
        await PdfWebView.EnsureCoreWebView2Async(await GetWebEnvironmentAsync());
        ConfigureBrowserResources(PdfWebView.CoreWebView2);
    }

    private Task<CoreWebView2Environment> GetWebEnvironmentAsync()
    {
        if (_webEnvironmentTask is not null) return _webEnvironmentTask;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll")))
            CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
        var userDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickPreview", "WebView2");
        return _webEnvironmentTask = CoreWebView2Environment.CreateAsync(userDataFolder: userDataPath);
    }

    private async Task InitializeModelWebViewAsync()
    {
        await ModelWebView.EnsureCoreWebView2Async(await GetWebEnvironmentAsync());
        ConfigureBrowserResources(ModelWebView.CoreWebView2);
    }

    private async Task LoadModelAsync(string path, CancellationToken token)
    {
        _modelLoading = true;
        ModelPreviewSession? session = null;
        try
        {
            session = await ModelPreviewService.CreateSessionAsync(path, token);
            _modelInitializationTask ??= InitializeModelWebViewAsync();
            await _modelInitializationTask.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            ResumeBrowser(ModelWebView);
            _modelSession = session;
            var color = (System.Windows.Application.Current.Resources["PreviewBackgroundBrush"] as SolidColorBrush)?.Color;
            session.Attach(ModelWebView.CoreWebView2, color is { } value && value.R + value.G + value.B > 384);
            var info = await session.Ready.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            _modelLoading = false;
            FileMetaText.Text += $"  •  {info.Summary}";
            ShowOnly(ModelView);
        }
        catch
        {
            session?.Dispose();
            if (ReferenceEquals(_modelSession, session)) _modelSession = null;
            throw;
        }
        finally
        {
            if (_previewCancellation?.Token == token) _modelLoading = false;
            QueueBrowserResourceUpdate();
        }
    }

    private void DisposeModelPreview()
    {
        _modelSession?.Dispose();
        _modelSession = null;
    }

    private async Task LoadRenderedImageAsync(string path, bool drawing, CancellationToken token)
    {
        try
        {
            var preview = drawing ? await DrawingPreviewService.LoadAsync(path, token) :
                await ExtendedImagePreviewService.LoadAsync(path, token);
            token.ThrowIfCancellationRequested();
            ShowBitmap(preview.Image, preview.Summary);
        }
        catch (NotSupportedException) when (!drawing && !token.IsCancellationRequested &&
            Path.GetExtension(path).ToLowerInvariant() is ".jxr" or ".hdp" or ".wdp")
        {
            // Optional WIC codecs can support camera/JPEG-XR variants unavailable to
            // the bundled decoder; retain those installed Windows capabilities.
            await LoadNativeImageAsync(path, token);
        }
    }

    private void ShowBitmap(BitmapSource image, string summary)
    {
        PreviewImage.Source = image;
        FitWindowToImage(image);
        ShowOnly(ImageView);
        FileMetaText.Text += $"  •  {summary}";
        Dispatcher.BeginInvoke(new Action(FitImageToViewport), DispatcherPriority.Loaded);
    }

    private void ShowTextContent(string text, string summary, bool monospaced = true)
    {
        PreviewText.Text = text;
        PreviewText.TextWrapping = monospaced ? TextWrapping.NoWrap : TextWrapping.Wrap;
        PreviewText.FontFamily = new FontFamily(monospaced ? "Cascadia Mono,Consolas" : "Segoe UI");
        PreviewText.FontSize = monospaced ? 13.5 : 16;
        PreviewText.ScrollToHome();
        FileMetaText.Text += $"  •  {summary}";
        ShowOnly(TextView);
    }

    private async Task LoadTechnicalAsync(string path, CancellationToken token)
    {
        var preview = await TechnicalPreviewService.LoadAsync(path, token);
        token.ThrowIfCancellationRequested();
        ShowTextContent(preview.Text, preview.Summary);
    }

    private async Task LoadTableAsync(string path, CancellationToken token)
    {
        var table = await TabularPreviewService.LoadAsync(path, token);
        token.ThrowIfCancellationRequested();
        var columns = new GridView { AllowsColumnReorder = false };
        for (var index = 0; index < table.Columns.Count; index++)
        {
            columns.Columns.Add(new GridViewColumn
            {
                Header = table.Columns[index], Width = Math.Clamp(table.Columns[index].Length * 8 + 32, 150, 300),
                DisplayMemberBinding = new System.Windows.Data.Binding($"[{index}]")
            });
        }
        TableRowsList.View = columns;
        TableRowsList.ItemsSource = table.Rows;
        TableSummaryText.Text = table.Summary;
        FileMetaText.Text += "  •  таблица";
        ShowOnly(TableView);
    }

    private async Task RenderPdfPageAsync(CancellationToken cancellationToken)
    {
        var document = _nativePdf;
        if (document is null) return;
        var pageIndex = _pdfPageIndex;
        PreviousPdfPageButton.IsEnabled = false;
        NextPdfPageButton.IsEnabled = false;
        var image = await document.RenderAsync(pageIndex, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_nativePdf, document) || _pdfPageIndex != pageIndex) return;
        NativePdfImage.Source = image;
        PdfPageText.Text = $"Страница {pageIndex + 1} из {document.PageCount}";
        PreviousPdfPageButton.IsEnabled = pageIndex > 0;
        NextPdfPageButton.IsEnabled = pageIndex + 1 < document.PageCount;
        ShowOnly(NativePdfView);
    }

    private async void ChangePdfPage(int offset)
    {
        if (_nativePdf is null || _previewCancellation is null) return;
        var next = (long)_pdfPageIndex + offset;
        if (next < 0 || next >= _nativePdf.PageCount) return;
        var token = _previewCancellation.Token;
        _pdfPageIndex = (uint)next;
        try { await RenderPdfPageAsync(token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!token.IsCancellationRequested) ShowError(exception.Message); }
    }

    private void PreviousPdfPageButton_Click(object sender, RoutedEventArgs e) => ChangePdfPage(-1);
    private void NextPdfPageButton_Click(object sender, RoutedEventArgs e) => ChangePdfPage(1);

    private async Task LoadArchiveAsync(string path, CancellationToken token)
    {
        var archive = await ArchivePreviewService.LoadAsync(path, token);
        token.ThrowIfCancellationRequested();
        ArchiveSummaryText.Text = archive.Error ?? archive.Summary;
        ArchiveEntriesList.ItemsSource = archive.Entries.Select(entry => new
        {
            Name = (entry.IsDirectory ? "📁  " : "") + entry.Path + (entry.IsEncrypted ? "  🔒" : ""),
            Size = entry.IsDirectory ? "—" : entry.Size is { } length ? FormatFileSize(length) : "—",
            Modified = entry.Modified?.ToString("g") ?? "—"
        }).ToArray();
        FileMetaText.Text += "  •  содержимое архива";
        ShowOnly(ArchiveView);
    }

    private async Task LoadOfficeAsync(string path, CancellationToken token)
    {
        var document = await OfficePreviewService.LoadAsync(path, token);
        token.ThrowIfCancellationRequested();
        ShowTextContent(document.Text, document.Summary);
    }

    private static string FormatJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string FormatXml(string text)
    {
        using var stringReader = new StringReader(text);
        var document = SafeXmlDocument.Load(stringReader, 2 * 1024 * 1024, CancellationToken.None,
            LoadOptions.PreserveWhitespace, preserveMarkup: true);
        var declaration = document.Declaration?.ToString();
        var body = document.ToString(SaveOptions.None);
        return string.IsNullOrWhiteSpace(declaration)
            ? body
            : $"{declaration}{Environment.NewLine}{body}";
    }

    private async Task LoadSystemPreviewAsync(string path, CancellationToken token)
    {
        var thumbnail = await Task.Run(() => ShellThumbnailService.TryGetThumbnail(path, 900, thumbnailOnly: true));
        token.ThrowIfCancellationRequested();
        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        if (thumbnail is null)
        {
            await LoadTechnicalAsync(path, token);
            return;
        }

        InfoThumbnail.Source = thumbnail;
        InfoThumbnail.Width = 520;
        InfoThumbnail.Height = 420;
        InfoTitle.Text = Path.GetFileName(path);
        InfoDetails.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        InfoDetails.TextAlignment = TextAlignment.Center;
        InfoDetails.Text = "Системный эскиз • нажмите Enter, чтобы открыть файл полностью";
        ShowOnly(InfoView);
    }

    internal void Navigate(NavigationDirection direction)
    {
        if (ExplorerSelectionService.HasOriginWindow)
        {
            var previous = _navigationTail;
            _navigationTail = NavigateFromExplorerAsync(previous, direction, _navigationEpoch,
                ExplorerSelectionService.OriginGeneration, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            return;
        }

        var offset = direction is NavigationDirection.Left or NavigationDirection.Up ? -1 : 1;
        var nextIndex = _navigationIndex + offset;
        if (nextIndex < 0 || nextIndex >= _navigationPaths.Count)
            return;

        _navigationIndex = nextIndex;
        _ = ShowPreviewAsync(_navigationPaths[_navigationIndex], rebuildNavigation: false);
    }

    private async Task NavigateFromExplorerAsync(Task previous, NavigationDirection direction, int epoch,
        int originGeneration, IntPtr previewWindow)
    {
        try
        {
            try { await previous; } catch { }
            if (!IsVisible || !IsActive || epoch != _navigationEpoch ||
                originGeneration != ExplorerSelectionService.OriginGeneration ||
                !GlobalKeyboardHook.CanHandlePreviewNavigation()) return;
            // Accessibility providers can be slow. Keep their reads and Shell COM
            // work off the UI thread so repeats, Escape and the hook stay responsive.
            var selectedPath = await ExplorerStaWorker.RunAsync(() =>
                ExplorerSelectionService.TryNavigateFromPreview(direction, previewWindow, originGeneration));
            if (!IsVisible || epoch != _navigationEpoch || originGeneration != ExplorerSelectionService.OriginGeneration) return;
            if (!string.IsNullOrWhiteSpace(selectedPath)) UpdatePreviewFromExplorer(selectedPath);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"QuickPreview navigation: {exception.Message}");
        }
    }


    private static async Task<bool> LooksLikeTextAsync(string path, long length, CancellationToken token)
    {
        if (length == 0) return true;
        var bytes = await BoundedFileReader.ReadAsync(path, 4096, token);
        token.ThrowIfCancellationRequested();
        return bytes.Length == 0 || FormatCatalog.LooksLikeText(bytes, endOfFile: bytes.Length == length);
    }

    private static string DecodeText(byte[] bytes) => FormatCatalog.DecodeText(bytes);

    private void PopulateHeader(string path, PreviewPathInfo info)
    {
        FileNameText.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (info.IsDirectory)
        {
            FileMetaText.Text = "Папка";
            return;
        }

        var type = string.IsNullOrEmpty(Path.GetExtension(path)) ? "Файл" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        FileMetaText.Text = $"{type}  •  {FormatFileSize(info.Length)}  •  {info.LastWriteTime:g}";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(value >= 10 || unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private void ResetViews()
    {
        ResetBrowserLoadingState();
        ResetRepresentations();
        ShowOnly(LoadingView);
        LoadingView.Opacity = 0;
        DisposeModelPreview();
        ReleaseSpreadsheetDocument();
        if (!string.Equals(Path.GetExtension(_currentPath), ".pdf", StringComparison.OrdinalIgnoreCase)) ReleasePdfDocument();
        else PdfWebView.CoreWebView2?.Stop();
        StopMedia();
        ResetAudioArtwork();
        PreviewImage.Source = null;
        PreviewImage.MaxWidth = double.PositiveInfinity;
        PreviewImage.MaxHeight = double.PositiveInfinity;
        PreviewImage.Width = double.NaN;
        PreviewImage.Height = double.NaN;
        _imageNaturalWidth = 0;
        _imageNaturalHeight = 0;
        _imageZoom = 1;
        _imageFitMode = true;
        EndImagePan();
        ImageZoomText.Text = "100%";
        ImageScrollViewer.ScrollToHorizontalOffset(0);
        ImageScrollViewer.ScrollToVerticalOffset(0);
        PreviewText.Clear();
        PreviewText.TextWrapping = TextWrapping.NoWrap;
        PreviewText.FontFamily = new FontFamily("Cascadia Mono,Consolas");
        PreviewText.FontSize = 13.5;
        _nativePdf = null;
        NativePdfImage.Source = null;
        ArchiveEntriesList.ItemsSource = null;
        TableRowsList.ItemsSource = null;
        InfoThumbnail.Source = null;
        InfoThumbnail.Width = 220;
        InfoThumbnail.Height = 220;
        InfoTitle.Text = string.Empty;
        InfoDetails.Text = string.Empty;
        ShowOnly(LoadingView);
    }

    private void ResetAudioArtwork()
    {
        AudioArtworkBorder.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        AudioFallbackIcon.Visibility = Visibility.Visible;
    }

    private void StopMedia()
    {
        _mediaTimer.Stop();
        _audioPlayer.Stop();
        _audioPlayer.Close();
        VideoMedia.Stop();
        VideoMedia.Source = null;

        _hasActiveMedia = false;
        _usingAudioPlayer = false;
        _activeSlider = null;
        _activeTimeText = null;
        _activePlayPauseButton = null;
        _mediaPlaying = false;
        _userSeeking = false;
    }

    private void ShowOnly(UIElement visible)
    {
        _visualPreviewView = visible;
        ApplyRepresentationVisibility();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = _hasSourcePreview ? message + "\n\nИсходное содержимое доступно на вкладке «Текст»." : message;
        ShowOnly(ErrorView);
    }

    private void SetDefaultWindowSize()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1000, area.Width * 0.86);
        Height = Math.Min(700, area.Height * 0.86);
        CenterInWorkArea(area);
    }

    private void SetAudioWindowSize()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(960, area.Width * 0.9);
        Height = Math.Min(540, area.Height * 0.84);
        CenterInWorkArea(area);
    }

    private void FitWindowToImage(BitmapSource bitmap)
    {
        var area = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        _imageNaturalWidth = bitmap.PixelWidth / Math.Max(dpi.DpiScaleX, 1);
        _imageNaturalHeight = bitmap.PixelHeight / Math.Max(dpi.DpiScaleY, 1);
        var maxContentWidth = area.Width * 0.9;
        var maxContentHeight = Math.Max(180, area.Height * 0.9 - 108);
        _imageZoom = Math.Min(1, Math.Min(maxContentWidth / _imageNaturalWidth, maxContentHeight / _imageNaturalHeight));
        _imageFitMode = true;
        var contentWidth = _imageNaturalWidth * _imageZoom;
        var contentHeight = _imageNaturalHeight * _imageZoom;

        ApplyImageZoom();
        Width = Math.Max(MinWidth, Math.Min(area.Width * 0.94, contentWidth + 36));
        Height = Math.Max(MinHeight, Math.Min(area.Height * 0.94, contentHeight + 108));
        CenterInWorkArea(area);
    }

    private void FitImageToViewport()
    {
        if (_imageNaturalWidth <= 0 || _imageNaturalHeight <= 0 || ImageView.Visibility != Visibility.Visible)
            return;

        var availableWidth = Math.Max(1, ImageScrollViewer.ViewportWidth - 44);
        var availableHeight = Math.Max(1, ImageScrollViewer.ViewportHeight - 108);
        _imageZoom = Math.Min(1, Math.Min(availableWidth / _imageNaturalWidth, availableHeight / _imageNaturalHeight));
        _imageFitMode = true;
        ApplyImageZoom();
        ImageScrollViewer.ScrollToHorizontalOffset(0);
        ImageScrollViewer.ScrollToVerticalOffset(0);
    }

    private void ApplyImageZoom()
    {
        if (_imageNaturalWidth <= 0 || _imageNaturalHeight <= 0)
            return;

        PreviewImage.Width = Math.Max(1, _imageNaturalWidth * _imageZoom);
        PreviewImage.Height = Math.Max(1, _imageNaturalHeight * _imageZoom);
        ImageZoomText.Text = $"{Math.Round(_imageZoom * 100):0}%";
        RenderOptions.SetBitmapScalingMode(PreviewImage, BitmapScalingMode.HighQuality);
    }

    private void SetImageZoom(double zoom, System.Windows.Point? viewportAnchor = null)
    {
        if (_imageNaturalWidth <= 0 || _imageNaturalHeight <= 0)
            return;

        var previousZoom = _imageZoom;
        var anchor = viewportAnchor ?? new System.Windows.Point(ImageScrollViewer.ViewportWidth / 2, ImageScrollViewer.ViewportHeight / 2);
        var horizontalOffset = ImageScrollViewer.HorizontalOffset;
        var verticalOffset = ImageScrollViewer.VerticalOffset;

        _imageZoom = Math.Clamp(zoom, ImageZoomSteps[0], ImageZoomSteps[^1]);
        _imageFitMode = false;
        ApplyImageZoom();
        ImageScrollViewer.UpdateLayout();

        var scaleChange = _imageZoom / Math.Max(previousZoom, ImageZoomSteps[0]);
        ImageScrollViewer.ScrollToHorizontalOffset((horizontalOffset + anchor.X) * scaleChange - anchor.X);
        ImageScrollViewer.ScrollToVerticalOffset((verticalOffset + anchor.Y) * scaleChange - anchor.Y);
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = Math.Pow(1.15, e.Delta / 120.0);
        SetImageZoom(_imageZoom * factor, e.GetPosition(ImageScrollViewer));
        e.Handled = true;
    }

    private void ImagePanSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ImageScrollViewer.ScrollableWidth <= 0 && ImageScrollViewer.ScrollableHeight <= 0)
            return;

        _imagePanning = true;
        _imagePanStart = e.GetPosition(ImageScrollViewer);
        _imagePanHorizontalOffset = ImageScrollViewer.HorizontalOffset;
        _imagePanVerticalOffset = ImageScrollViewer.VerticalOffset;
        ImagePanSurface.Cursor = System.Windows.Input.Cursors.SizeAll;
        ImagePanSurface.CaptureMouse();
        e.Handled = true;
    }

    private void ImagePanSurface_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_imagePanning || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(ImageScrollViewer);
        ImageScrollViewer.ScrollToHorizontalOffset(_imagePanHorizontalOffset - (current.X - _imagePanStart.X));
        ImageScrollViewer.ScrollToVerticalOffset(_imagePanVerticalOffset - (current.Y - _imagePanStart.Y));
        e.Handled = true;
    }

    private void ImagePanSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_imagePanning)
            return;

        EndImagePan();
        e.Handled = true;
    }

    private void ImagePanSurface_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => EndImagePan();

    private void EndImagePan()
    {
        _imagePanning = false;
        if (ImagePanSurface.IsMouseCaptured)
            ImagePanSurface.ReleaseMouseCapture();
        ImagePanSurface.Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private void ImageZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        var next = ImageZoomSteps.LastOrDefault(step => step < _imageZoom - 0.001);
        SetImageZoom(next > 0 ? next : ImageZoomSteps[0]);
    }

    private void ImageZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        var next = ImageZoomSteps.FirstOrDefault(step => step > _imageZoom + 0.001);
        SetImageZoom(next > 0 ? next : ImageZoomSteps[^1]);
    }

    private void ImageActualSizeButton_Click(object sender, RoutedEventArgs e) => SetImageZoom(1);

    private void ImageFitButton_Click(object sender, RoutedEventArgs e) => FitImageToViewport();

    private void ImageView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_imageFitMode)
            Dispatcher.BeginInvoke(new Action(FitImageToViewport), DispatcherPriority.Loaded);
    }

    private void CenterInWorkArea(Rect area)
    {
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void OpenCurrent()
    {
        if (_currentPath is null) return;
        TryShellAction(new ProcessStartInfo(_currentPath) { UseShellExecute = true }, "открыть файл");
    }

    private void RevealCurrent()
    {
        if (_currentPath is null) return;
        if (_currentPathIsDirectory)
            TryShellAction(new ProcessStartInfo("explorer.exe", $"\"{_currentPath}\"") { UseShellExecute = true }, "показать папку");
        else
            TryShellAction(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentPath}\"") { UseShellExecute = true }, "показать файл в папке");
    }

    private void TryShellAction(ProcessStartInfo startInfo, string action)
    {
        try
        {
            using var process = Process.Start(startInfo);
            HidePreview();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StopMedia();
            ShowError($"Не удалось {action}.\n{exception.Message}");
            if (_hasSourcePreview) PreviewTabs.SelectedIndex = 0;
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePreview();
            e.Handled = true;
        }
        else if ((e.Key is Key.Left or Key.Up or Key.Right or Key.Down) && GlobalKeyboardHook.CanHandlePreviewNavigation())
        {
            Navigate(e.Key switch
            {
                Key.Left => NavigationDirection.Left,
                Key.Up => NavigationDirection.Up,
                Key.Right => NavigationDirection.Right,
                _ => NavigationDirection.Down
            });
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenCurrent();
            e.Handled = true;
        }
        else if (ImageView.Visibility == Visibility.Visible &&
                 (e.Key == Key.Add || e.Key == Key.OemPlus))
        {
            ImageZoomInButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ImageView.Visibility == Visibility.Visible &&
                 (e.Key == Key.Subtract || e.Key == Key.OemMinus))
        {
            ImageZoomOutButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ImageView.Visibility == Visibility.Visible && e.Key == Key.D0 &&
                 Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            FitImageToViewport();
            e.Handled = true;
        }
        else if (e.Key == Key.E && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                 Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            RevealCurrent();
            e.Handled = true;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        _navigationEpoch++;
        ExplorerSelectionService.CancelPendingNavigation();
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => KeepAboveOriginExplorer()), DispatcherPriority.Input);
    }

    private void ActiveMedia_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_usingAudioPlayer || !ReferenceEquals(sender, VideoMedia))
            return;
        HandleMediaOpened();
    }

    private void ActiveMedia_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_usingAudioPlayer || !ReferenceEquals(sender, VideoMedia))
            return;
        HandleMediaEnded();
    }

    private void ActiveMedia_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_usingAudioPlayer || !ReferenceEquals(sender, VideoMedia))
            return;
        HandleMediaFailed(e.ErrorException);
    }

    private void HandleMediaOpened()
    {
        if (!_hasActiveMedia || _activeSlider is null)
            return;

        var duration = GetActiveDuration();
        if (duration > TimeSpan.Zero)
            _activeSlider.Maximum = duration.TotalSeconds;
        if (_mediaPlaying)
            PlayActiveMedia();
        UpdateMediaPosition();
    }

    private void HandleMediaEnded()
    {
        if (!_hasActiveMedia)
            return;

        SetActivePosition(TimeSpan.Zero);
        PauseActiveMedia();
        _mediaPlaying = false;
        if (_activePlayPauseButton is not null)
            _activePlayPauseButton.Content = "\uE768";
        UpdateMediaPosition();
    }

    private void HandleMediaFailed(Exception exception)
    {
        StopMedia();
        ShowError($"Windows не смогла воспроизвести этот формат.\n{exception.Message}");
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasActiveMedia)
            return;

        if (_mediaPlaying)
        {
            PauseActiveMedia();
            if (_activePlayPauseButton is not null) _activePlayPauseButton.Content = "\uE768";
        }
        else
        {
            PlayActiveMedia();
            if (_activePlayPauseButton is not null) _activePlayPauseButton.Content = "\uE769";
        }
        _mediaPlaying = !_mediaPlaying;
    }

    private void MediaSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !_hasActiveMedia || !ReferenceEquals(slider, _activeSlider))
            return;

        if (FindVisualParent<Thumb>(e.OriginalSource as DependencyObject) is not null)
        {
            _userSeeking = true;
            return;
        }

        SetMediaPositionFromPointer(slider, e.GetPosition(slider).X);
        e.Handled = true;
    }

    private void MediaSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !_hasActiveMedia || !ReferenceEquals(slider, _activeSlider))
            return;

        if (_userSeeking)
            SetActivePosition(TimeSpan.FromSeconds(slider.Value));
        _userSeeking = false;
        UpdateMediaPosition();
    }

    private void SetMediaPositionFromPointer(Slider slider, double pointerX)
    {
        if (!_hasActiveMedia || slider.ActualWidth <= 0)
            return;

        var ratio = Math.Clamp(pointerX / slider.ActualWidth, 0, 1);
        slider.Value = slider.Minimum + (slider.Maximum - slider.Minimum) * ratio;
        SetActivePosition(TimeSpan.FromSeconds(slider.Value));
        UpdateMediaPosition();
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T result)
                return result;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void UpdateMediaPosition()
    {
        if (!_hasActiveMedia || _activeSlider is null || _activeTimeText is null)
            return;

        if (!_userSeeking)
            _activeSlider.Value = Math.Clamp(GetActivePosition().TotalSeconds, _activeSlider.Minimum, _activeSlider.Maximum);

        var current = TimeSpan.FromSeconds(_activeSlider.Value);
        var total = GetActiveDuration();
        if (_usingAudioPlayer)
        {
            _activeTimeText.Text = FormatTime(current);
            AudioDurationText.Text = FormatTime(total);
            AudioDurationHeroText.Text = FormatTime(total);
        }
        else
        {
            _activeTimeText.Text = $"{FormatTime(current)} / {FormatTime(total)}";
        }
    }

    private TimeSpan GetActivePosition() => _usingAudioPlayer ? _audioPlayer.Position : VideoMedia.Position;

    private TimeSpan GetActiveDuration()
    {
        var duration = _usingAudioPlayer ? _audioPlayer.NaturalDuration : VideoMedia.NaturalDuration;
        return duration.HasTimeSpan ? duration.TimeSpan : TimeSpan.Zero;
    }

    private void SetActivePosition(TimeSpan position)
    {
        if (_usingAudioPlayer)
            _audioPlayer.Position = position;
        else
            VideoMedia.Position = position;
    }

    private void PlayActiveMedia()
    {
        if (_usingAudioPlayer)
            _audioPlayer.Play();
        else
            VideoMedia.Play();
    }

    private void PauseActiveMedia()
    {
        if (_usingAudioPlayer)
            _audioPlayer.Pause();
        else
            VideoMedia.Pause();
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void MediaPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_userSeeking && sender is Slider slider && ReferenceEquals(slider, _activeSlider) && _activeTimeText is not null)
        {
            var total = GetActiveDuration();
            var current = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            _activeTimeText.Text = _usingAudioPlayer ? current : $"{current} / {FormatTime(total)}";
        }
    }

    private void SkipBackwardButton_Click(object sender, RoutedEventArgs e) => SkipMedia(-15);
    private void SkipForwardButton_Click(object sender, RoutedEventArgs e) => SkipMedia(15);

    private void SkipMedia(double seconds)
    {
        if (!_hasActiveMedia)
            return;

        var duration = GetActiveDuration();
        var target = Math.Clamp(GetActivePosition().TotalSeconds + seconds, 0, duration.TotalSeconds);
        SetActivePosition(TimeSpan.FromSeconds(target));
        UpdateMediaPosition();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        if (!_isMuted && _volume <= 0.001)
            _volume = 0.5;
        ApplyVolumeState();
        SyncVolumeControls();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolumeControls)
            return;

        _volume = Math.Clamp(e.NewValue, 0, 1);
        _isMuted = _volume <= 0.001;
        ApplyVolumeState();
        SyncVolumeControls();
    }

    private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || slider.ActualWidth <= 0 ||
            FindVisualParent<Thumb>(e.OriginalSource as DependencyObject) is not null)
            return;

        var ratio = Math.Clamp(e.GetPosition(slider).X / slider.ActualWidth, 0, 1);
        slider.Value = slider.Minimum + (slider.Maximum - slider.Minimum) * ratio;
        e.Handled = true;
    }

    private void VolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider)
            return;

        slider.Value = Math.Clamp(slider.Value + Math.Sign(e.Delta) * 0.05, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private void ApplyVolumeState()
    {
        _audioPlayer.Volume = _volume;
        _audioPlayer.IsMuted = _isMuted;
        VideoMedia.Volume = _volume;
        VideoMedia.IsMuted = _isMuted;
    }

    private void SyncVolumeControls()
    {
        _updatingVolumeControls = true;
        AudioVolumeSlider.Value = _volume;
        VideoVolumeSlider.Value = _volume;
        _updatingVolumeControls = false;

        var icon = _isMuted || _volume <= 0.001 ? "\uE74F" : "\uE767";
        var tooltip = _isMuted ? "Включить звук" : "Выключить звук";
        AudioMuteButton.Content = icon;
        VideoMuteButton.Content = icon;
        AudioMuteButton.ToolTip = tooltip;
        VideoMuteButton.ToolTip = tooltip;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HidePreview();
    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenCurrent();
    private void RevealButton_Click(object sender, RoutedEventArgs e) => RevealCurrent();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        HidePreview();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
