using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickPreview;

internal static class Program
{
    private static int _exitCode;
    private static readonly string TestDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));

    [STAThread]
    private static int Main()
    {
        // Initialize the real resource dictionary without Application.Run, so App_OnStartup
        // never installs keyboard hooks, starts the tray, or changes startup settings.
        var application = new App();
        application.InitializeComponent();
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        application.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), application, startup);
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(application); }
            catch (Exception exception) { Console.Error.WriteLine(exception); _exitCode = 1; }
            finally { application.Shutdown(); Dispatcher.ExitAllFrames(); }
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task RunAsync(App application)
    {
        var screenshots = Path.Combine(TestDirectory, "screenshots");
        Directory.CreateDirectory(screenshots);
        var archivePath = Path.GetFullPath(Path.Combine(TestDirectory, "../ArchiveSmoke/fixtures/archives/sample.7z"));
        var pdfPath = Path.GetFullPath(Path.Combine(TestDirectory, "../Fixtures/preview.pdf"));
        var officePath = Path.Combine(AppContext.BaseDirectory, "sample.docx");
        CreateOfficeFixture(officePath);
        var csvPath = Path.Combine(AppContext.BaseDirectory, "alignment-table.csv");
        File.WriteAllText(csvPath, "ID,Очень длинное название сопоставимого столбца данных,Примечание\r\n1,Короткое значение,Текст\r\n2,Длинное значение для проверки горизонтальной прокрутки,Другая строка\r\n", new System.Text.UTF8Encoding(false));
        var emailPath = Path.Combine(AppContext.BaseDirectory, "sample.eml");
        File.WriteAllText(emailPath, "From: Anna <anna@example.test>\r\nTo: User <user@example.test>\r\nSubject: Проверка предпросмотра письма\r\nDate: Mon, 16 Nov 2026 10:00:00 +0300\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<html><body><h1>Письмо для проверки</h1><p>Длинная строка должна переноситься по ширине окна, чтобы текст письма можно было прочитать и в узком предпросмотре.</p><ul><li>Первый пункт</li><li>Второй пункт</li></ul><script>не показывать</script></body></html>", new System.Text.UTF8Encoding(false));
        var svgPath = Path.Combine(AppContext.BaseDirectory, "sample.svg");
        File.WriteAllText(svgPath, "<svg xmlns='http://www.w3.org/2000/svg' width='640' height='360' viewBox='0 0 640 360'><rect width='640' height='360' rx='24' fill='#195ea6'/><circle cx='470' cy='145' r='75' fill='#8fe3df'/><path d='M55 285 L210 80 L350 285 Z' fill='#f9c66a'/><text x='350' y='300' font-size='42' fill='white'>SVG 123</text></svg>");
        var fontPath = Path.Combine(AppContext.BaseDirectory, "preview font with spaces.ttf");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"), fontPath, overwrite: true);

        var themeType = typeof(App).Assembly.GetType("QuickPreview.ThemeService", throwOnError: true)!;
        using var theme = (IDisposable)Activator.CreateInstance(themeType, nonPublic: true)!;
        var window = new MainWindow();
        application.MainWindow = window;
        themeType.GetMethod("RegisterWindow")!.Invoke(theme, new object[] { window });
        // Exercise precisely the fallback used by an installation with a missing native loader.
        typeof(MainWindow).GetField("_pdfInitializationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, Task.FromException(new DllNotFoundException("UI fixture: missing WebView2Loader.dll")));

        foreach (var themeName in new[] { "light", "dark" })
        {
            Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", themeName);
            themeType.GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(theme, null);
            foreach (var (width, height) in new[] { (1000d, 700d), (520d, 500d) })
            {
                window.ShowPreview(archivePath, activateWindow: false);
                await WaitAsync(window, () => Control<Grid>(window, "ArchiveView").IsVisible, "archive loading");
                await SetSizeAsync(window, width, height);
                var list = Control<ListView>(window, "ArchiveEntriesList");
                Assert(list.Items.Count == 2, "Archive rows displayed");
                VerifyArchiveColumns(list);
                Capture(window, Path.Combine(screenshots, $"archive-{themeName}-{width:0}.png"));

                window.ShowPreview(pdfPath, activateWindow: false);
                await WaitAsync(window, () => Control<Grid>(window, "NativePdfView").IsVisible, "native PDF fallback");
                await SetSizeAsync(window, width, height);
                VerifyPdfButtons(window);
                Assert(Control<TextBlock>(window, "PdfPageText").Text == "Страница 1 из 2", "PDF first-page label");
                var next = Control<Button>(window, "NextPdfPageButton");
                Assert(next.IsEnabled, "PDF next-page control enabled");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(window, () => Control<TextBlock>(window, "PdfPageText").Text == "Страница 2 из 2", "PDF second page");
                Assert(!next.IsEnabled && Control<Button>(window, "PreviousPdfPageButton").IsEnabled,
                    "PDF page navigation controls update");
                Capture(window, Path.Combine(screenshots, $"pdf-{themeName}-{width:0}.png"));

                window.ShowPreview(officePath, activateWindow: false);
                await WaitAsync(window, () => Control<Grid>(window, "TextView").IsVisible, "Office preview");
                await SetSizeAsync(window, width, height);
                Assert(Control<TextBox>(window, "PreviewText").Text.Contains("Документ Office для проверки"),
                    "Office document text displayed");
                Capture(window, Path.Combine(screenshots, $"office-{themeName}-{width:0}.png"));

                foreach (var textScale in new[] { 1d, 1.25d })
                {
                    var scaleName = textScale == 1 ? "100" : "125";
                    window.ShowPreview(csvPath, activateWindow: false);
                    await WaitAsync(window, () => Control<Grid>(window, "TableView").IsVisible, "CSV table preview");
                    await SetSizeAsync(window, width, height);
                    using (ScaleText(window, textScale))
                    {
                        await FinishLayoutAsync(window);
                        var table = Control<ListView>(window, "TableRowsList");
                        Assert(table.Items.Count == 2, "CSV data rows displayed");
                        VerifyColumns(table, 3, "CSV");
                        Assert(Control<TextBlock>(window, "TableSummaryText").Text.Contains("столбцов: 3"), "CSV summary displayed");
                        Capture(window, Path.Combine(screenshots, $"csv-{themeName}-{width:0}-{scaleName}.png"));
                    }

                    window.ShowPreview(fontPath, activateWindow: false);
                    await WaitAsync(window, () => Control<Grid>(window, "FontView").IsVisible, "font specimen");
                    await SetSizeAsync(window, width, height);
                    using (ScaleText(window, textScale))
                    {
                        await FinishLayoutAsync(window);
                        Assert(Control<TextBlock>(window, "FontNameText").Text.Contains("Arial", StringComparison.OrdinalIgnoreCase), "Font file with spaces resolves Arial family");
                        var panel = Control<StackPanel>(window, "FontSamplesPanel");
                        var samples = Descendants<TextBlock>(panel).ToArray();
                        Assert(samples.Length == 4 && samples.All(sample => sample.FontFamily.FamilyNames.Values.Any(name => name.Contains("Arial", StringComparison.OrdinalIgnoreCase))), "Font specimen uses selected local family");
                        Assert(samples.All(sample => sample.ActualWidth <= panel.ActualWidth + 1 && Bounds(sample, panel).Left >= -1), "Font specimen wraps inside content column");
                        Capture(window, Path.Combine(screenshots, $"font-{themeName}-{width:0}-{scaleName}.png"));
                    }

                    window.ShowPreview(emailPath, activateWindow: false);
                    await WaitAsync(window, () => Control<Grid>(window, "TextView").IsVisible, "EML document preview");
                    await SetSizeAsync(window, width, height);
                    using (ScaleText(window, textScale))
                    {
                        await FinishLayoutAsync(window);
                        var text = Control<TextBox>(window, "PreviewText");
                        Assert(text.Text.Contains("Письмо для проверки") && text.Text.Contains("anna@example.test") && !text.Text.Contains("не показывать"), "EML headers and body displayed without script");
                        Assert(text.TextWrapping == TextWrapping.Wrap && text.ExtentWidth <= text.ViewportWidth + 1, "EML text fits viewport without horizontal scroll");
                        Capture(window, Path.Combine(screenshots, $"eml-{themeName}-{width:0}-{scaleName}.png"));
                    }

                    window.ShowPreview(svgPath, activateWindow: false);
                    await WaitAsync(window, () => Control<Grid>(window, "ImageView").IsVisible && Control<Image>(window, "PreviewImage").Source is not null, "SVG image preview");
                    await SetSizeAsync(window, width, height);
                    using (ScaleText(window, textScale))
                    {
                        await FinishLayoutAsync(window);
                        var image = Control<Image>(window, "PreviewImage");
                        Assert(image.Source is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "SVG rendered image present");
                        VerifyFooterButtons(window);
                        Capture(window, Path.Combine(screenshots, $"svg-{themeName}-{width:0}-{scaleName}.png"));
                    }
                }
            }
        }
        window.HidePreview();
        Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", null);
        Console.WriteLine($"PASS: WPF UI fixtures; screenshots: {screenshots}");
    }

    private static async Task SetSizeAsync(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
    }

    private static void VerifyPdfButtons(MainWindow window)
    {
        var previous = Control<Button>(window, "PreviousPdfPageButton");
        var next = Control<Button>(window, "NextPdfPageButton");
        var previousBounds = Bounds(previous, window);
        var nextBounds = Bounds(next, window);
        Assert(Math.Abs(previousBounds.Top - nextBounds.Top) <= 1 &&
               Math.Abs(previousBounds.Height - nextBounds.Height) <= 1, "PDF navigation buttons aligned");
        foreach (var button in new[] { previous, next })
        {
            var text = Descendants<TextBlock>(button).FirstOrDefault();
            if (text is null) continue;
            var textBounds = Bounds(text, button);
            Assert(textBounds.Top >= 0 && textBounds.Bottom <= button.ActualHeight + 1 &&
                   textBounds.Left >= 0 && textBounds.Right <= button.ActualWidth + 1, "PDF arrow glyph fits control");
        }
    }

    private static void VerifyArchiveColumns(ListView list) => VerifyColumns(list, 3, "Archive");

    private static void VerifyColumns(ListView list, int count, string name)
    {
        var item = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Assert(item is not null, $"{name} first row materialized");
        var row = Descendants<GridViewRowPresenter>(item!).Single();
        var headers = Descendants<GridViewColumnHeader>(list).Where(header => header.Column is not null)
            .OrderBy(header => Bounds(header, list).Left).ToArray();
        Assert(headers.Length == count && row.Columns.Count == count, $"{name} header/row column count");
        Assert(headers.Select(header => header.Column).SequenceEqual(row.Columns), $"{name} header/row column order");
        var cells = Descendants<TextBlock>(row).ToArray();
        Assert(cells.Length == count, $"{name} row cells materialized");
        for (var index = 0; index < cells.Length; index++)
        {
            var header = headers[index];
            var presenter = Descendants<ContentPresenter>(header).FirstOrDefault(content => Equals(content.Content, header.Content));
            if (presenter is not null)
            {
                var delta = Bounds(cells[index], list).Left - Bounds(presenter, list).Left;
                Console.WriteLine($"{name} column {index} header/row text offset: {delta:F2}px");
                Assert(Math.Abs(delta) <= 1, $"{name} column {index} header and row aligned");
            }
        }
    }

    private static void VerifyFooterButtons(MainWindow window)
    {
        var buttons = Descendants<Button>((DependencyObject)window.Content)
            .Where(button => button.IsVisible && Equals(button.Content, "Открыть") || button.IsVisible && Equals(button.Content, "Показать в папке"))
            .ToArray();
        Assert(buttons.Length == 2, "Footer actions materialized");
        var first = Bounds(buttons[0], window); var second = Bounds(buttons[1], window);
        Assert(Math.Abs(first.Top - second.Top) <= 1 && Math.Abs(first.Bottom - second.Bottom) <= 1,
            "Footer action controls share top and bottom bounds");
    }

    private static async Task FinishLayoutAsync(MainWindow window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
    }

    private static IDisposable ScaleText(MainWindow window, double factor)
    {
        var values = Descendants<FrameworkElement>((DependencyObject)window.Content)
            .Select(element => element switch
            {
                TextBlock text => (Element: (DependencyObject)text, Property: TextBlock.FontSizeProperty, Size: text.FontSize),
                System.Windows.Controls.Control control => (Element: (DependencyObject)control, Property: System.Windows.Controls.Control.FontSizeProperty, Size: control.FontSize),
                _ => default
            }).Where(value => value.Element is not null).ToArray();
        foreach (var value in values) value.Element.SetCurrentValue(value.Property, value.Size * factor);
        return new TextScaleScope(values.Select(value => new Action(() => value.Element.SetCurrentValue(value.Property, value.Size))).ToArray());
    }

    private sealed class TextScaleScope : IDisposable
    {
        private readonly Action[] _restore;
        public TextScaleScope(Action[] restore) => _restore = restore;
        public void Dispose() { foreach (var action in _restore) action(); }
    }

    private static async Task WaitAsync(MainWindow window, Func<bool> condition, string operation)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (Control<Grid>(window, "ErrorView").IsVisible)
                throw new Exception($"{operation}: {Control<TextBlock>(window, "ErrorText").Text}");
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(operation);
            await Task.Delay(25);
        }
        window.UpdateLayout();
    }

    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Capture(MainWindow window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void CreateOfficeFixture(string path)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Write("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Write("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Документ Office для проверки</w:t></w:r></w:p><w:p><w:r><w:t>Текст документа доступен без установки Microsoft Office.</w:t></w:r></w:p></w:body></w:document>");
        void Write(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
