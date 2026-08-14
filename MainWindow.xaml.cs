using System.Diagnostics;
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

namespace QuickPreview;

public partial class MainWindow : Window
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private const int SwRestore = 9;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".wdp", ".webp"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".wma", ".aac", ".m4a", ".flac"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".jsonl", ".xml", ".yaml", ".yml",
        ".csv", ".tsv", ".ini", ".cfg", ".conf", ".toml", ".env", ".properties",
        ".cs", ".fs", ".vb", ".cpp", ".c", ".h", ".hpp", ".java", ".kt", ".swift",
        ".js", ".jsx", ".ts", ".tsx", ".html", ".htm", ".css", ".scss", ".less",
        ".py", ".rb", ".php", ".go", ".rs", ".sh", ".ps1", ".bat", ".cmd",
        ".sql", ".graphql", ".vue", ".svelte", ".dockerfile", ".gitignore", ".gitattributes",
        ".srt", ".vtt", ".ass", ".rtf"
    };

    private readonly DispatcherTimer _mediaTimer;
    private readonly MediaPlayer _audioPlayer = new();
    private readonly List<string> _navigationPaths = new();
    private string? _currentPath;
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
    private double _volume = 0.8;

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
    }

    public void ShowPreview(string path) => _ = ShowPreviewAsync(path, rebuildNavigation: true);

    private async Task ShowPreviewAsync(string path, bool rebuildNavigation)
    {
        if (rebuildNavigation)
            BuildNavigation(path);

        _currentPath = path;
        ResetViews();
        PopulateHeader(path);
        SetDefaultWindowSize();

        BringPreviewToFront();

        try
        {
            await LoadPreviewAsync(path);
        }
        catch (Exception exception)
        {
            if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            ShowError($"Не удалось открыть предпросмотр.\n{exception.Message}");
        }
    }

    public void HidePreview()
    {
        var pathToSelect = IsVisible ? _currentPath : null;
        StopMedia();
        PreviewImage.Source = null;
        InfoThumbnail.Source = null;
        _currentPath = null;
        Hide();

        if (!string.IsNullOrWhiteSpace(pathToSelect))
            _ = ExplorerSelectionService.TrySelectPathInOriginWindow(pathToSelect);
    }

    private void BringPreviewToFront()
    {
        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

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

    private async Task LoadPreviewAsync(string path)
    {
        if (Directory.Exists(path))
        {
            await LoadFolderPreviewAsync(path);
            return;
        }

        if (!File.Exists(path))
            throw new FileNotFoundException("Файл больше не существует.", path);

        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await LoadPdfAsync(path);
            return;
        }

        if (ImageExtensions.Contains(extension))
        {
            LoadImage(path);
            return;
        }

        if (AudioExtensions.Contains(extension))
        {
            await LoadAudioAsync(path);
            return;
        }

        if (VideoExtensions.Contains(extension))
        {
            LoadVideo(path);
            return;
        }

        if (TextExtensions.Contains(extension) || await LooksLikeTextAsync(path))
        {
            await LoadTextAsync(path);
            return;
        }

        await LoadSystemPreviewAsync(path);
    }

    private void LoadImage(string path)
    {
        BitmapFrame frame;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames.OrderByDescending(item => (long)item.PixelWidth * item.PixelHeight).First();
        }

        var orientation = ReadExifOrientation(frame);
        BitmapSource bitmap = ApplyExifOrientation(frame, orientation);
        bitmap.Freeze();

        PreviewImage.Source = bitmap;
        FitWindowToImage(bitmap);
        ShowOnly(ImageView);
        FileMetaText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}  •  {FormatFileSize(new FileInfo(path).Length)}";
    }

    private async Task LoadAudioAsync(string path)
    {
        ResetAudioArtwork();
        ConfigureMedia(usingAudioPlayer: true, AudioPositionSlider, AudioTimeText, AudioPlayPauseButton);
        AudioTitleText.Text = Path.GetFileNameWithoutExtension(path);
        AudioFormatText.Text = $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}  •  {FormatFileSize(new FileInfo(path).Length)}";
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

    private async Task LoadTextAsync(string path)
    {
        const int maxBytes = 2 * 1024 * 1024;
        var info = new FileInfo(path);
        var bytesToRead = (int)Math.Min(info.Length, maxBytes);
        var buffer = new byte[bytesToRead];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead));
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != buffer.Length)
                Array.Resize(ref buffer, totalRead);
        }

        var text = DecodeText(buffer);
        var structuredTextStatus = string.Empty;
        if (info.Length <= maxBytes)
        {
            try
            {
                var extension = Path.GetExtension(path);
                if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                {
                    text = FormatJson(text);
                    structuredTextStatus = "форматировано";
                }
                else if (string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
                {
                    text = FormatXml(text);
                    structuredTextStatus = "форматировано";
                }
            }
            catch (JsonException exception)
            {
                var line = exception.LineNumber is null ? string.Empty : $", строка {exception.LineNumber.Value + 1}";
                structuredTextStatus = $"исходный текст — ошибка JSON{line}";
            }
            catch (XmlException exception)
            {
                structuredTextStatus = $"исходный текст — ошибка XML, строка {exception.LineNumber}";
            }
        }
        if (info.Length > maxBytes)
            text += $"\n\n—— Превью ограничено первыми {FormatFileSize(maxBytes)} из {FormatFileSize(info.Length)} ——";

        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        PreviewText.Text = text;
        PreviewText.ScrollToHome();
        if (!string.IsNullOrWhiteSpace(structuredTextStatus))
            FileMetaText.Text += $"  •  {structuredTextStatus}";
        ShowOnly(TextView);
    }

    private async Task LoadPdfAsync(string path)
    {
        try
        {
            await PdfWebView.EnsureCoreWebView2Async();
            if (!_pdfWebViewConfigured)
            {
                PdfWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                PdfWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                PdfWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                PdfWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
                PdfWebView.CoreWebView2.Settings.IsZoomControlEnabled = true;
                _pdfWebViewConfigured = true;
            }

            if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            PdfWebView.CoreWebView2.Navigate(new Uri(path, UriKind.Absolute).AbsoluteUri);
            ShowOnly(PdfView);
            FileMetaText.Text += "  •  встроенный PDF-просмотр";
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or InvalidOperationException or COMException)
        {
            await LoadSystemPreviewAsync(path);
            if (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
                InfoDetails.Text = "Встроенный PDF-просмотр недоступен. Показан системный эскиз; нажмите Enter, чтобы открыть документ полностью.";
        }
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
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var stringReader = new StringReader(text);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        var declaration = document.Declaration?.ToString();
        var body = document.ToString(SaveOptions.None);
        return string.IsNullOrWhiteSpace(declaration)
            ? body
            : $"{declaration}{Environment.NewLine}{body}";
    }

    private async Task LoadFolderPreviewAsync(string path)
    {
        var summary = await Task.Run(() =>
        {
            var directory = new DirectoryInfo(path);
            var entries = directory.EnumerateFileSystemInfos()
                .OrderByDescending(entry => entry is DirectoryInfo)
                .ThenBy(entry => entry.Name, NaturalStringComparer.Instance)
                .Take(24)
                .Select(entry => entry is DirectoryInfo
                    ? $"📁  {entry.Name}"
                    : $"     {entry.Name}   ·   {FormatFileSize(((FileInfo)entry).Length)}")
                .ToList();
            return entries.Count == 0 ? "Папка пуста" : string.Join(Environment.NewLine, entries);
        });

        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        InfoThumbnail.Source = ShellThumbnailService.TryGetThumbnail(path, 256);
        InfoTitle.Text = new DirectoryInfo(path).Name;
        InfoDetails.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono,Consolas");
        InfoDetails.TextAlignment = TextAlignment.Left;
        InfoDetails.Text = summary;
        ShowOnly(InfoView);
        FileMetaText.Text = "Папка";
    }

    private async Task LoadSystemPreviewAsync(string path)
    {
        var thumbnail = await Task.Run(() => ShellThumbnailService.TryGetThumbnail(path, 900));
        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        InfoThumbnail.Source = thumbnail;
        InfoThumbnail.Width = thumbnail is null ? 96 : 520;
        InfoThumbnail.Height = thumbnail is null ? 96 : 420;
        InfoTitle.Text = Path.GetFileName(path);
        InfoDetails.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        InfoDetails.TextAlignment = TextAlignment.Center;
        InfoDetails.Text = thumbnail is null
            ? "Для этого формата Windows не предоставила эскиз. Нажмите Enter, чтобы открыть файл."
            : "Системный эскиз • нажмите Enter, чтобы открыть файл полностью";
        ShowOnly(InfoView);
    }

    private void BuildNavigation(string path)
    {
        _navigationPaths.Clear();
        _navigationIndex = -1;

        try
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                return;

            _navigationPaths.AddRange(Directory.EnumerateFiles(parent)
                .OrderBy(file => Path.GetFileName(file), NaturalStringComparer.Instance));
            _navigationIndex = _navigationPaths.FindIndex(file =>
                string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException)
        {
            _navigationPaths.Clear();
            _navigationIndex = -1;
        }
        catch (IOException)
        {
            _navigationPaths.Clear();
            _navigationIndex = -1;
        }

    }

    private void Navigate(int offset)
    {
        var nextIndex = _navigationIndex + offset;
        if (nextIndex < 0 || nextIndex >= _navigationPaths.Count)
            return;

        _navigationIndex = nextIndex;
        _ = ShowPreviewAsync(_navigationPaths[_navigationIndex], rebuildNavigation: false);
    }

    private static ushort ReadExifOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                var value = metadata.GetQuery("/app1/ifd/{ushort=274}");
                if (value is not null)
                    return Convert.ToUInt16(value, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or FormatException)
        {
            // Damaged or non-standard EXIF should not prevent the image from opening.
        }

        return 1;
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource source, ushort orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => CreateTransform(new ScaleTransform(-1, 1), new RotateTransform(270)),
            6 => new RotateTransform(90),
            7 => CreateTransform(new ScaleTransform(-1, 1), new RotateTransform(90)),
            8 => new RotateTransform(270),
            _ => null
        };

        return transform is null ? source : new TransformedBitmap(source, transform);
    }

    private static TransformGroup CreateTransform(params Transform[] transforms)
    {
        var group = new TransformGroup();
        foreach (var transform in transforms)
            group.Children.Add(transform);
        return group;
    }

    private static async Task<bool> LooksLikeTextAsync(string path)
    {
        var info = new FileInfo(path);
        if (info.Length == 0)
            return true;

        var length = (int)Math.Min(info.Length, 4096);
        var buffer = new byte[length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(buffer.AsMemory());
        if (read == 0) return true;

        var suspicious = 0;
        for (var index = 0; index < read; index++)
        {
            var value = buffer[index];
            if (value == 0) return false;
            if (value < 8 || (value > 13 && value < 32)) suspicious++;
        }

        return suspicious < read / 20;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private void PopulateHeader(string path)
    {
        FileNameText.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (Directory.Exists(path))
        {
            FileMetaText.Text = "Папка";
            return;
        }

        var info = new FileInfo(path);
        var type = string.IsNullOrEmpty(info.Extension) ? "Файл" : info.Extension.TrimStart('.').ToUpperInvariant();
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
        StopMedia();
        ResetAudioArtwork();
        PreviewImage.Source = null;
        PreviewImage.MaxWidth = double.PositiveInfinity;
        PreviewImage.MaxHeight = double.PositiveInfinity;
        PreviewText.Clear();
        if (PdfWebView.CoreWebView2 is not null)
            PdfWebView.CoreWebView2.Navigate("about:blank");
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
        foreach (var view in new[] { LoadingView, ImageView, TextView, PdfView, VideoView, AudioView, InfoView, ErrorView })
            view.Visibility = ReferenceEquals(view, visible) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
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
        var naturalWidth = bitmap.PixelWidth / Math.Max(dpi.DpiScaleX, 1);
        var naturalHeight = bitmap.PixelHeight / Math.Max(dpi.DpiScaleY, 1);
        var maxContentWidth = area.Width * 0.9;
        var maxContentHeight = Math.Max(180, area.Height * 0.9 - 108);
        var scale = Math.Min(1, Math.Min(maxContentWidth / naturalWidth, maxContentHeight / naturalHeight));
        var contentWidth = naturalWidth * scale;
        var contentHeight = naturalHeight * scale;

        PreviewImage.MaxWidth = naturalWidth;
        PreviewImage.MaxHeight = naturalHeight;
        Width = Math.Max(MinWidth, Math.Min(area.Width * 0.94, contentWidth + 36));
        Height = Math.Max(MinHeight, Math.Min(area.Height * 0.94, contentHeight + 108));
        CenterInWorkArea(area);
    }

    private void CenterInWorkArea(Rect area)
    {
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void OpenCurrent()
    {
        if (_currentPath is null) return;
        Process.Start(new ProcessStartInfo(_currentPath) { UseShellExecute = true });
        HidePreview();
    }

    private void RevealCurrent()
    {
        if (_currentPath is null) return;
        if (Directory.Exists(_currentPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_currentPath}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentPath}\"") { UseShellExecute = true });
        HidePreview();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePreview();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            Navigate(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            Navigate(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenCurrent();
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
        // Keep the preview open when clicking its media controls or taskbar button.
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

    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static NaturalStringComparer Instance { get; } = new();
        public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string first, string second);

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
