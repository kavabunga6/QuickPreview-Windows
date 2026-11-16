'use strict';
(() => {
  const query = new URLSearchParams(location.search);
  document.documentElement.dataset.theme = query.get('theme') === 'light' ? 'light' : 'dark';
  const scroll = document.getElementById('sheet-scroll');
  const scale = document.getElementById('sheet-scale');
  const table = document.getElementById('sheet-table');
  const tabs = document.getElementById('sheet-tabs');
  const info = document.getElementById('sheet-info');
  const limits = document.getElementById('limit-state');
  const status = document.getElementById('status');
  const minus = document.getElementById('zoom-out');
  const plus = document.getElementById('zoom-in');
  const reset = document.getElementById('zoom-reset');
  const request = new AbortController();
  const positions = new Map();
  let workbook = null;
  let sheetIndex = -1;
  let naturalWidth = 1;
  let zoom = 1;
  let zoomMode = 'auto';
  let selected = null;
  let resizeFrame = 0;
  let disposed = false;
  const post = value => window.chrome?.webview?.postMessage(value);
  const clamp = (value, min, max) => Math.min(max, Math.max(min, Number(value) || min));
  const color = value => typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value) ? value : null;
  const coordinate = (row, column) => `${row}:${column}`;
  const columnName = index => {
    let result = '';
    for (let value = index + 1; value > 0; value = Math.floor((value - 1) / 26))
      result = String.fromCharCode(65 + (value - 1) % 26) + result;
    return result;
  };
  const fail = () => {
    if (disposed) return;
    status.textContent = 'Не удалось отобразить таблицу. Откройте файл в офисном приложении.';
    status.hidden = false;
    minus.disabled = plus.disabled = reset.disabled = true;
    document.getElementById('fit').disabled = true;
    post({ type: 'error' });
  };

  const applyZoom = value => {
    zoom = clamp(value, 0.4, 2.5);
    scale.style.zoom = String(zoom);
    reset.textContent = Math.round(zoom * 100) + '%';
    reset.setAttribute('aria-label', `Масштаб ${Math.round(zoom * 100)} процентов. Сбросить до 100 процентов`);
    minus.disabled = zoom <= 0.4001;
    plus.disabled = zoom >= 2.4999;
  };
  const fitWidth = () => {
    if (scroll.clientWidth <= 0 || !workbook) return;
    const padding = parseFloat(getComputedStyle(scroll).paddingLeft) + parseFloat(getComputedStyle(scroll).paddingRight);
    applyZoom(Math.max(zoomMode === 'auto' ? 0.7 : 0.4,
      Math.min(1, (scroll.clientWidth - padding - 2) / naturalWidth)));
  };
  const setZoom = value => { zoomMode = 'manual'; applyZoom(value); };
  const borderStyle = value => ({ thin: '1px solid', medium: '2px solid', thick: '3px solid',
    dashed: '1px dashed', dotted: '1px dotted', double: '3px double' })[value];
  const applyStyle = (cell, content, style) => {
    if (!style) return;
    if (typeof style.fontName === 'string' && style.fontName.length <= 64)
      cell.style.fontFamily = JSON.stringify(style.fontName) + ', Arial, sans-serif';
    cell.style.fontSize = (clamp(style.fontSize || 10, 6, 48) * 4 / 3) + 'px';
    if (style.bold) cell.style.fontWeight = 'bold';
    if (style.italic) cell.style.fontStyle = 'italic';
    if (style.underline) cell.style.textDecoration = 'underline';
    cell.style.color = color(style.foreground) || '#1b1c1f';
    cell.style.backgroundColor = color(style.background) || '#ffffff';
    cell.style.textAlign = ['left', 'center', 'right'].includes(style.horizontalAlignment) ? style.horizontalAlignment : 'left';
    cell.style.verticalAlign = ({ top: 'top', center: 'middle', bottom: 'bottom' })[style.verticalAlignment] || 'bottom';
    content.classList.toggle('wrap', !!style.wrapText);
    for (const side of ['Left', 'Top', 'Right', 'Bottom']) {
      const border = borderStyle(style['border' + side]);
      if (border) cell.style['border' + side] = border + ' #525964';
    }
  };

  const selectSheet = index => {
    if (!workbook || disposed || !Number.isInteger(index) || index < 0 || index >= workbook.sheets.length) return;
    if (sheetIndex >= 0) positions.set(sheetIndex, { left: scroll.scrollLeft, top: scroll.scrollTop });
    sheetIndex = index;
    selected = null;
    const sheet = workbook.sheets[index];
    const columns = new Map((sheet.columns || []).filter(item => item.index >= 0 && item.index < 80).map(item => [item.index, item]));
    const rows = new Map((sheet.rows || []).filter(item => item.index >= 0 && item.index < 200).map(item => [item.index, item]));
    const cellMap = new Map();
    let lastColumn = 0;
    let lastRow = 0;
    for (const row of rows.values()) {
      lastRow = Math.max(lastRow, row.index);
      for (const cell of (row.cells || []).slice(0, 80)) {
        if (cell.column < 0 || cell.column >= 80) continue;
        cellMap.set(coordinate(row.index, cell.column), cell);
        lastColumn = Math.max(lastColumn, cell.column);
      }
    }
    for (const column of columns.values()) lastColumn = Math.max(lastColumn, column.index);
    const merges = (sheet.merges || []).filter(item => item.row >= 0 && item.column >= 0 &&
      item.row < 200 && item.column < 80 && item.rowSpan > 0 && item.columnSpan > 0).slice(0, 16000);
    for (const merge of merges) {
      lastRow = Math.max(lastRow, Math.min(199, merge.row + merge.rowSpan - 1));
      lastColumn = Math.max(lastColumn, Math.min(79, merge.column + merge.columnSpan - 1));
    }
    const visibleColumns = Array.from({ length: lastColumn + 1 }, (_, i) => i).filter(i => !columns.get(i)?.hidden);
    const visibleRows = Array.from({ length: lastRow + 1 }, (_, i) => i).filter(i => !rows.get(i)?.hidden);
    const anchors = new Map();
    const covered = new Set();
    const occupied = new Set();
    let examinedMergeCells = 0;
    let skippedMerges = false;
    for (const merge of merges) {
      const mergeRows = visibleRows.filter(i => i >= merge.row && i < merge.row + merge.rowSpan);
      const mergeColumns = visibleColumns.filter(i => i >= merge.column && i < merge.column + merge.columnSpan);
      if (!mergeRows.length || !mergeColumns.length) continue;
      const key = coordinate(mergeRows[0], mergeColumns[0]);
      if (occupied.has(key)) { skippedMerges = true; continue; }
      examinedMergeCells += mergeRows.length * mergeColumns.length;
      if (examinedMergeCells > 64000) { skippedMerges = true; break; }
      if (mergeRows.some(row => mergeColumns.some(column => occupied.has(coordinate(row, column))))) {
        skippedMerges = true;
        continue;
      }
      anchors.set(key, { row: merge.row, column: merge.column, rowSpan: mergeRows.length, columnSpan: mergeColumns.length });
      for (const row of mergeRows) for (const column of mergeColumns) {
        occupied.add(coordinate(row, column));
        if (row !== mergeRows[0] || column !== mergeColumns[0]) covered.add(coordinate(row, column));
      }
    }
    const colgroup = document.createElement('colgroup');
    const rowNumbers = document.createElement('col');
    rowNumbers.style.width = '40px';
    colgroup.append(rowNumbers);
    naturalWidth = 40;
    for (const index of visibleColumns) {
      const width = clamp(columns.get(index)?.width || 80, 16, 800);
      const column = document.createElement('col');
      column.style.width = width + 'px';
      naturalWidth += width;
      colgroup.append(column);
    }
    const header = document.createElement('thead');
    const headerRow = document.createElement('tr');
    const corner = document.createElement('th');
    corner.setAttribute('aria-label', 'Номера строк');
    headerRow.append(corner);
    for (const index of visibleColumns) {
      const cell = document.createElement('th');
      cell.scope = 'col';
      cell.textContent = columnName(index);
      headerRow.append(cell);
    }
    header.append(headerRow);
    const body = document.createElement('tbody');
    for (const rowIndex of visibleRows) {
      const row = document.createElement('tr');
      row.style.height = clamp(rows.get(rowIndex)?.height || 22, 12, 800) + 'px';
      const number = document.createElement('th');
      number.scope = 'row';
      number.textContent = String(rowIndex + 1);
      row.append(number);
      for (const column of visibleColumns) {
        const key = coordinate(rowIndex, column);
        if (covered.has(key)) continue;
        const merge = anchors.get(key);
        const value = cellMap.get(merge ? coordinate(merge.row, merge.column) : key);
        const cell = document.createElement('td');
        if (merge) { cell.rowSpan = merge.rowSpan; cell.colSpan = merge.columnSpan; }
        const reference = columnName(merge?.column ?? column) + String((merge?.row ?? rowIndex) + 1);
        cell.dataset.reference = reference;
        const content = document.createElement('div');
        content.className = 'cell-content';
        content.textContent = value?.text || '';
        cell.title = reference + (value?.text ? ': ' + value.text : '');
        applyStyle(cell, content, workbook.styles[value?.styleIndex ?? 0]);
        cell.append(content);
        row.append(cell);
      }
      body.append(row);
    }
    table.replaceChildren(colgroup, header, body);
    table.style.width = naturalWidth + 'px';
    table.setAttribute('aria-label', sheet.name || `Лист ${index + 1}`);
    scroll.setAttribute('aria-label', `Таблица, лист ${sheet.name || index + 1}`);
    info.textContent = `${lastRow + 1} строк · ${lastColumn + 1} столбцов`;
    limits.textContent = workbook.isTruncated || sheet.isTruncated || skippedMerges ? 'Показана часть' : '';
    limits.title = limits.textContent ? 'Для просмотра всей книги откройте файл в офисном приложении.' : '';
    for (const [index, button] of Array.from(tabs.children).entries()) {
      button.setAttribute('aria-selected', String(index === sheetIndex));
      button.tabIndex = index === sheetIndex ? 0 : -1;
    }
    if (zoomMode !== 'manual') fitWidth();
    const position = positions.get(index);
    scroll.scrollLeft = position?.left || 0;
    scroll.scrollTop = position?.top || 0;
    status.hidden = true;
  };

  minus.addEventListener('click', () => setZoom(Math.round((zoom - 0.1) * 100) / 100));
  plus.addEventListener('click', () => setZoom(Math.round((zoom + 0.1) * 100) / 100));
  reset.addEventListener('click', () => setZoom(1));
  document.getElementById('fit').addEventListener('click', () => { zoomMode = 'fit'; fitWidth(); });
  table.addEventListener('click', event => {
    const cell = event.target.closest('td[data-reference]');
    if (!cell) return;
    selected?.classList.remove('selected');
    selected = cell;
    selected.classList.add('selected');
  });
  tabs.addEventListener('keydown', event => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const index = event.key === 'Home' ? 0 : event.key === 'End' ? workbook.sheets.length - 1 :
      Math.max(0, Math.min(workbook.sheets.length - 1, sheetIndex + (event.key === 'ArrowRight' ? 1 : -1)));
    selectSheet(index);
    tabs.children[index]?.focus();
  });
  const observer = new ResizeObserver(() => {
    if (resizeFrame || disposed || zoomMode === 'manual') return;
    resizeFrame = requestAnimationFrame(() => { resizeFrame = 0; if (!disposed) fitWidth(); });
  });
  observer.observe(scroll);
  window.addEventListener('pagehide', () => {
    disposed = true;
    request.abort();
    observer.disconnect();
    if (resizeFrame) cancelAnimationFrame(resizeFrame);
    workbook = null;
  }, { once: true });
  window.addEventListener('error', fail, { once: true });
  window.addEventListener('unhandledrejection', fail, { once: true });
  fetch('workbook.json?session=' + encodeURIComponent(query.get('session')), { signal: request.signal })
    .then(response => { if (!response.ok) throw new Error('Workbook unavailable'); return response.json(); })
    .then(value => {
      if (disposed) return;
      if (!Array.isArray(value.sheets) || !value.sheets.length || !Array.isArray(value.styles)) throw new Error('Empty workbook');
      workbook = value;
      for (const [index, sheet] of workbook.sheets.slice(0, 30).entries()) {
        const button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('role', 'tab');
        button.textContent = sheet.name || `Лист ${index + 1}`;
        button.title = button.textContent;
        button.addEventListener('click', () => selectSheet(index));
        tabs.append(button);
      }
      window.quickPreviewSpreadsheet = { get workbook() { return workbook; }, get sheetIndex() { return sheetIndex; },
        get zoom() { return zoom; }, selectSheet, setZoom };
      selectSheet(0);
      // The native host shows this WebView only after Ready. Do not wait for a
      // visible animation frame here, or a hidden viewer could deadlock opening.
      post({ type: 'loaded' });
    }).catch(error => { if (error.name !== 'AbortError') fail(); });
})();
