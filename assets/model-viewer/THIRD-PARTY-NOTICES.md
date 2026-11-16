# Offline 3D viewer components

QuickPreview uses these separately distributed libraries for local 3D rendering.
No model is sent to a service. All JavaScript and WebAssembly files are loaded
from this installation. The application does not need Node.js or npm at runtime.

| Component | Version | License | Source |
| --- | --- | --- | --- |
| Online 3D Viewer | 0.18.0 | MIT | https://github.com/kovacsv/Online3DViewer/tree/0.18.0 |
| three.js (inside the viewer bundle) | 0.176.0 | MIT | https://github.com/mrdoob/three.js/tree/r176 |
| fflate (inside the viewer bundle) | 0.8.2 | MIT | https://github.com/101arrowz/fflate/tree/v0.8.2 |
| occt-import-js / Open Cascade import module | 0.0.22 | LGPL-2.1 | https://github.com/kovacsv/occt-import-js |
| rhino3dm | 8.17.0 | MIT | https://github.com/mcneel/rhino3dm/tree/8.17.0 |
| web-ifc | 0.0.68 | MPL-2.0 | https://github.com/ThatOpen/engine_web-ifc |
| Draco | 1.5.7 | Apache-2.0 | https://github.com/google/draco/tree/1.5.7 |

Complete license texts ship under `vendor/`. The Open Cascade import module and
IFC module are unmodified external library files, not embedded into the executable;
their replaceable `.js`/`.wasm` files are in `vendor/occt/` and `vendor/ifc/`.
Their corresponding source, build scripts and release metadata are also available
in the exact npm source distributions:

- https://registry.npmjs.org/occt-import-js/-/occt-import-js-0.0.22.tgz
- https://registry.npmjs.org/web-ifc/-/web-ifc-0.0.68.tgz

The OCCT distribution contains the module's C++ sources and Windows Emscripten
build scripts. The OCCT upstream used by its build is identified by those sources.

The browser bundle came from
https://github.com/kovacsv/Online3DViewer/releases/download/0.18.0/o3dv.zip.
QuickPreview changes exactly four fixed CDN prefixes in `o3dv.min.js` to local
`https://quickpreview-model.invalid/vendor/{occt,rhino,ifc,draco}/` URLs. No other
viewer or third-party library source is modified. The Draco decoder's original
`draco_decoder_nodejs.js` is stored as `draco_decoder_nodejs.min.js` because that is
the filename requested by the viewer. Local HTML/CSS and the adapter JavaScript
are original QuickPreview code.
