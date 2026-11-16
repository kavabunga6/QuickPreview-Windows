# QuickPreview third-party components

The application uses unmodified .NET packages under their original licenses.
Full license texts and notices are in `assets/licenses/`; offline browser/CAD
module licenses and source distribution references are in
`assets/model-viewer/THIRD-PARTY-NOTICES.md` and its `vendor/` directory.

| Component | Version | License / copyright | Corresponding source |
| --- | --- | --- | --- |
| Magick.NET / bundled ImageMagick codecs | 15.0.0 | Apache-2.0 and bundled codec notices; Dirk Lemstra and contributors | https://github.com/dlemstra/Magick.NET/tree/15.0.0 |
| ACadSharp | 3.6.51 | MIT; Albert Domenech | https://github.com/DomCR/ACadSharp |
| MimeKit | 4.17.0 | MIT; Jeffrey Stedfast | https://github.com/jstedfast/MimeKit/tree/4.17.0 |
| MsgReader | 6.0.3 | MIT; Kees van Spelde | https://github.com/Sicos1977/MSGReader/tree/d39571a290dc40451b42af4bc2a3a2e5e60e1be4 |
| OpenMcdf | 3.3.0 | MPL-2.0; Federico Blaseotto, Jeremy Powell and contributors | https://github.com/ironfede/openmcdf |
| RtfPipe | 2.0.7677.4303 | MIT; Eric Domke | https://github.com/erdomke/RtfPipe |
| SharpVectors | 1.8.5 | BSD-3-Clause; Elinam LLC | https://github.com/ElinamLLC/SharpVectors |
| ExcelDataReader | 3.9.0 | MIT; ExcelDataReader contributors | https://github.com/ExcelDataReader/ExcelDataReader/tree/v3.9.0 |
| ExcelNumberFormat | 1.2.0 | MIT; andersnm | https://github.com/andersnm/ExcelNumberFormat/tree/ab946237dc0ab1fb50abda150a3cd893ed9f4f78 |
| SharpCompress | 0.50.9 | MIT; Adam Hathcock and contributors | https://github.com/adamhathcock/sharpcompress/tree/0.50.9 |
| Microsoft WebView2 SDK | 1.0.4078.44 | Microsoft WebView2 SDK license | https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4078.44 |

OpenMcdf is used without modification; its covered source and build information
are available from the linked upstream repository. The MPL-2.0 license text is
included as `assets/licenses/MPL-2.0.txt`. ImageMagick's native codec copyrights
and complete bundled notices are included in `Magick.NET-NOTICES.txt`.

ExcelNumberFormat is used without modification. Its source is pinned to the
commit linked above; the complete MIT license is included as
`assets/licenses/ExcelNumberFormat-LICENSE.txt`.

Framework and other transitive NuGet component notices remain available in the
corresponding package distributions. Test-only fixtures carry their own
provenance and licenses in each test project's README and fixture directory.
