# Native Windows PDF renderer checks

Requires Windows 10/11 and the .NET 6 SDK. No Office installation, Edge runtime,
UI automation, or third-party PDF library is needed.

```powershell
dotnet run --project tests/NativePdfSmoke/NativePdfSmoke.csproj -c Release
```

Opens the original two-page fixture `tests/Fixtures/preview.pdf`, renders both
pages with the production native fallback, and verifies page count, bounded
dimensions, portrait/landscape orientation, distinct pixels, cross-thread bitmap
availability, and cancelled loading. The fixture is an original minimal PDF 1.4
with Helvetica text and coloured rectangles; it has no embedded files or scripts.
