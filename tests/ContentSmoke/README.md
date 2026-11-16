# Table and font smoke tests

On Windows with the .NET 6 SDK:

```powershell
dotnet run --project tests\ContentSmoke\ContentSmoke.csproj -c Release
```

Generated CSV/TSV fixtures cover quoted delimiters, escaped quotes, multiline
cells, UTF-16 Cyrillic, semicolon detection, ragged rows, row/column/cell/byte
limits, empty input, cancellation and unchanged source hashes.

The test copies an existing Windows TrueType font into its isolated fixture
directory under a filename containing spaces. It verifies actual glyph metadata
and rasterizes a Latin/Cyrillic/numeric specimen offscreen through WPF. The font
is not installed and its source hash stays unchanged. The entry point runs in
STA for WPF font rendering. No screenshots or desktop input are required.
