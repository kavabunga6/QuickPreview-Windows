# WPF preview UI checks

Requires Windows 10/11 and the .NET 6 SDK.

```powershell
dotnet run --project tests/UiSmoke/UiSmoke.csproj -c Release
```

Instantiates the production WPF window and application resources without running
the production Startup handler. It does not install keyboard hooks, create a tray
icon, or change startup settings. It tests archive rows and header alignment,
Office document text, and missing-WebView2-loader PDF fallback with working page
navigation at 1000 × 700 and 520 × 500 in both light and dark themes.

Additional CSV table, font specimen, EML, and SVG fixtures run at those same widths
in both themes with 100% and 125% text sizes. CSV fixtures include short and long
column headings, and compare rendered header/cell text origins with a 1 px
tolerance. The font fixture copies the machine's Arial font into generated build
output under a filename containing spaces; no Microsoft font is committed.
EML checks cover readable headers/body, wrapping, and omitted scripts. SVG checks
require a rendered image and aligned footer actions. These scenarios produce
32 additional PNGs (44 total with the preserved baseline fixtures).

Actual WPF layout is rendered to PNG files under `screenshots/` for visual review.
These images are generated test output and are ignored by Git. The PDF fixture is
original; archive fixture provenance is in `../ArchiveSmoke/README.md`.
