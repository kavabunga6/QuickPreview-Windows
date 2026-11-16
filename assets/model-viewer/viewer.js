'use strict';
(() => {
  const query = new URLSearchParams(location.search);
  const light = query.get('theme') === 'light';
  document.documentElement.dataset.theme = light ? 'light' : 'dark';
  const viewport = document.getElementById('viewport');
  const status = document.getElementById('status');
  const fit = document.getElementById('fit');
  const edges = document.getElementById('edges');
  const limits = {
    maxTriangles: 500000, maxLineSegments: 1000000, maxInstances: 1000,
    maxMaterials: 1000, maxVertices: 1500000, maxEdgeTriangles: 100000,
    maxTextureBytes: 32 * 1024 * 1024, maxTexturePixels: 16 * 1024 * 1024,
    maxTextures: 64, maxPixelRatio: 1.5, maxDrawingPixels: 1920 * 1080
  };
  const drawingRatio = (ratio, width, height) => Math.min(Math.max(0.1, ratio || 1),
    limits.maxPixelRatio, Math.sqrt(limits.maxDrawingPixels / Math.max(1, width * height)));
  // Cap the constructor's very first buffer too, before the instance scheduler
  // can be installed. OV's normal resize otherwise allocates at full device DPR.
  const initializeViewer = OV.Viewer.prototype.Init;
  OV.Viewer.prototype.Init = function (canvas) {
    canvas.width = canvas.height = 1;
    return initializeViewer.call(this, canvas);
  };
  const resizeViewer = OV.Viewer.prototype.ResizeRenderer;
  OV.Viewer.prototype.ResizeRenderer = function (width, height) {
    const renderer = this.renderer;
    const setRatio = renderer.setPixelRatio;
    renderer.setPixelRatio = ratio => setRatio.call(renderer, drawingRatio(ratio, width, height));
    try { return resizeViewer.call(this, width, height); }
    finally { renderer.setPixelRatio = setRatio; }
  };
  const configRequest = new AbortController();
  let viewer = null;
  let loaded = false;
  let disposed = false;
  let fitMode = true;
  let fitDirty = false;
  let fittedSphere = null;
  let frame = 0;
  let dirty = false;
  let resizeDirty = true;
  let rendering = false;
  let resizeObserver = null;
  let renderNow = null;
  let resizeNow = null;
  let lastWidth = -1;
  let lastHeight = -1;
  const post = message => window.chrome?.webview?.postMessage(message);

  const schedule = () => {
    if (disposed || rendering || frame || document.hidden) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      if (disposed || document.hidden || !viewer) return;
      rendering = true;
      try {
        if (resizeDirty) {
          resizeDirty = false;
          const width = Math.floor(viewport.clientWidth);
          const height = Math.floor(viewport.clientHeight);
          if (width > 0 && height > 0 && (width !== lastWidth || height !== lastHeight)) {
            lastWidth = width;
            lastHeight = height;
            resizeNow(width, height);
            fitDirty = true;
          }
        }
        if (fitDirty && fitMode && loaded && viewport.clientWidth > 0 && viewport.clientHeight > 0) {
          fitDirty = false;
          fitToViewport();
        }
        if (dirty) {
          dirty = false;
          renderNow();
        }
      } finally { rendering = false; }
    });
  };
  const requestRender = () => { dirty = true; schedule(); };
  const requestResize = () => { resizeDirty = true; schedule(); };
  const fitToViewport = () => {
    if (!loaded || !fittedSphere) return;
    const native = viewer.GetViewer();
    native.AdjustClippingPlanesToSphere(fittedSphere);
    native.FitSphereToWindow(fittedSphere, false);
  };
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      if (frame) cancelAnimationFrame(frame);
      frame = 0;
      return;
    }
    requestResize();
    requestRender();
  });
  // A user-chosen camera survives tab changes and resizing until Fit is pressed.
  viewport.addEventListener('pointerdown', () => { fitMode = false; });
  viewport.addEventListener('wheel', () => { fitMode = false; }, { passive: true });

  const destroy = () => {
    if (disposed) return;
    disposed = true;
    configRequest.abort();
    resizeObserver?.disconnect();
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    // OV keeps its OCCT worker on the individual importer. Destroying the viewer
    // alone does not clear those importers while an import is still pending.
    const importer = viewer?.modelLoader?.GetImporter();
    if (importer?.importers) {
      for (const format of importer.importers) {
        try { format.Clear(); } catch { }
      }
    }
    try { viewer?.Destroy(); } catch { }
    viewer = null;
    fittedSphere = null;
  };
  const fail = (reason = 'invalid') => {
    if (disposed) return;
    status.textContent = reason === 'complexity'
      ? 'Модель слишком сложная для быстрого предпросмотра. Откройте её в CAD-приложении.'
      : 'Не удалось построить модель. Откройте файл в CAD-приложении для проверки.';
    status.hidden = false;
    fit.disabled = edges.disabled = true;
    post({ type: 'error', reason });
    destroy();
  };
  window.addEventListener('error', () => fail(), { once: true });
  window.addEventListener('unhandledrejection', () => fail(), { once: true });

  const texturePixels = (buffer, name) => {
    const bytes = buffer instanceof ArrayBuffer ? new Uint8Array(buffer) : buffer;
    if (!ArrayBuffer.isView(bytes) || bytes.byteLength < 16) return null;
    const data = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const text = (offset, length) => String.fromCharCode(...bytes.subarray(offset, offset + length));
    if (bytes.byteLength >= 24 && text(1, 3) === 'PNG') return data.getUint32(16) * data.getUint32(20);
    if (text(0, 3) === 'GIF') return data.getUint16(6, true) * data.getUint16(8, true);
    if (bytes.byteLength >= 26 && text(0, 2) === 'BM') return Math.abs(data.getInt32(18, true) * data.getInt32(22, true));
    if (bytes.byteLength >= 20 && text(0, 4) === 'DDS ') return data.getUint32(12, true) * data.getUint32(16, true);
    if (bytes.byteLength >= 32 && text(0, 4) === 'RIFF' && text(8, 4) === 'WEBP') {
      if (text(12, 4) === 'VP8X') {
        const width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
        const height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
        return width * height;
      }
      if (text(12, 4) === 'VP8 ') return (data.getUint16(26, true) & 0x3fff) * (data.getUint16(28, true) & 0x3fff);
      if (text(12, 4) === 'VP8L' && bytes[20] === 0x2f) {
        const bits = data.getUint32(21, true);
        return (1 + (bits & 0x3fff)) * (1 + ((bits >>> 14) & 0x3fff));
      }
    }
    if (bytes[0] === 0xff && bytes[1] === 0xd8) {
      for (let offset = 2; offset + 9 < bytes.length;) {
        if (bytes[offset++] !== 0xff) break;
        while (offset < bytes.length && bytes[offset] === 0xff) offset++;
        const marker = bytes[offset++];
        if (marker === 0xd9 || marker === 0xda) break;
        if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
        if (offset + 2 > bytes.length) break;
        const length = data.getUint16(offset);
        if (length < 2 || offset + length > bytes.length) break;
        if ([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf].includes(marker))
          return data.getUint16(offset + 3) * data.getUint16(offset + 5);
        offset += length;
      }
    }
    if (/\.tga$/i.test(name || '')) return data.getUint16(12, true) * data.getUint16(14, true);
    return null;
  };
  const withinBudget = model => {
    if (model.TriangleCount() > limits.maxTriangles || model.LineSegmentCount() > limits.maxLineSegments ||
        model.VertexCount() > limits.maxVertices || model.MaterialCount() > limits.maxMaterials) return false;
    let instances = 0;
    model.EnumerateMeshInstances(() => { instances++; });
    if (instances > limits.maxInstances) return false;
    const textures = new Set();
    let textureBytes = 0;
    let pixels = 0;
    for (let index = 0; index < model.MaterialCount(); index++) {
      const material = model.GetMaterial(index);
      for (const field of ['diffuseMap', 'bumpMap', 'normalMap', 'emissiveMap', 'specularMap', 'metalnessMap']) {
        const texture = material[field];
        if (!texture?.buffer || textures.has(texture.buffer)) continue;
        textures.add(texture.buffer);
        textureBytes += texture.buffer.byteLength || 0;
        const size = texturePixels(texture.buffer, texture.name);
        if (size !== null) pixels += size;
        if (textures.size > limits.maxTextures || textureBytes > limits.maxTextureBytes ||
            !Number.isFinite(pixels) || pixels > limits.maxTexturePixels) return false;
      }
    }
    return true;
  };

  fetch('config.json?session=' + encodeURIComponent(query.get('session')), { signal: configRequest.signal })
    .then(response => { if (!response.ok) throw new Error('Missing model manifest'); return response.json(); })
    .then(config => {
      if (disposed) return;
      viewer = new OV.EmbeddedViewer(viewport, {
        backgroundColor: light ? new OV.RGBAColor(239, 240, 243, 255) : new OV.RGBAColor(20, 21, 25, 255),
        defaultColor: light ? new OV.RGBColor(122, 146, 176) : new OV.RGBColor(163, 185, 215),
        defaultLineColor: light ? new OV.RGBColor(36, 49, 68) : new OV.RGBColor(220, 228, 240),
        onModelLoaded: () => {
          if (disposed) return;
          const model = viewer.GetModel();
          const triangles = model.TriangleCount();
          const meshes = model.MeshCount();
          if (triangles === 0 && model.LineSegmentCount() === 0) { fail(); return; }
          status.hidden = true;
          loaded = true;
          fittedSphere ||= viewer.GetViewer().GetBoundingSphere(() => true);
          fitDirty = true;
          fit.disabled = false;
          edges.disabled = triangles > limits.maxEdgeTriangles;
          edges.title = edges.disabled ? 'Рёбра отключены для сложной модели, чтобы не задерживать предпросмотр.' : '';
          document.getElementById('stats').textContent = triangles.toLocaleString('ru-RU') + ' треугольников';
          window.quickPreviewModel = { viewer, triangles, meshes };
          requestResize();
          requestRender();
          post({ type: 'loaded', triangles, meshes });
        },
        onModelLoadFailed: () => fail()
      });
      const native = viewer.GetViewer();
      const originalRender = native.Render.bind(native);
      const updateClipping = () => {
        const sphere = fittedSphere;
        if (!sphere || !Number.isFinite(sphere.radius) || sphere.radius <= 0) return;
        const camera = native.GetCamera();
        const dx = camera.center.x - camera.eye.x;
        const dy = camera.center.y - camera.eye.y;
        const dz = camera.center.z - camera.eye.z;
        const distance = Math.hypot(dx, dy, dz);
        if (!Number.isFinite(distance) || distance <= 0) return;
        // Use depth along the view direction: panning can move the sphere away
        // from the orbit target without moving it farther along the camera axis.
        const depth = ((sphere.center.x - camera.eye.x) * dx +
          (sphere.center.y - camera.eye.y) * dy + (sphere.center.z - camera.eye.z) * dz) / distance;
        const margin = sphere.radius * 1.05;
        const near = Math.max(Number.MIN_VALUE, sphere.radius * 0.0001, depth - margin);
        const far = Math.max(near * 2, depth + margin);
        if (!Number.isFinite(near) || !Number.isFinite(far) || far <= near) return;
        if (native.camera.near !== near || native.camera.far !== far) {
          native.camera.near = near;
          native.camera.far = far;
          native.cameraValidator.ForceUpdate();
        }
      };
      renderNow = () => { updateClipping(); originalRender(); };
      native.Render = requestRender;
      // OV's world-unit clipping bands hide millimetre models expressed in
      // metres and very large scenes. Keep the planes relative to this model,
      // including after manual zoom, without adding an animation loop.
      native.AdjustClippingPlanesToSphere = sphere => {
        if (sphere) fittedSphere = sphere;
        updateClipping();
        requestRender();
      };
      const originalResize = native.ResizeRenderer.bind(native);
      const originalSetRatio = native.renderer.setPixelRatio.bind(native.renderer);
      native.GetCanvasSize = () => ({
        width: native.renderer.domElement.width / native.renderer.getPixelRatio(),
        height: native.renderer.domElement.height / native.renderer.getPixelRatio()
      });
      let drawingWidth = Math.max(1, viewport.clientWidth);
      let drawingHeight = Math.max(1, viewport.clientHeight);
      native.renderer.setPixelRatio = ratio => originalSetRatio(drawingRatio(ratio, drawingWidth, drawingHeight));
      resizeNow = (width, height) => {
        drawingWidth = width;
        drawingHeight = height;
        originalResize(width, height);
      };
      native.ResizeRenderer = requestResize;
      viewer.Resize = requestResize;
      const originalBounds = native.GetBoundingSphere.bind(native);
      native.GetBoundingSphere = predicate => {
        // Rendering is deferred, but imported roots (notably Z-up STL) have
        // already been rotated. Propagate those transforms before caching bounds.
        native.scene.updateMatrixWorld(true);
        const sphere = originalBounds(predicate);
        if (sphere) fittedSphere = sphere;
        return sphere;
      };
      // Intercept the successful import BEFORE OV expands it into Three.js
      // geometry/material arrays. The pinned vendor bundle remains unmodified.
      const importer = viewer.modelLoader.GetImporter();
      const originalImport = importer.ImportFiles.bind(importer);
      importer.ImportFiles = (files, settings, callbacks) => originalImport(files, settings, {
        ...callbacks,
        onImportSuccess: result => {
          if (disposed) return;
          if (!withinBudget(result.model)) { fail('complexity'); return; }
          callbacks.onImportSuccess(result);
        }
      });
      resizeObserver = new ResizeObserver(requestResize);
      resizeObserver.observe(viewport);
      viewer.LoadModelFromUrlList(config.modelUrls);
      requestResize();
    }).catch(error => { if (error.name !== 'AbortError') fail(); });
  fit.addEventListener('click', () => {
    fitMode = true;
    fitDirty = true;
    requestResize();
  });
  edges.addEventListener('click', () => {
    if (!loaded || edges.disabled || disposed) return;
    const show = edges.getAttribute('aria-pressed') !== 'true';
    edges.setAttribute('aria-pressed', String(show));
    viewer.GetViewer().SetEdgeSettings(new OV.EdgeSettings(show,
      light ? new OV.RGBColor(42, 52, 68) : new OV.RGBColor(218, 226, 239), 30));
  });
  window.addEventListener('pagehide', destroy, { once: true });
})();
