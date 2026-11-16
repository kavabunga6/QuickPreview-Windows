# Explorer selection smoke test

Run on an interactive Windows desktop with the .NET 6 SDK:

```powershell
dotnet run --project tests\ExplorerSmoke\ExplorerSmoke.csproj -c Release
```

This test opens its own File Explorer window containing three generated text
fixtures. It sorts that view by modification date descending and verifies that
preview navigation follows the actual Explorer view and selected file, including
the first and last boundaries. Alphabetical ordering produces a different result
and fails the test. The fixture window is closed in `finally`; existing Explorer
windows are not changed. No keyboard or UI Automation injection is used.

The test requires access to the desktop Shell COM server (`Shell.Application`).
A restricted sandbox or noninteractive CI session may deny that access; run it
outside that sandbox in the logged-in user's desktop session. Administrator
rights are not required. Generated fixtures remain under the ignored `bin/`
output directory.
