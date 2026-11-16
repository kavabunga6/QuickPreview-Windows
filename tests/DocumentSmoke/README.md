# Document and SVG smoke checks

From the repository root on Windows with .NET 6 SDK:

```powershell
dotnet run --project tests/DocumentSmoke/DocumentSmoke.csproj -c Release
```

The console harness compiles `DocumentPreviewService.cs` and `SvgPreviewService.cs` directly. All test documents, including the Outlook MSG compound file, are generated in a unique temporary folder and removed afterward. No downloaded third-party document fixtures are included.

Coverage: RFC 2047 email headers, MIME quoted-printable bodies, attachment listings without extraction, HTML paragraphs and script removal, Unicode MSG properties and attachments, EPUB reading order and remote resource skipping, ICS timezones/all-day dates/folded lines/nested alarms, vCard structured escapes and legacy quoted-printable charsets, RTF paragraphs and Unicode, invalid inputs, output limits, cancellation, SVG/SVGZ rendering, external reference stripping, DTD rejection, and reference cycles. The SVG external-resource check listens only on loopback and asserts that the renderer makes no request.

Production dependencies are MimeKit 4.17.0 (MIT), MsgReader 6.0.3 (MIT; brings RtfPipe), and SharpVectors.Wpf 1.8.5 (BSD-3-Clause). These versions were selected to retain .NET 6 compatibility; later email packages currently bring BCL 10 dependencies that warn about unsupported .NET 6 targets.
