# Origin Explorer lifetime regression

The default regression runs real Explorer folder navigation/unrelated-window
close/origin-window close with loaded 3D cleanup, then a native two-host protocol
matrix. It verifies inactive tab switching, neighbor destruction, fallback
detection, stale queued callbacks, event time/thread/object filters, native
PID/TID/class/parent identity checks and literal previews without an origin.
Foreground must remain unchanged when the origin closes.

The optional native probe uses only a newly owned Explorer window and original folders.
It records ShellWindows entry identities, IShellBrowser host HWNDs, view parent
chains and native window destroy events in `artifacts/native-probe.json`.

```powershell
dotnet run --project tests/ExplorerLifetimeSmoke/ExplorerLifetimeSmoke.csproj -c Release
dotnet run --project tests/ExplorerLifetimeSmoke/ExplorerLifetimeSmoke.csproj -c Release -- --probe
```

On the tested Windows10 build19045, IShellBrowser.GetWindow returns a persistent
`ShellTabWindowClass` child under `CabinetWClass`. Normal folder navigation
destroys/recreates `SHELLDLL_DefView` and its controls but preserves that host.
The ShellWindows IWebBrowser canonical IUnknown remains stable; the IShellBrowser
service object's IUnknown changes even between identical snapshots. Bindings
therefore use the native host identity rather than the temporary service object
or view identity.

Native whole-window close emits captured host and frame EVENT_OBJECT_DESTROY
events. IsWindow can still return true inside a frame destroy callback, so a
callback must not discard that notification solely on the immediate IsWindow
value. The recorded hierarchy and close events come from real Explorer.

This machine does not expose real Windows11 multi-tab Explorer. An initial
Navigate2 navOpenInNewTab attempt navigated the same host. That optional attempt
is now explicit `--try-tab`; default runs do not repeatedly try unsupported tab
creation. `--hold` keeps only the owned window up to120seconds for supported
manual desktop tab actions, printing its HWND and unique stop-file path.

The native two-child-host fixture covers the tab identity/lifetime protocol
without claiming Windows11 shell-UI coverage. Only its own native windows can be
destroyed. Production startup, tray, hooks and installed processes are untouched.
