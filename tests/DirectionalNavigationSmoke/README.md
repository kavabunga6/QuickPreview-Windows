# Explorer directional navigation regression

Run on the interactive Windows desktop:

```powershell
dotnet run --project tests/DirectionalNavigationSmoke/DirectionalNavigationSmoke.csproj -c Release
```

The harness opens a dedicated new Explorer window containing 40 original text
fixtures. It closes only that owned Explorer HWND and its own WPF windows. It
constructs the production App with its startup handler removed, so it creates no
tray icon, global keyboard hooks or startup settings. Installed QuickPreview
processes are untouched. No global keyboard messages or SendInput are used.

The expected targets come from Explorer's actual UI Automation grid coordinates,
group containers and screen bounds, resolved against the native Shell view's
file identities. The production test path calls MainWindow.Navigate and waits
for its serial navigation task, native Explorer selection, preview header and
exact original text body. Every move checks native foreground, relative Z order,
non-topmost style and unchanged preview bounds.

Coverage includes differently sized icon grids and window widths, Explorer's
32px-to-small-icon coercion, List column layouts at two heights, Tiles,
Details/Content horizontal clamping, name/date sorting, actual date grouping and
visible cross-group movement, offscreen scrolling, first/last boundaries, ordered
rapid queues, hide/reopen generation invalidation and changed-foreground
cancellation. The test verifies real Down and Right select different files in
multi-column grids and lists, and resize changes the icon row stride.

Optional diagnostics are retained as `--probe-native`, `--probe-grouped` and
`--probe-accelerator`. These use only the dedicated fixture. The accelerator
diagnostic tests targeted Shell COM and bounded messages to that window; they do
not send global input. Modern Explorer on the tested machine returns E_NOTIMPL
for IFolderView.GetItemPosition and no useful MSAA/TranslateAccelerator arrow
navigation, so the default regression uses the actual accessibility layout.
