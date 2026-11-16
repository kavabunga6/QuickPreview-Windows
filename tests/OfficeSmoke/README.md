# Office preview smoke checks

Run from the repository root on Windows with the .NET 6 SDK:

```powershell
dotnet run --project tests/OfficeSmoke/OfficeSmoke.csproj -c Release
```

The executable compiles the Office text readers and bounded XML loader directly and needs no NuGet test packages or Microsoft Office application. It generates its modern OOXML/ODF fixtures in a unique temporary directory, deletes only that directory afterward, and reads the legacy DOC/PPT fixtures through the registered Windows document IFilter. The legacy checks need a working Windows Office document filter; an unavailable filter causes an explicit test failure.

Coverage includes Word run boundaries and tables; custom main-part relationships; PowerPoint presentation order; ODF whitespace; output truncation; encrypted-document messages; DTD rejection; cancellation; extension routing; and real DOC/PPT extraction. Spreadsheet parsing and the shared legacy XLS fixture are covered by [SpreadsheetSmoke](../SpreadsheetSmoke/README.md); XML depth and number-format regressions are covered by [ReaderRegressionSmoke](../ReaderRegressionSmoke/README.md).

## Binary fixture provenance

The following unmodified files were retrieved from the Apache POI public test-data repository on 2026-11-15. They are included solely for these tests, under the upstream Apache License 2.0. The upstream license and notice are retained beside them as `LICENSE-APACHE-2.0.txt` and `NOTICE-APACHE-POI.txt`.

| Local name | Upstream file | SHA-256 |
| --- | --- | --- |
| `fixtures/legacy.doc` | [document/simple.doc](https://github.com/apache/poi/blob/trunk/test-data/document/simple.doc) | `4876E828E62D490FA188C7C4E47013E2E0DFDF02F5903DFD374BC0D2B2049CA1` |
| `fixtures/legacy.xls` | [spreadsheet/Simple.xls](https://github.com/apache/poi/blob/trunk/test-data/spreadsheet/Simple.xls) | `2B5EC8298BACFFDCA61C50F6F7607DA0A4E63CF76FA011C62F203E6A66BC072A` |
| `fixtures/legacy.ppt` | [slideshow/basic_test_ppt_file.ppt](https://github.com/apache/poi/blob/trunk/test-data/slideshow/basic_test_ppt_file.ppt) | `E8FD4C38AD72CE3735ACBCA0B60DD5870FB60F819C169E3947D166F4F1BCC79D` |

The production preview services do not depend on Apache POI.
