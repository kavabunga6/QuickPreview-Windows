# Model budgets and render scheduling

Run on an interactive Windows desktop with WebView2:

```powershell
dotnet run --project tests/ModelBudgetSmoke/ModelBudgetSmoke.csproj -c Release
```

Uses generated original fixtures and an isolated browser profile under ignored
bin output. Does not create an Explorer window, install hooks or touch installed
QuickPreview. Verifies binary STL/streaming OBJ/container preflight and cancellation,
actual4x-DPI drawing-buffer limits, zero idle frames, fit/resize coalescing and cached
bounds. An injected oversized importer result checks rejection before Three.js
conversion. This fixture proves budgets and render behavior; before/after machine
CPU, memory and dispatcher timing are measured separately by PreviewPerformanceSmoke.
