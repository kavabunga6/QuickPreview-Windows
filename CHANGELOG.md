# Changelog

## 1.3.10 — 2026-11-16

- Reject excessively nested XML before building document trees, including Office/OpenDocument, EPUB and SVG containers; keep ordinary XML available as raw text when formatting is unsafe.
- Correct Excel number formats for scaled units, zero sections, month and elapsed-time formats, percentages, leading-zero identifiers and the 1904 date system with ExcelNumberFormat 1.2.0. Remove the obsolete duplicate spreadsheet text readers.
- Decode ordinary images and folder thumbnails outside the UI process, with cancellation, time/memory limits and bounded output. Preserve EXIF orientation, select the largest icon frame, and report when a large image is reduced.
- Move folder listing, standalone navigation and file metadata reads to a bounded background worker, preventing slow filesystem calls from blocking the preview interface or accumulating workers.
- Show errors from Open and Show in folder in the visible preview rather than letting shell failures escape.
- Keep very small and very large 3D models visible by adapting clipping planes to the model and current camera, including manual zoom, without adding idle rendering.
- Normalize CAD text measurement so equivalent DXF/DWG drawings with large coordinate scales render identically without exceeding WPF font limits.
- Add generated regressions for deeply nested documents, spreadsheet display values, native image decoding and bounded filesystem operations.

## 1.3.9 — 2026-11-16

- Render XLSX/XLSM/XLTX/XLTM, legacy XLS/XLT and ODS/OTS as local spreadsheet grids instead of tab-separated text, with sheet selection, merged cells, source dimensions, basic styles, numeric/date formats, zoom and width fitting.
- Keep parsing, cells, text, XML expansion and active-sheet DOM bounded; read cached formulas without evaluation or external links. Suspend inactive spreadsheet browsers and dispose old documents on selection, hide and cancellation.
- Ignore obsolete navigation-completion events when opening the next workbook, preventing canceled blank-page loads from failing the current table.
- Verify initial STEP/OBJ framing and translated/rotated/scaled 3MF before Fit/Edges, including rapid arrow bursts and late callbacks; preserve model identity, full framing, focus and window stacking.

## 1.3.8 — 2026-11-16

- Fix initially offset and clipped STL previews by applying imported root transforms before calculating and caching model bounds.
- Schedule initial camera fitting independently of viewport-size changes, including when the model becomes visible after loading; keep manual camera changes and on-demand rendering.
- Add initial binary STL framing regression coverage before Fit/Edges can mask the error, including asymmetric, negative-offset and large-coordinate models and an optional read-only local model probe.

## 1.3.7 — 2026-11-16

- Move Visual/Text selection into a compact icon switcher beside the window close button, keeping accessible labels, tooltips and keyboard focus.
- Keep the switcher visible and its header space fixed for every file; disable it when only one representation is available.
- Remove the separate representation toolbar so switching files no longer adds or removes a row above the preview.

## 1.3.6 — 2026-11-16

- Close the preview when its originating Explorer tab host or frame is destroyed, including while minimized or another application is foreground.
- Bind selection, navigation and reveal to the captured browser/tab rather than the first Shell window sharing a frame; ignore unrelated windows, neighboring tab hosts and ordinary folder-view replacement.
- Use per-binding native destroy notifications and a lightweight 500 ms fallback, rejecting stale notifications after reopening; automatic closure cancels rendering without restoring Explorer focus.
- Capture the Space gesture's tab before dispatcher queuing and revalidate it through asynchronous opening so a closed tab cannot redirect the preview to its neighbor.

## 1.3.5 — 2026-11-16

- Bound 3D preview geometry, textures, materials, edge generation and drawing resolution; reject oversized STL/OBJ/container data before browser import where possible.
- Coalesce redraw/resize work, cache model bounds, revalidate trusted viewer assets and terminate importer workers on disposal.
- Suspend loaded browser previews on the Text tab or minimization, cancel pending work while minimized, and release hidden model/PDF documents.
- Lower owned browser renderer/utility priority; run isolated image/CAD decoders below normal priority with a 25% CPU job ceiling, existing memory/time limits and cancellation.
- Move selection reads and Space opening off the UI to the shared STA, prioritize arrows over polling, and use bounded selection notifications with adaptive idle polling.

## 1.3.4 — 2026-11-16

- Preserve Left/Up/Right/Down separately through both the global hook and preview keyboard handler.
- Navigate the active Explorer layout: rows for icons/tiles, columns for List, and vertical rows for Details/Content, following live view changes, sorting and resizing.
- Read accessibility layout on a bounded background worker, select through Shell without taking focus, and discard queued arrows after closing, reopening or leaving the preview.
- Keep the highlighted Explorer item and preview synchronized across scrolling and grouped views.

## 1.3.3 — 2026-11-16

- Keep Space-opened previews above Explorer without taking keyboard focus or leaving a persistent topmost window.
- Cancel obsolete loads across fast PDF/archive/model selection, including A → B → A, and show browser content only when the matching document/model is ready.
- Coalesce rapid selections, avoid blank PDF navigations and short loading-indicator flashes, and reuse the WebView environment across viewers.
- Fix rounded tab clipping and consistent button label alignment in native controls and the 3D toolbar.

## 1.3.2 — 2026-11-16

- Keep the preview above its originating Explorer window when the selection changes or Explorer is activated again.
- Preserve Explorer keyboard focus, preview position/size, normal stacking with other applications, and minimized/hidden states.

## 1.3.1 — 2026-11-16

- Add Visual/Text tabs for text-based visual formats, opening the visual by default.
- Keep source text unformatted, read it lazily with cancellation and size limits, and expose model XML from 3MF/FreeCAD containers.
- Preserve loaded visuals, source scroll positions and the 3D camera when switching tabs.
- Retain the selected tab while asynchronous visual loading completes; reset it when selecting a different file.
- Keep source available when visual rendering fails, and preserve native keyboard navigation on tab headers.

## 1.3.0 — 2026-11-16

- Add offline interactive STEP/STL/3MF/OBJ and other CAD/3D previews, including STEP/IGES/BREP tessellation and FreeCAD/Rhino/IFC importers.
- Add bounded DWG/DXF model-space plan rendering and isolated advanced raster decoding, including HEIC, AVIF, PSD, EXR and DDS.
- Add sanitized SVG rendering, local font specimens, and CSV/TSV tables.
- Add email, EPUB, RTF, calendar and contact content previews.
- Add read-only SQLite data, Windows events, network captures, ISO/Joliet directories, PE headers/imports, certificates and a binary hex fallback.
- Expand source/configuration formats and improve UTF-8/16/32 text detection.
- Preserve Explorer selection synchronization for every new viewer.

## 1.2.0 — 2026-11-15

- Follow the highlighted Explorer item while navigating with arrows, preserving current sort, grouping, filters, and icon layout.
- Keep Explorer focus when opening with Space; synchronize selection while navigating from the preview too.
- Ship the native WebView2 loader with the installer and use a writable browser profile folder.
- Fall back to Windows PDF page rendering when WebView2 or its loader is unavailable.
- List ZIP, 7z, RAR, TAR, and compressed TAR contents locally without extraction.
- Preview Word text, Excel stored cell values, PowerPoint slide text, and OpenDocument content without launching Office.
- Read legacy DOC/XLS/PPT content using installed Windows search filters.

## 1.1.0 — 2026-08-15

- Add interactive PDF preview using the built-in Microsoft Edge WebView2 PDF viewer.
- Pretty-print valid JSON and XML files while preserving raw text for malformed documents.
- Fall back to the Windows system thumbnail when the WebView2 Runtime is unavailable.
- Add image zoom from 5% to 800% with mouse-wheel, keyboard, actual-size, and fit-to-window controls.
- Pan zoomed images by dragging them with the left mouse button.

## 1.0.1 — 2026-08-15

- Ensure the preview window is brought to the foreground when opened from File Explorer.
- Return the preview to the normal window layer immediately, so it does not remain always on top.

## 1.0.0 — 2026-08-15

- Quick Look-style preview from File Explorer with the Space key.
- Image, text, source code, audio, video, folder, and Windows thumbnail previews.
- Previous/next file navigation with Explorer selection synchronization.
- EXIF Orientation support and full-resolution image loading.
- Audio and video seeking, volume, mute, and media controls.
- Embedded audio artwork through the Windows thumbnail provider.
- Automatic Windows light/dark theme and accent color support.
- System tray integration and optional startup with Windows.
- Per-user, self-contained Inno Setup installer with standard uninstallation.
