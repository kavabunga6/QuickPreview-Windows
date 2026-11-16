# Archive preview smoke checks

Run on .NET 6 or later:

```powershell
dotnet run --project tests/ArchiveSmoke/ArchiveSmoke.csproj -c Release
```

Checks ZIP, 7z, TAR, tar.gz, tar.xz, tar.bz2, RAR, UTF-8 and legacy OEM names,
encrypted filenames/headers, empty/corrupt input, cancellation, the entry limit,
and that listing never modifies the source archive. Listing never extracts files.

`sample.*` (except RAR), `encrypted-headers.7z`, and `encrypted-entries.zip` were
generated with 7-Zip from a directory `nested` containing a UTF-8-named text file
`привет.txt` with the eleven bytes `hello world`. Encrypted fixtures use password
`test`. The password is intentionally not supplied to the preview service.

The RAR fixtures are copied unchanged from SharpCompress 0.50.9's MIT-licensed
test corpus, with descriptive local names:

- `sample.rar`: [Rar.comment.rar](https://github.com/adamhathcock/sharpcompress/blob/0.50.9/tests/TestArchives/Archives/Rar.comment.rar)
- `encrypted-headers.rar`: [Rar.encrypted_filesAndHeader.rar](https://github.com/adamhathcock/sharpcompress/blob/0.50.9/tests/TestArchives/Archives/Rar.encrypted_filesAndHeader.rar)

Source: [SharpCompress](https://github.com/adamhathcock/sharpcompress),
copyright (c) 2014 Adam Hathcock, [MIT license](https://github.com/adamhathcock/sharpcompress/blob/0.50.9/LICENSE.txt).
