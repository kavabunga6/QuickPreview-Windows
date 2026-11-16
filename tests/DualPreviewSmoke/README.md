# Visual/Text preview regression checks

Requires Windows 10/11, .NET 6 SDK, Edge WebView2, and an interactive desktop.
A restricted sandbox can prevent the WebView2 graphics process from starting.

```powershell
dotnet run --project tests/DualPreviewSmoke/DualPreviewSmoke.csproj -c Release
```

Uses the real WPF MainWindow and original SVG, CSV, binary STL, PNG, TXT and STEP
fixtures. The committed STEP is the original generated six-plane AP203 cube from
ModelSmoke, named with Cyrillic text, spaces, and an uppercase extension.

Checks default Visual selection, exact unformatted source, image/table caches,
actual STEP rendering, preserved STEP session/camera/document across tab switches,
switching to source before an asynchronous model finishes, cancelled stale raw
reads after selecting another file, single representation for binary/plain files,
and readable source after a visual error.

The layout matrix covers light/dark themes, 1000×700 and 520×500 windows, and
100%/125% text scaling. Tab bounds and label containment are measured. WPF PNG
captures go to ignored `screenshots/`; separate WebView2 captures show the actual
3D canvas. WPF cannot capture a native WebView2 surface into its bitmap, so STEP
outer-UI captures verify the header/tabs and pair with `step-canvas-*.png`.

The production Startup handler is unsubscribed before starting the Dispatcher.
The test never installs global hooks, creates a tray icon, changes startup
settings, or closes another QuickPreview process. All runtime fixtures and the
isolated browser profile live under ignored `bin/`.
