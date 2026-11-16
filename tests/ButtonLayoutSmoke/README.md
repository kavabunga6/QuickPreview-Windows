# Button and tab layout smoke check

Requires an interactive Windows desktop, .NET 6 SDK and Edge WebView2.

```powershell
dotnet run --project tests/ButtonLayoutSmoke/ButtonLayoutSmoke.csproj -c Release
```

Loads the real WPF window and shared application resources with production startup
unsubscribed. It uses an isolated WebView2 profile and opens SVG, then a real STEP
cube, without pre-initializing the model control or revealing it before readiness.

The matrix covers light/dark themes, 1000×700 and 520×500 windows, 100%/125% text,
and both selected tabs. It measures actual visible control/chrome/text bounds for
tab headers, image controls, close and footer buttons. It checks complete borders,
centered labels, label containment, tab alignment and spacing. Embedded STEP button
bounds and text ranges are measured in the live DOM. WPF and separate WebView2 PNGs,
plus `measurements.json`, are written to ignored `screenshots/after/`.

WPF bitmap capture excludes native WebView2 airspace; pair `step-visual-*.png` with
the matching `step-canvas-*.png`. Text scaling adjusts font sizes, not Windows DPI.
The check measures text layout boxes, not individual glyph ink, and does not drive
mouse/keyboard input. Restricted sandboxes may block WebView2 graphics.
