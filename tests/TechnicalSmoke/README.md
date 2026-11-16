# Technical preview smoke tests

On Windows 10/11 with the .NET 6 SDK:

```powershell
dotnet run --project tests\TechnicalSmoke\TechnicalSmoke.csproj -c Release
```

Fixtures are generated in the ignored `bin/` directory. Assertions cover Unicode
text detection/decoding, actual read-only SQLite schema and row queries, source
hash and modification-time preservation, PCAP byte orders, PCAPNG interfaces and
packet endpoints, X.509 metadata and protected/private containers, PE sections
and imports without executing the fixture, bounded hex output, ISO9660/Joliet
root listings without mounting, malformed data and cancellation.

SQLite uses the Windows system `winsqlite3.dll`; no DLL is downloaded or copied.
The EVTX assertion exports a filtered local Application log archive to the
fixture directory and reads it through the OS EventLogReader. That assertion is
explicitly skipped if the current sandbox/session cannot read the log. Event
payloads are not printed. No source database, executable, certificate store,
mounted disk, application setting or network service is changed.
