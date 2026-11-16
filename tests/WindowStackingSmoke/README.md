# Explorer / preview native stacking regression

Requires Windows 10/11, .NET 6 SDK, and an interactive desktop. Run outside the
restricted sandbox so Shell COM and native foreground/Z-order APIs are available.

```powershell
dotnet run --project tests/WindowStackingSmoke/WindowStackingSmoke.csproj -c Release
```

Opens a dedicated Explorer window on three generated TXT fixtures, captures its
origin through the production selection service, and deliberately puts Explorer
above the still-visible preview. The production selection poll must raise the
preview above Explorer while Explorer keeps keyboard focus. The native
`WS_EX_TOPMOST` flag must remain clear. Both changing files and reactivating the
same selected file are covered; native position and size must remain unchanged.

An unrelated foreground WPF window must remain above the preview. Hidden and
minimized previews must stay hidden/minimized. Assertions use real `GetWindow`
Z-order, `GetForegroundWindow`, `GetWindowRect`, `GetWindowLong`, and `IsIconic`;
screenshots and control visibility alone cannot prove these behaviors.

The test unsubscribes production startup, installs no hooks or tray icon, sends
no keystrokes, and closes only its own WPF/Explorer fixture windows. Explorer's
HWND must be absent from the pre-test window inventory before cleanup can call
Quit, protecting existing user windows if Windows reuses an Explorer tab.
Fixtures live under ignored `bin/`. No installed QuickPreview process is stopped.
