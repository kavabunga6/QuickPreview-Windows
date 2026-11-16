# Source preview smoke checks

Run from the repository root with the .NET 6 SDK:

```powershell
dotnet run --project tests/SourceSmoke/SourceSmoke.csproj -c Release
```

This console project compiles only `SourcePreviewService.cs` and `FormatCatalog.cs`; it has no WPF, browser, native decoder, model renderer, or UI dependencies. The CodePages package supplies Windows-1251 decoding. All fixtures are generated in a unique temporary directory and removed afterward. No third-party model files are included.

The checks cover the visual-handler extension set; Cyrillic filenames and uppercase STEP extensions; Windows-1251, UTF-8, UTF-16 and UTF-32 source text; raw whitespace and line-ending preservation; ASCII versus binary STL/PLY/FBX/DXF; binary-only model containers; SVG/SVGZ; CSV/TSV; embedded 3MF/FCStd XML; malformed/missing files; the 4 KiB sniff boundary; 2 MiB plain/decompressed limits; cancellation; concurrent writer sharing; source SHA-256 preservation; and absence of archive extraction.

`CanShowAsync` reads at most 4 KiB of uncompressed source payload. ZIP metadata is read separately to locate a 3MF/FCStd XML member, on a background thread, with 128 MiB container and 20,000-entry limits. `LoadAsync` checks all displayed bytes, so binary content appearing after the initial sniff is still rejected. Truncation is reported in the result and summary; no notice is inserted into raw source text.
