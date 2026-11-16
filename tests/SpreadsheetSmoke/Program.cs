using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;

internal static class Program
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
    private static readonly List<string> Checks = new(), Errors = new();
    private static readonly Type Service = typeof(App).Assembly.GetType("QuickPreview.SpreadsheetPreviewService", true)!;
    private static MainWindow? _window;
    private static WebView2? _browser;
    private static int _exit;

    [STAThread]
    private static int Main(string[] arguments)
    {
        var app = new App(); app.InitializeComponent();
        var startup = typeof(App).GetMethod("App_OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        app.DispatcherUnhandledException += (_, e) => { Errors.Add(e.Exception.ToString()); e.Handled = true; _exit = 1; };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var watch = Stopwatch.StartNew();
            try { await RunAsync(app, arguments); }
            catch (Exception error) { Console.Error.WriteLine(error); Errors.Add(error.ToString()); _exit = 1; }
            finally
            {
                _window?.HidePreview();
                if (_window is not null)
                {
                    typeof(MainWindow).GetMethod("StopOriginLifetimeMonitoring", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, null);
                    foreach (var name in new[] { "SpreadsheetWebView", "ModelWebView", "PdfWebView" })
                        (_window.FindName(name) as WebView2)?.Dispose();
                }
                Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", null);
                Directory.CreateDirectory(Path.Combine(Root, "artifacts"));
                File.WriteAllText(Path.Combine(Root, "artifacts", "result.json"), JsonSerializer.Serialize(new { Passed = _exit == 0, Checks, Errors, Seconds = watch.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Spreadsheet result: {Checks.Count} passed checks; errors={Errors.Count}; {watch.Elapsed.TotalSeconds:F1}s.");
                app.Shutdown(); Dispatcher.ExitAllFrames();
            }
        }));
        Dispatcher.Run(); return _exit;
    }

    private static async Task RunAsync(App app, string[] arguments)
    {
        var artifacts = Path.Combine(Root, "artifacts"); Directory.CreateDirectory(artifacts);
        var fixtures = Path.Combine(artifacts, "fixtures"); Directory.CreateDirectory(fixtures);
        var workbook = FixtureWorkbook(Path.Combine(fixtures, "Original report.xlsx"));
        var parsed = await LoadAsync(workbook);
        var sheets = parsed.GetProperty("Sheets");
        Check(sheets.GetArrayLength() == 3, "XLSX retains three named worksheets.");
        var first = sheets[0];
        Check(first.GetProperty("Name").GetString() == "Отчет", "Workbook sheet order and names are retained.");
        Check(first.GetProperty("Merges").EnumerateArray().Any(m => m.GetProperty("Row").GetInt32() == 0 && m.GetProperty("ColumnSpan").GetInt32() == 6), "Merged title keeps its six-column span.");
        Check(first.GetProperty("Rows").EnumerateArray().Any(r => r.GetProperty("Index").GetInt32() == 41), "Sparse A42 keeps its original row coordinate.");
        Check(first.GetProperty("Rows").EnumerateArray().Any(r => r.GetProperty("Index").GetInt32() == 4 && r.GetProperty("Hidden").GetBoolean()), "Hidden row metadata is retained.");
        Check(first.GetProperty("Columns").EnumerateArray().Any(c => c.GetProperty("Index").GetInt32() == 6 && c.GetProperty("Hidden").GetBoolean()), "Hidden column metadata is retained.");
        Check(Cell(sheets[1], 1, 1).GetProperty("Text").GetString() == "0", "Cached formula zero is displayed as a value.");
        Check(Cell(sheets[1], 1, 2).GetProperty("Text").GetString() == "", "A cached empty-string formula remains blank.");
        Check(Cell(sheets[1], 2, 0).GetProperty("Text").GetString()!.StartsWith("="), "Formula without cached value stays unevaluated.");
        Check(Cell(first, 3, 4).GetProperty("Text").GetString()!.Contains("2023"), "Excel date serial is formatted as a readable date.");
        Check(Cell(first, 3, 5).GetProperty("Text").GetString()!.Contains("%"), "Percent cell uses its number format.");
        var titleStyle = parsed.GetProperty("Styles")[Cell(first, 0, 0).GetProperty("StyleIndex").GetInt32()];
        Check(titleStyle.GetProperty("Bold").GetBoolean(), "Original bold font styling is retained.");
        var headerStyle = parsed.GetProperty("Styles")[Cell(first, 2, 0).GetProperty("StyleIndex").GetInt32()];
        Check(headerStyle.GetProperty("Background").GetString() is { Length: > 0 } && headerStyle.GetProperty("BorderBottom").GetString() != "none", "Cell fill and borders are represented structurally.");
        await ExpectAsync<OperationCanceledException>(() => LoadAsync(workbook, new CancellationToken(true)), "Spreadsheet parsing honors cancellation.");
        var malicious = WritePackage(Path.Combine(fixtures, "external-entity.xlsx"), new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = "<!DOCTYPE workbook [<!ENTITY external SYSTEM 'file:///C:/Windows/win.ini'>]><workbook><sheets>&external;</sheets></workbook>"
        });
        await ExpectAsync<System.Xml.XmlException>(() => LoadAsync(malicious), "XML external entities are rejected.");
        var legacy = await LoadAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy.xls"));
        Check(legacy.GetProperty("Sheets").GetArrayLength() > 0 && legacy.GetProperty("Sheets").EnumerateArray().Any(s => s.GetProperty("Rows").GetArrayLength() > 0), "Real BIFF XLS is parsed without launching installed Office.");
        var ods = WritePackage(Path.Combine(fixtures, "Bounded repeated tail.ods"), new Dictionary<string, string>
        {
            ["content.xml"] = "<office:document-content xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' xmlns:table='urn:oasis:names:tc:opendocument:xmlns:table:1.0' xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'><office:body><office:spreadsheet><table:table table:name='Original ODS'><table:table-row><table:table-cell table:number-columns-repeated='2'><text:p>Repeated value</text:p></table:table-cell></table:table-row><table:table-row table:number-rows-repeated='1048575'><table:table-cell table:number-columns-repeated='16384'/></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>"
        });
        var odsData = await LoadAsync(ods);
        Check(odsData.GetProperty("Sheets")[0].GetProperty("Rows").GetArrayLength() == 1 && Cell(odsData.GetProperty("Sheets")[0], 0, 1).GetProperty("Text").GetString() == "Repeated value", "ODS repeated cells preserve values without expanding a million-row empty tail.");
        var epoch = WritePackage(Path.Combine(fixtures, "1904 epoch.xlsx"), new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = "<workbook xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><workbookPr date1904='1'/><sheets><sheet name='Epoch' r:id='sheet'/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships><Relationship Id='sheet' Target='worksheets/sheet1.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet'/><Relationship Id='style' Target='styles.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles'/></Relationships>",
            ["xl/styles.xml"] = "<styleSheet><cellXfs><xf numFmtId='14'/></cellXfs></styleSheet>",
            ["xl/worksheets/sheet1.xml"] = "<worksheet><sheetData><row r='1'><c r='A1'><v>0</v></c></row></sheetData></worksheet>"
        });
        Check(Cell((await LoadAsync(epoch)).GetProperty("Sheets")[0], 0, 0).GetProperty("Text").GetString() == "01.01.1904", "1904 workbook date epoch formats serial zero correctly.");
        if (arguments.Contains("--source-only")) return;

        CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
        _window = new MainWindow(); app.MainWindow = _window;
        _browser = Control<WebView2>("SpreadsheetWebView");
        var profile = Path.Combine(artifacts, "profiles", Guid.NewGuid().ToString("N"));
        Field("_webEnvironmentTask").SetValue(_window, CoreWebView2Environment.CreateAsync(userDataFolder: profile));
        var themeType = typeof(App).Assembly.GetType("QuickPreview.ThemeService", true)!;
        using var theme = (IDisposable)Activator.CreateInstance(themeType, nonPublic: true)!;
        themeType.GetMethod("RegisterWindow")!.Invoke(theme, new object[] { _window });
        var screenshots = Path.Combine(artifacts, "screenshots"); Directory.CreateDirectory(screenshots);

        foreach (var themeName in new[] { "light", "dark" })
        {
            Environment.SetEnvironmentVariable("QUICKPREVIEW_TEST_THEME", themeName);
            themeType.GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(theme, null);
            await ShowAsync(workbook);
            Check(await ScriptBoolAsync("document.querySelectorAll('#sheet-tabs button[role=tab]').length===3"), "Viewer exposes all worksheet tabs: " + themeName);
            Check(await ScriptBoolAsync("document.querySelector('td[data-reference=\"A1\"]').colSpan===6"), "Merged title is rendered as a merged cell: " + themeName);
            Check(await ScriptBoolAsync("(()=>{const e=document.querySelector('td[data-reference=\"A3\"]'),s=getComputedStyle(e);return s.borderBottomStyle!=='none'&&s.backgroundColor!=='rgba(0, 0, 0, 0)'})()"), "Cell border and fill are actually rendered: " + themeName);
            Check(await ScriptBoolAsync("document.querySelector('td[data-reference=\"A42\"]').textContent.includes('Sparse A42')"), "Sparse row remains addressable at A42 in the rendered table.");
            Check(await ScriptBoolAsync("(()=>{const e=document.querySelector('td[data-reference=\"A5\"]');return !e||e.getClientRects().length===0})()"), "Hidden rows are excluded from visible layout.");
            Check(await ScriptBoolAsync("(()=>{const e=document.querySelector('td[data-reference=\"G4\"]');return !e||e.getClientRects().length===0})()"), "Hidden columns are excluded from visible layout.");
            foreach (var width in new[] { 1040d, 520d })
            foreach (var scale in new[] { 1d, 1.25d })
            {
                _window.Width = width; _window.Height = 720; _window.UpdateLayout();
                await _browser.CoreWebView2.ExecuteScriptAsync($"document.documentElement.style.zoom='{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}';window.quickPreviewSpreadsheet.selectSheet(0)");
                await SettleAsync();
                using var measure = JsonDocument.Parse(await _browser.CoreWebView2.ExecuteScriptAsync(LayoutMeasurements));
                var value = measure.RootElement;
                Check(value.GetProperty("toolbarFits").GetBoolean(), $"Toolbar stays in the viewport: {themeName} {width} {scale}.");
                Check(value.GetProperty("controlsAligned").GetBoolean(), $"Spreadsheet toolbar controls align: {themeName} {width} {scale}.");
                Check(value.GetProperty("cellsDoNotOverlap").GetBoolean(), $"Distinct table cells do not overlap: {themeName} {width} {scale}.");
                var suffix = $"{themeName}-{width:0}-{scale * 100:0}";
                File.WriteAllText(Path.Combine(screenshots, suffix + ".json"), measure.RootElement.GetRawText());
                using var image = File.Create(Path.Combine(screenshots, suffix + ".png"));
                await _browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            await _browser.CoreWebView2.ExecuteScriptAsync("document.documentElement.style.zoom='1';document.querySelectorAll('#sheet-tabs button[role=tab]')[1].click()");
            await SettleAsync();
            Check(await ScriptBoolAsync("window.quickPreviewSpreadsheet.sheetIndex===1&&document.querySelector('td[data-reference=\"A2\"]').textContent.includes('<img')&&document.querySelectorAll('#sheet-table img').length===0&&!window.injected"), "Worksheet switching preserves markup as text without executing it.");
            await _browser.CoreWebView2.ExecuteScriptAsync("window.quickPreviewSpreadsheet.setZoom(1.25)");
            Check(await ScriptBoolAsync("Math.abs(window.quickPreviewSpreadsheet.zoom-1.25)<.001"), "Explicit spreadsheet zoom control works.");
            await _browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('fit').click()");
            await SettleAsync();
            Check(await ScriptBoolAsync("window.quickPreviewSpreadsheet.zoom<=1&&window.quickPreviewSpreadsheet.zoom>=.39"), "Fit uses a bounded readable spreadsheet scale.");
            await _browser.CoreWebView2.ExecuteScriptAsync("fetch('https://example.invalid/never-send-workbook').then(()=>window.externalBlocked=false).catch(()=>window.externalBlocked=true)");
            await WaitScriptAsync("window.externalBlocked===true", "External spreadsheet requests are blocked.");
        }

        var beforeHide = Session()!;
        _window.WindowState = WindowState.Minimized;
        await WaitAsync(() => _browser.CoreWebView2.IsSuspended, "Minimized spreadsheet browser suspends.");
        _window.WindowState = WindowState.Normal;
        await WaitAsync(() => !_browser.CoreWebView2.IsSuspended, "Restored spreadsheet browser resumes.");
        Check(ReferenceEquals(beforeHide, Session()), "Minimize/restore preserves the loaded spreadsheet session.");
        var plain = Path.Combine(fixtures, "Plain.txt"); File.WriteAllText(plain, "Plain text selection after spreadsheet.");
        _window.ShowPreview(plain, activateWindow: false);
        await WaitAsync(() => Control<Grid>("TextView").IsVisible && Session() is null && _browser.CoreWebView2.IsSuspended, "Switching to text releases and suspends spreadsheet preview.");
        Check(IsDisposed(beforeHide), "Leaving a spreadsheet disposes its old session.");
        // A→B→A within the normal coalescing interval must not let old workbook
        // navigation or asynchronous load completion overwrite the last A.
        Update(workbook); await Task.Delay(20); Update(plain); await Task.Delay(20); Update(workbook);
        await ReadyAsync(workbook); await Task.Delay(300);
        Check(Control<Grid>("SpreadsheetView").IsVisible && (string?)Field("_currentPath").GetValue(_window) == workbook && await ScriptBoolAsync("window.quickPreviewSpreadsheet.workbook.sheets.length===3"), "Rapid spreadsheet/text ABA finishes on the selected workbook.");
        var hidden = Session()!; _window.HidePreview();
        await WaitAsync(() => _browser.CoreWebView2.IsSuspended, "Hidden spreadsheet browser suspends.");
        Check(Session() is null && IsDisposed(hidden), "Hiding preview disposes the spreadsheet session.");

        var externalIndex = Array.IndexOf(arguments, "--workbook");
        if (externalIndex >= 0)
        {
            if (externalIndex + 1 >= arguments.Length) throw new ArgumentException("--workbook requires a local workbook path.");
            var path = Path.GetFullPath(arguments[externalIndex + 1]);
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            await ShowAsync(path);
            using var actual = File.Create(Path.Combine(screenshots, "external-workbook.png"));
            await _browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, actual);
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "User workbook remains byte-for-byte unchanged.");
            Check(await ScriptBoolAsync("document.querySelectorAll('#sheet-table td').length>0"), "User workbook renders actual table cells.");
        }
        Check(Errors.Count == 0, "No spreadsheet dispatcher errors occurred.");
    }

    private const string LayoutMeasurements = @"(()=>{
      const buttons=[...document.querySelectorAll('#zoom-in,#zoom-out,#zoom-reset,#fit')].map(e=>e.getBoundingClientRect()), scroll=document.getElementById('sheet-scroll').getBoundingClientRect(),cells=[...document.querySelectorAll('#sheet-table td')].filter(e=>e.getClientRects().length>0).map(e=>e.getBoundingClientRect());
      let overlap=false;for(let i=0;i<cells.length&&!overlap;i++)for(let j=i+1;j<cells.length;j++){const a=cells[i],b=cells[j];if(Math.min(a.right,b.right)-Math.max(a.left,b.left)>.5&&Math.min(a.bottom,b.bottom)-Math.max(a.top,b.top)>.5){overlap=true;break;}}
      return{toolbarFits:buttons.every(r=>r.left>=-1&&r.right<=innerWidth+1&&r.top>=-1&&r.bottom<=scroll.top+1),controlsAligned:buttons.every(r=>Math.abs(r.top-buttons[0].top)<=1&&Math.abs(r.height-buttons[0].height)<=1),cellsDoNotOverlap:!overlap,cells:cells.length,buttons:buttons.map(r=>({x:r.x,y:r.y,width:r.width,height:r.height})),viewport:{width:innerWidth,height:innerHeight}};
    })()";

    private static async Task<JsonElement> LoadAsync(string path, CancellationToken token = default)
    {
        Task task;
        try { task = (Task)Service.GetMethod("LoadAsync")!.Invoke(null, new object[] { path, token })!; }
        catch (TargetInvocationException e) when (e.InnerException is { } inner) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
        await task;
        return JsonSerializer.SerializeToElement(task.GetType().GetProperty("Result")!.GetValue(task));
    }
    private static JsonElement Cell(JsonElement sheet, int row, int column) => sheet.GetProperty("Rows").EnumerateArray().First(r => r.GetProperty("Index").GetInt32() == row).GetProperty("Cells").EnumerateArray().First(c => c.GetProperty("Column").GetInt32() == column);
    private static async Task ShowAsync(string path) { _window!.ShowPreview(path, activateWindow: false); await ReadyAsync(path); }
    private static async Task ReadyAsync(string path)
    {
        await WaitAsync(() => Session() is not null && !(bool)Field("_spreadsheetLoading").GetValue(_window)! && Control<Grid>("SpreadsheetView").IsVisible && (string?)Field("_currentPath").GetValue(_window) == path, "Spreadsheet becomes ready for " + Path.GetFileName(path));
        await ((Task)Session()!.GetType().GetProperty("Ready")!.GetValue(Session())!).WaitAsync(TimeSpan.FromSeconds(30));
        await WaitScriptAsync("!!window.quickPreviewSpreadsheet", "Spreadsheet script is ready.");
        await SettleAsync();
    }
    private static void Update(string path) => typeof(MainWindow).GetMethod("UpdatePreviewFromExplorer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, new object[] { path });
    private static object? Session() => Field("_spreadsheetSession").GetValue(_window);
    private static bool IsDisposed(object value) => Convert.ToBoolean(value.GetType().GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value));
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Control<T>(string name) where T : FrameworkElement => (T)_window!.FindName(name);
    private static async Task<bool> ScriptBoolAsync(string code) => await _browser!.CoreWebView2.ExecuteScriptAsync(code) == "true";
    private static async Task WaitScriptAsync(string code, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!await ScriptBoolAsync(code)) { if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(description); await Task.Delay(20); }
        Check(true, description);
    }
    private static async Task SettleAsync() { _window!.UpdateLayout(); await _window.Dispatcher.InvokeAsync(_window.UpdateLayout, DispatcherPriority.ApplicationIdle); await Task.Delay(100); }
    private static async Task WaitAsync(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (_window is not null && Control<Grid>("ErrorView").IsVisible) throw new Exception(description + ": " + Control<TextBlock>("ErrorText").Text);
            if (watch.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException(description);
            await Task.Delay(20);
        }
        Check(true, description);
    }
    private static async Task ExpectAsync<T>(Func<Task<JsonElement>> action, string description) where T : Exception
    {
        try { await action(); } catch (T) { Check(true, description); return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + description);
    }
    private static void Check(bool condition, string description) { if (!condition) throw new Exception("FAIL: " + description); Checks.Add(description); Console.WriteLine("PASS: " + description); }

    private static string FixtureWorkbook(string path) => WritePackage(path, new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/></Types>",
        ["_rels/.rels"] = "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='main' Target='xl/workbook.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument'/></Relationships>",
        ["xl/workbook.xml"] = "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Отчет' sheetId='1' r:id='one'/><sheet name='Детали' sheetId='2' r:id='two'/><sheet name='Третий лист с длинным названием' sheetId='3' r:id='three'/></sheets></workbook>",
        ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='one' Target='worksheets/sheet1.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet'/><Relationship Id='two' Target='worksheets/sheet2.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet'/><Relationship Id='three' Target='worksheets/sheet3.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet'/><Relationship Id='style' Target='styles.xml' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles'/></Relationships>",
        ["xl/styles.xml"] = "<styleSheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><fonts count='2'><font><sz val='11'/><name val='Calibri'/></font><font><b/><sz val='14'/><name val='Calibri'/></font></fonts><fills count='3'><fill><patternFill patternType='none'/></fill><fill><patternFill patternType='gray125'/></fill><fill><patternFill patternType='solid'><fgColor rgb='FFDBEAFE'/></patternFill></fill></fills><borders count='2'><border/><border><left style='thin'><color rgb='FF64748B'/></left><right style='thin'><color rgb='FF64748B'/></right><top style='thin'><color rgb='FF64748B'/></top><bottom style='thin'><color rgb='FF64748B'/></bottom></border></borders><cellXfs count='6'><xf numFmtId='0' fontId='0' fillId='0' borderId='0'/><xf numFmtId='0' fontId='1' fillId='2' borderId='1'><alignment horizontal='center' vertical='center' wrapText='1'/></xf><xf numFmtId='0' fontId='1' fillId='2' borderId='1'><alignment horizontal='center' wrapText='1'/></xf><xf numFmtId='4' fontId='0' fillId='0' borderId='1'><alignment horizontal='right'/></xf><xf numFmtId='14' fontId='0' fillId='0' borderId='1'/><xf numFmtId='10' fontId='0' fillId='0' borderId='1'/></cellXfs></styleSheet>",
        ["xl/worksheets/sheet1.xml"] = "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetFormatPr defaultRowHeight='18'/><cols><col min='1' max='1' width='36'/><col min='2' max='6' width='16'/><col min='7' max='7' width='16' hidden='1'/></cols><sheetData><row r='1' ht='36' customHeight='1'>" + Inline("A1", "Отчет по продажам", 1) + "</row><row r='3' ht='32' customHeight='1'>" + Inline("A3", "Товар", 2) + Inline("B3", "Кол-во", 2) + Inline("C3", "Цена", 2) + Inline("D3", "Сумма", 2) + Inline("E3", "Дата", 2) + Inline("F3", "Доля", 2) + "</row><row r='4' ht='30' customHeight='1'>" + Inline("A4", "Оригинальный товар с длинным названием для проверки границ ячейки") + "<c r='B4'><v>3</v></c><c r='C4' s='3'><v>12345.67</v></c><c r='D4' s='3'><f>B4*C4</f><v>37037.01</v></c><c r='E4' s='4'><v>45000</v></c><c r='F4' s='5'><v>0.125</v></c>" + Inline("G4", "Hidden column") + "</row><row r='5' hidden='1'>" + Inline("A5", "Hidden row") + "</row><row r='42'>" + Inline("A42", "Sparse A42") + "</row></sheetData><mergeCells count='1'><mergeCell ref='A1:F1'/></mergeCells></worksheet>",
        ["xl/worksheets/sheet2.xml"] = "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><cols><col min='1' max='1' width='48'/><col min='2' max='2' width='16'/></cols><sheetData><row r='1'>" + Inline("A1", "Детали", 1) + "</row><row r='2'>" + Inline("A2", "<img src='https://example.invalid/probe' onerror='window.injected=true'>") + "<c r='B2'><f>1-1</f><v>0</v></c><c r='C2' t='str'><f>IF(1=1,&quot;&quot;,&quot;Wrong&quot;)</f><v/></c></row><row r='3'><c r='A3'><f>HYPERLINK(&quot;https://example.invalid/&quot;,&quot;Never execute&quot;)</f></c></row></sheetData><mergeCells><mergeCell ref='A1:B1'/></mergeCells></worksheet>",
        ["xl/worksheets/sheet3.xml"] = "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'>" + Inline("A1", "Original third worksheet") + "</row></sheetData></worksheet>"
    });
    private static string Inline(string reference, string text, int style = 0) => $"<c r='{reference}' t='inlineStr' s='{style}'><is><t>{System.Security.SecurityElement.Escape(text)}</t></is></c>";
    private static string WritePackage(string path, IReadOnlyDictionary<string, string> parts)
    {
        using var stream = File.Create(path); using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in parts) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content); }
        return path;
    }
}
