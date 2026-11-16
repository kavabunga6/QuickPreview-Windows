# Browser and decoder resource lifecycle smoke test

Run on an interactive Windows desktop with .NET 6 and WebView2 Runtime:

```powershell
dotnet run --project tests\ResourceLifecycleSmoke\ResourceLifecycleSmoke.csproj -c Release
```

Run separately from CPU/GPU benchmarks. This bounded 28-second test uses real
WPF/WebView2 controls with a fresh profile and existing synthetic cube/PDF
fixtures. Application Startup is unsubscribed, so no tray, keyboard hooks,
Explorer selection hooks, startup setting or installed application is changed.

Assertions cover actual browser suspension/resumption, unchanged loaded model
session/camera/cache across source tabs and minimization, cancellation during
gated initialization and the pre-parser delay, restored source selection, model
disposal on hide, and PDF document release. The decoder job is instantiated and
its actual kernel CPU/priority/memory limits queried without starting a decoder
or running a CPU load. Results go to the ignored `artifacts/result.json`.
