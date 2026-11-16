# Reader review regressions

```powershell
dotnet run --project tests/ReaderRegressionSmoke/ReaderRegressionSmoke.csproj -c Release
```

The headless suite generates its own compressed XML packages and rejects
40,000-level nesting before building recursive text values. It covers ODS,
XLSX, Word, EPUB, SVG and the plain-XML helper, plus DTD rejection, cancellation
and preservation of decoded UTF-16 declarations/whitespace. The formerly
crashing ODS fixture is only a few kilobytes compressed.

Number format checks cover unit scaling, zero sections, month-only dates,
minute/second and elapsed-time formats, negative percentages, leading zeroes,
cached zero, the 1904 date epoch, currency position and invalid formats. All
files are synthetic and stay under ignored `artifacts/`; no native UI opens.
