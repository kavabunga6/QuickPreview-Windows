using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;

internal static class Program
{
    private static int _exitCode;
    private static readonly string TestDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
    private const string SvgSource = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"640\" height=\"360\" viewBox=\"0 0 640 360\">\r\n  <!-- Исходное форматирование и комментарий должны сохраниться -->\r\n  <rect width=\"640\" height=\"360\" rx=\"24\" fill=\"#195ea6\"/>\r\n  <circle cx=\"470\" cy=\"145\" r=\"75\" fill=\"#8fe3df\"/>\r\n  <path d=\"M55 285 L210 80 L350 285 Z\" fill=\"#f9c66a\"/>\r\n</svg>\r\n";
    private const string CsvSource = "Имя,Описание,Значение\r\n\"Деталь, первая\",\"Две строки\r\nв одной ячейке\",42\r\nВторая,\"Кавычки \"\"внутри\"\"\",3.5\r\n";

    [STAThread]
    private static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        // Application's constructor schedules Startup even without Application.Run.
        // Unsubscribe before starting the Dispatcher so no tray/hooks/startup mutation runs.
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(app); }
            catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; }
            finally { app.Shutdown(); Dispatcher.ExitAllFrames(); }
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task RunAsync(App app)
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var screenshots = Path.Combine(TestDirectory, "screenshots");
        Directory.CreateDirectory(screenshots);
        var svg = Path.Combine(fixtureDirectory, "Изображение С ПРОБЕЛАМИ.SVG");
        var csv = Path.Combine(fixtureDirectory, "Исходная таблица.CSV");
        var step = Path.Combine(fixtureDirectory, "Деталь С ПРОБЕЛАМИ.STEP");
        var malformedStep = Path.Combine(fixtureDirectory, "Повреждённая деталь.STEP");
        var largeSvg = Path.Combine(fixtureDirectory, "Большой исходник A.svg");
        File.WriteAllText(svg, SvgSource, new UTF8Encoding(false));
        File.WriteAllText(csv, CsvSource, new UTF8Encoding(false));
        const string malformedSource = "ISO-10303-21;\r\nHEADER;\r\n/* readable source but deliberately missing shape data */\r\nENDSEC;\r\nDATA;\r\nBROKEN MODEL;\r\nENDSEC;\r\nEND-ISO-10303-21;\r\n";
        File.WriteAllText(malformedStep, malformedSource, new UTF8Encoding(false));
        File.WriteAllText(largeSvg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"><!-- FILE-A-ONLY " + new string('x', 4 * 1024 * 1024) + "--><rect width=\"100\" height=\"100\" fill=\"red\"/></svg>", new UTF8Encoding(false));
        var stepSource = File.ReadAllText(step);

        var themeType = typeof(App).Assembly.GetType("QuickPreview.ThemeService", true)!;
        using var theme = (IDisposable)Activator.CreateInstance(themeType, nonPublic: true)!;
        var window = new MainWindow();
        app.MainWindow = window;
        themeType.GetMethod("RegisterWindow")!.Invoke(theme, new object[] { window });
        Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", "light");
        themeType.GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(theme, null);
        CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
        var environmentTask = CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppContext.BaseDirectory, "WebView2-test"));
        Field("_webEnvironmentTask").SetValue(window, environmentTask);

        await ShowAsync(window, svg, "ImageView");
        Assert(Tabs(window).SelectedIndex == 0, "SVG opens Visual by default");
        var image = Control<Image>(window, "PreviewImage").Source;
        await SelectSourceAsync(window, SvgSource);
        SelectVisual(window);
        Assert(ReferenceEquals(image, Control<Image>(window, "PreviewImage").Source), "SVG visual cache survives Text → Visual");
        await ShowAsync(window, csv, "TableView");
        Assert(Tabs(window).SelectedIndex == 0, "CSV opens Visual by default");
        var rows = Control<ListView>(window, "TableRowsList").ItemsSource;
        Assert(Control<ListView>(window, "TableRowsList").Items.Count == 2, "CSV renders two data rows");
        await SelectSourceAsync(window, CsvSource);
        SelectVisual(window);
        Assert(ReferenceEquals(rows, Control<ListView>(window, "TableRowsList").ItemsSource), "CSV visual rows survive Text → Visual");

        var browser = Control<WebView2>(window, "ModelWebView");
        await browser.EnsureCoreWebView2Async(await environmentTask);
        var modelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Field("_modelInitializationTask").SetValue(window, modelGate.Task);
        window.ShowPreview(step, activateWindow: false);
        await WaitAsync(() => Tabs(window).IsEnabled, "STEP switcher while renderer is loading");
        await SelectSourceAsync(window, stepSource);
        Assert(Field("_modelSession").GetValue(window) is null, "STEP visual load is still gated");
        modelGate.SetResult();
        await WaitModelReadyAsync(window);
        Assert(Tabs(window).SelectedIndex == 1 && Control<Grid>(window, "SourceView").IsVisible,
            "Finishing asynchronous STEP render preserves selected Text tab");
        SelectVisual(window);
        await FinishLayoutAsync(window);
        var session = Field("_modelSession").GetValue(window);
        var navigations = 0;
        browser.CoreWebView2.NavigationStarting += (_, _) => navigations++;
        await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#viewport canvas').dispatchEvent(new WheelEvent('wheel',{deltaY:-120,bubbles:true,cancelable:true}));window.dualPreviewCacheProbe='still-here';");
        const string cameraScript = "(()=>{const c=window.quickPreviewModel.viewer.GetViewer().GetCamera();return [c.eye.x,c.eye.y,c.eye.z,c.center.x,c.center.y,c.center.z,c.up.x,c.up.y,c.up.z];})()";
        var camera = await browser.CoreWebView2.ExecuteScriptAsync(cameraScript);
        var navigationBaseline = navigations;
        await SelectSourceAsync(window, stepSource);
        SelectVisual(window);
        await FinishLayoutAsync(window);
        Assert(ReferenceEquals(session, Field("_modelSession").GetValue(window)), "STEP session survives Text → Visual");
        Assert(camera == await browser.CoreWebView2.ExecuteScriptAsync(cameraScript), "STEP camera survives Text → Visual");
        Assert(navigations == navigationBaseline && await browser.CoreWebView2.ExecuteScriptAsync("window.dualPreviewCacheProbe") == "\"still-here\"", "STEP tab switch does not navigate/reload browser");

        window.ShowPreview(malformedStep, activateWindow: false);
        await WaitAsync(() => Control<Grid>(window, "ErrorView").IsVisible, "malformed STEP visual error");
        Assert(Tabs(window).IsEnabled, "Text switch remains available after visual error");
        await SelectSourceAsync(window, malformedSource);
        Assert(Control<Grid>(window, "SourceView").IsVisible, "Failed visual still offers exact readable source");

        window.ShowPreview(largeSvg, activateWindow: false);
        await WaitAsync(() => Tabs(window).IsEnabled, "large SVG source eligibility");
        Tabs(window).SelectedIndex = 1;
        var previousLoad = Field("_sourceLoadTask").GetValue(window) as Task;
        Assert(previousLoad is { IsCompleted: false }, "File A raw read is pending before switching to B");
        window.ShowPreview(csv, activateWindow: false);
        await WaitAsync(() => Tabs(window).IsEnabled && Control<TextBlock>(window, "FileNameText").Text == Path.GetFileName(csv), "file B selection");
        Assert(Tabs(window).SelectedIndex == 0, "Changing files resets to Visual");
        await SelectSourceAsync(window, CsvSource);
        await previousLoad!.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(100);
        Assert(Control<TextBox>(window, "SourcePreviewText").Text == CsvSource, "Cancelled file A raw read cannot overwrite file B");

        await VerifySingleRepresentationAsync(window, fixtureDirectory);

        foreach (var themeName in new[] { "light", "dark" })
        {
            Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", themeName);
            themeType.GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(theme, null);
            foreach (var (path, view, label, source) in new[] { (svg,"ImageView","svg",SvgSource), (csv,"TableView","csv",CsvSource), (step,"ModelView","step",stepSource) })
            {
                await ShowAsync(window, path, view);
                if (label == "step") await WaitModelReadyAsync(window);
                Assert(Tabs(window).SelectedIndex == 0, label + " defaults to Visual for new file");
                foreach (var (width,height) in new[] { (1040d,700d), (520d,500d) })
                foreach (var scale in new[] { 1d,1.25d })
                {
                    window.Width=width; window.Height=height;
                    await FinishLayoutAsync(window);
                    using (ScaleText(window,scale))
                    {
                        await FinishLayoutAsync(window);
                        VerifyTabAlignment(window);
                        var suffix=$"{themeName}-{width:0}-{(scale==1?"100":"125")}";
                        Capture(window,Path.Combine(screenshots,$"{label}-visual-{suffix}.png"));
                        if (label=="step")
                        {
                            using var png=File.Create(Path.Combine(screenshots,$"step-canvas-{suffix}.png"));
                            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,png);
                        }
                        await SelectSourceAsync(window,source);
                        await FinishLayoutAsync(window);
                        VerifyTabAlignment(window);
                        var text=Control<TextBox>(window,"SourcePreviewText");
                        Assert(text.IsReadOnly && text.IsVisible && text.ActualHeight>100 && text.ActualWidth>400,
                            label + " source is readable in viewport");
                        Assert(!string.IsNullOrWhiteSpace(Control<TextBlock>(window,"SourceStatusText").Text), "Source status describes representation");
                        Capture(window,Path.Combine(screenshots,$"{label}-text-{suffix}.png"));
                        SelectVisual(window);
                        await FinishLayoutAsync(window);
                    }
                }
            }
        }
        window.HidePreview();
        Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", null);
        Console.WriteLine("PASS: Dual preview behavior and layout; screenshots: "+screenshots);
    }

    private static async Task VerifySingleRepresentationAsync(MainWindow window,string directory)
    {
        var plain=Path.Combine(directory,"plain.txt"); File.WriteAllText(plain,"Plain source is already the only preview.");
        await ShowAsync(window,plain,"TextView",expectTabs:false);
        Assert(Control<Border>(window,"RepresentationBar").IsVisible && !Tabs(window).IsEnabled,"Plain TXT keeps the single-representation switcher visible and disabled");
        var pngPath=Path.Combine(directory,"binary.png");
        var bitmap=BitmapSource.Create(2,2,96,96,PixelFormats.Bgr24,null,new byte[]{255,0,0,0,255,0,0,0,255,255,255,255},6);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(pngPath))encoder.Save(file);
        await ShowAsync(window,pngPath,"ImageView",expectTabs:false);
        Assert(Control<Border>(window,"RepresentationBar").IsVisible && !Tabs(window).IsEnabled,"Binary PNG keeps the switcher visible without offering text");
        var stl=Path.Combine(directory,"binary.stl");
        using(var file=File.Create(stl))using(var writer=new BinaryWriter(file))
        {
            writer.Write(new byte[80]);writer.Write(1u);
            foreach(var value in new[]{0f,0f,1f,0f,0f,0f,2f,0f,0f,0f,3f,0f})writer.Write(value);
            writer.Write((ushort)0);
        }
        await ShowAsync(window,stl,"ModelView",expectTabs:false);
        await WaitModelReadyAsync(window);
        Assert(Control<Border>(window,"RepresentationBar").IsVisible && !Tabs(window).IsEnabled,"Binary STL keeps the switcher visible without offering text");
    }

    private static async Task ShowAsync(MainWindow window,string path,string visualView,bool expectTabs=true)
    {
        window.ShowPreview(path,activateWindow:false);
        await WaitAsync(()=>Control<FrameworkElement>(window,visualView).IsVisible,"visual preview "+Path.GetExtension(path));
        if(expectTabs)await WaitAsync(()=>Tabs(window).IsEnabled,"Dual representation controls available");
        Assert(Control<Border>(window,"RepresentationBar").IsVisible,"Representation switcher always occupies the header");
    }

    private static async Task SelectSourceAsync(MainWindow window,string expected)
    {
        Tabs(window).SelectedIndex=1;
        await WaitAsync(()=>Control<Grid>(window,"SourceView").IsVisible && Control<TextBox>(window,"SourcePreviewText").Text==expected,"exact source text");
        Assert(Control<TextBox>(window,"SourcePreviewText").Text==expected,"Source text preserves exact formatting and line endings");
    }

    private static void SelectVisual(MainWindow window)=>Tabs(window).SelectedIndex=0;
    private static TabControl Tabs(MainWindow window)=>Control<TabControl>(window,"PreviewTabs");
    private static FieldInfo Field(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)??throw new MissingFieldException(name);
    private static T Control<T>(MainWindow window,string name)where T:FrameworkElement=>(T)(window.FindName(name)??throw new MissingMemberException(name));

    private static async Task WaitModelReadyAsync(MainWindow window)
    {
        await WaitAsync(()=>Field("_modelSession").GetValue(window)is not null,"model session attached");
        var session=Field("_modelSession").GetValue(window)!;
        var ready=(Task)session.GetType().GetProperty("Ready")!.GetValue(session)!;
        await ready.WaitAsync(TimeSpan.FromSeconds(45));
        await FinishLayoutAsync(window);
    }

    private static void VerifyTabAlignment(MainWindow window)
    {
        var visual=Control<TabItem>(window,"VisualPreviewTab");var source=Control<TabItem>(window,"SourcePreviewTab");
        var first=Bounds(visual,window);var second=Bounds(source,window);
        Assert(Math.Abs(first.Top-second.Top)<=1&&Math.Abs(first.Bottom-second.Bottom)<=1,"Visual/Text tabs share top and bottom bounds");
        Assert(Math.Abs(first.Width-second.Width)<=1,"Visual/Text segments have equal widths");
        Assert(first.Left>=0&&second.Right<=window.ActualWidth+1,"Visual/Text tabs fit supported window width");
        foreach(var tab in new[]{visual,source})
        foreach(var label in Descendants<TextBlock>(tab).Where(text=>text.IsVisible))
        {
            var bounds=Bounds(label,tab);
            Assert(bounds.Left>=-1&&bounds.Top>=-1&&bounds.Right<=tab.ActualWidth+1&&bounds.Bottom<=tab.ActualHeight+1,"Tab label fits control");
        }
    }

    private static async Task WaitAsync(Func<bool> condition,string operation)
    {
        var watch=Stopwatch.StartNew();
        while(!condition())
        {
            if(watch.Elapsed>TimeSpan.FromSeconds(20))throw new TimeoutException(operation);
            await Task.Delay(15);
        }
    }

    private static async Task FinishLayoutAsync(MainWindow window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        await Task.Delay(60);
    }

    private static IDisposable ScaleText(MainWindow window,double factor)
    {
        var values=Descendants<FrameworkElement>((DependencyObject)window.Content).Select(element=>element switch
        {
            TextBlock text=>(Element:(DependencyObject)text,Property:TextBlock.FontSizeProperty,Size:text.FontSize),
            System.Windows.Controls.Control control=>(Element:(DependencyObject)control,Property:System.Windows.Controls.Control.FontSizeProperty,Size:control.FontSize),_=>default
        }).Where(value=>value.Element is not null).ToArray();
        foreach(var value in values)value.Element.SetCurrentValue(value.Property,value.Size*factor);
        return new RestoreScope(values.Select(value=>new Action(()=>value.Element.SetCurrentValue(value.Property,value.Size))).ToArray());
    }

    private sealed class RestoreScope:IDisposable
    {
        private readonly Action[] _actions;public RestoreScope(Action[] actions)=>_actions=actions;
        public void Dispose(){foreach(var action in _actions)action();}
    }

    private static Rect Bounds(FrameworkElement element,Visual ancestor)=>element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent)where T:DependencyObject
    {
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)
        {
            var child=VisualTreeHelper.GetChild(parent,index);if(child is T match)yield return match;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }

    private static void Capture(MainWindow window,string path)
    {
        var content=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(path);encoder.Save(file);
    }

    private static void Assert(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
}
