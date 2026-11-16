# Native image and folder budgets

```powershell
dotnet run --project tests/NativeContentSmoke/NativeContentSmoke.csproj -c Release
```

Uses only generated, local fixtures. Native image decoders run in the normal
isolated worker: original-resolution PNG, EXIF JPEG, a downsampled 24MP JPEG,
multi-size ICO and a tiny PNG whose rewritten IHDR declares 400MP. The huge
header case must fail before raster allocation. Frozen transport and source
hashes are checked.

Folder checks cover natural ordering, truncation, cancellation and standalone
navigation. A bounded simulated blocked OS read verifies that timing out the
caller does not release the single-reader gate prematurely or accumulate new
workers. No network locations or user files are accessed; no native windows or
keyboard hooks are opened by this console harness.
