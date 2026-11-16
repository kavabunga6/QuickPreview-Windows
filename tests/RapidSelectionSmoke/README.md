# Rapid Explorer selection regression

Requires Windows 10/11, .NET 6 SDK, Edge WebView2, and an interactive desktop.
Run outside the restricted sandbox so Shell COM, native foreground APIs, and the
WebView2 graphics process can work normally.

```powershell
dotnet run --project tests/RapidSelectionSmoke/RapidSelectionSmoke.csproj -c Release
```

Starts its own fixture Explorer with 21 mixed PDF, ZIP, 7Z, STEP, STL, 3MF, and OBJ
files. Production `TryNavigateInOriginWindow` and the production origin poll are
called at nominal 20, 50, and 120 ms spacing. Each burst stops on PDF, archive, or
3D targets and verifies the latest native Explorer selection, header, actual
archive sentinel, successful PDF navigation, and model session/document/triangle
identity. The checks repeat after old asynchronous work has had time to settle.
Actual browser captures verify non-background model pixels and projected model
bounds, including hidden-WebView startup becoming visible with a nonzero canvas.

The first opening uses production `App.TogglePreview` while Explorer is foreground;
the test never manually raises or activates its preview. It also checks reopening
after Hide/ForgetOrigin. Both must appear above Explorer immediately at Loaded and
after a frame/poll, preserve Explorer foreground, and leave topmost mode disabled.
After those opening assertions, the test recreates only the production 120ms
Dispatcher timer. Per-arrow poll requests may be coalesced or idle-throttled;
this timer supplies the trailing selection read after a burst stops. Native wake
hooks remain disabled with startup, so no global hooks are installed by the test.

During rapid navigation, native HWND, visibility, Z-order, foreground, topmost flag,
and exact position/size are checked. WM_SHOWWINDOW and WPF visibility events detect
hide/show flicker. LoadingView entries and navigation/poll timing are logged as
diagnostics. Preview startup/hooks/tray are disabled, the browser profile is
isolated under ignored bin output, and only a newly created fixture Explorer HWND
can be closed by cleanup. No user keystrokes or installed processes are touched.

PDF/STEP seeds are original fixtures from the existing test suite. ZIP/7Z archives
have unique per-file sentinels. The STL tetrahedron, two-triangle OBJ, and 3MF cube
are generated originals. Model PNG captures are ignored under `artifacts/`.

For a separate real-keyboard check against an installed tray app:

```powershell
dotnet run --project tests/RapidSelectionSmoke/RapidSelectionSmoke.csproj -c Release -- --keyboard-fixture
```

This mode opens only its dedicated Explorer and reports the folder/title/HWND and
a unique stop-file path. It installs no test preview/hook, so Space and arrows can
be exercised against the installed application using normal desktop input. It
holds for up to180seconds, or until the reported stop file is created, then safely
closes only the Explorer HWND it owns.
