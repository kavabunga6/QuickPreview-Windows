# Spreadsheet preview regression

```powershell
dotnet run --project tests/SpreadsheetSmoke/SpreadsheetSmoke.csproj -c Release -- --source-only
dotnet run --project tests/SpreadsheetSmoke/SpreadsheetSmoke.csproj -c Release
dotnet run --project tests/SpreadsheetSmoke/SpreadsheetSmoke.csproj -c Release -- --workbook "C:\path\report.xlsx"
```

The source pass uses original generated XLSX/ODS packages and the existing
OfficeSmoke BIFF XLS fixture. It checks merged ranges, sparse coordinates,
hidden rows/columns, styles, cached formulas, date/number formats, cancellation,
external-entity rejection and bounded repeated ODS tails.

The full pass creates only its own WPF preview, with application startup and
global keyboard hooks disabled. It captures light/dark spreadsheet previews at
1040/520px and 100/125% scale, checks native HTML table geometry and toolbar
alignment, switches sheets and zoom, and verifies browser/session cleanup after
minimizing, changing files, rapid A→B→A selection and hiding the preview.

`--workbook` reads an optional local workbook, saves a local preview capture and
checks that the file hash remains unchanged. Workbook values are not written to
test source or sent to an external service. Generated fixtures, captures and
results stay under the ignored `artifacts/` directory. The linked legacy XLS
fixture retains its existing Apache POI provenance in `tests/OfficeSmoke/fixtures`.
