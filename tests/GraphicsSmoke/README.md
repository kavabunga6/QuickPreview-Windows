# Image and drawing regression checks

```powershell
dotnet run --project tests/GraphicsSmoke/GraphicsSmoke.csproj -c Release
```

Runs the real separate decoder process with its Windows Job limits, using
generated PSD, TGA, DDS, EXR, HDR, AVIF, JP2, JXL, QOI, PPM, PCX, WebP,
Krita/OpenRaster, DXF and DWG files. Checks dimensions, real drawing pixels,
unchanged source hashes, corrupt-input errors, and cancellation.

Generated inputs and rendered drawings remain in ignored `bin/`.

`fixtures/example.heic` is the public libheif example from
https://github.com/strukturag/libheif/blob/master/examples/example.heic,
downloaded 2026-11-16. The repository license is included as
`fixtures/LIBHEIF-LICENSE.txt`. It exercises real HEIC decoding because the
bundled ImageMagick build intentionally has no HEIC encoder.
