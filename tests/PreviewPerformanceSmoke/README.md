# Preview performance smoke benchmark

The harness references a frozen application payload rather than rebuilding the
main project. This allows the immutable1.3.4 baseline to remain valid while
production source changes. The before payload and SHA256 manifest are in
`artifacts/baseline-app` and `artifacts/baseline-manifest.json`. Every report also
records the exact application DLL and viewer script hashes used at runtime.

On an interactive Windows desktop:

```powershell
dotnet run --project tests/PreviewPerformanceSmoke/PreviewPerformanceSmoke.csproj -c Release -- before
dotnet run --project tests/PreviewPerformanceSmoke/PreviewPerformanceSmoke.csproj -c Release -p:AppPayloadDir="C:/absolute/path/to/QuickPreview/bin/Release/net6.0-windows10.0.17763.0" -- after
```

Each run takes about40seconds after fixture preparation, with bounded model
timeouts and a total phase budget. Use desktop-capable execution for WebView2.
It opens and closes only a dedicated new Explorer HWND and its own preview.
The production startup handler is removed before Dispatcher startup: no installed
processes, tray icons, keyboard hooks or startup settings are touched. A hidden
MainWindow is created for the initial no-preview idle case. Tray-icon overhead
itself is outside this isolated test.

The original deterministic fixtures contain12-triangle STEP/STL cubes and real
200,000/1,000,000 triangle STL/OBJ meshes. They are generated under
`artifacts/fixtures`, outside measured time. The isolated WebView2 environment is
created before the first load phase, so timings are model-load comparisons and
do not represent total tray-to-first-preview startup time. Scenarios cover first STEP load,
ready idle, a dedicated actual production Explorer-selection poll at120ms ticks,
Text tab, minimized/hidden states,200k loads and page-local orbit interaction,
and1M imports canceled by replacement or hide. The1M ActionNote records whether
a budget rejected the import before cancellation, so rejection is not reported
as successful full rendering.

CPU units are **CPU core milliseconds per second**, with1000meaning one whole
logical core. CPU uses boundary snapshots; expensive Process private-memory
queries occur outside the timed app CPU interval. The earlier high-frequency
memory-sampling calibration is retained as `observer-calibration.json` and must
not be used as baseline CPU evidence. Memory is endpoint private bytes in MiB,
not a continuously sampled peak. App CPU includes the small common test-probe
overhead. Browser IDs come exclusively from the unique benchmark WebView2
environment's GetProcessInfos; unrelated Edge/WebView processes are excluded.

WPF responsiveness uses Background-priority Dispatcher probes about every40ms.
Explorer health uses bounded read-only WM_NULL roundtrips to only the owned
Explorer HWND. These measure scheduling/message-pump responsiveness, not a
physical-keypress latency guarantee. Idle render deltas come from Three's real
renderer frame counter and a test-only native Render wrapper. Frame counters may
reset across model navigation; compare deltas only within a persistent ready
page. Suspended cores are detected through IsSuspended; the harness does not run
JavaScript to wake them, and cached counters are marked suspended with no inferred
delta. Script observations also have a500ms timeout. Browser CPU immediately following initial STEP load can include startup,
WASM and garbage collection even when the frame counter stays unchanged.

Raw reports are `artifacts/before.json`, `artifacts/after.json`, with incremental
phase snapshots in corresponding `*-partial.json` files. Results are a bounded
single-machine comparison, subject to background load and CPU timer granularity;
zero means no measured CPU ticks in that interval.

GetProcessInfos scope and private-memory access follow the official
[WebView2 process information API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.getprocessinfos).
