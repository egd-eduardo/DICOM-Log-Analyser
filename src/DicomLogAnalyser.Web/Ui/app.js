// @ts-check
// DICOM Log Analyser UI. Plain JavaScript, no external libraries or network calls other than this application's API.
// All log content is inserted with textContent (never as HTML).

/** @type {{csrfToken: string, mode: string, identity: string, version: string, platform: string, sourceRoots: string[], maxUploadMb: number, scanIntervalMinutes: number, formats: {id: string, name: string}[]} | null} */
let session = null;
/** @type {any[]} */
let sourcesCache = [];
/** @type {string | null} */
let nextCursor = null;
/** @type {RegExp | null} */
let highlightRegex = null;
/** @type {number | null} */
let currentEventId = null;

const HIGHLIGHT_PRESETS = [
  { name: 'amber', color: '#ffd34d' },
  { name: 'cyan', color: '#3ee6e6' },
  { name: 'lime', color: '#9be15d' },
  { name: 'magenta', color: '#ff6bd6' },
  { name: 'orange', color: '#ff9944' },
];

/** @param {string} id @returns {any} */
const $ = (id) => /** @type {any} */ (document.getElementById(id));

/** @param {string} tag @param {string} [cls] @param {string | number | null} [text] @returns {any} */
function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined && text !== null) e.textContent = String(text);
  return e;
}

// ---------- Preferences (stored only in this browser) ----------

/** @param {string} key @param {string} fallback */
function pref(key, fallback) {
  try { return localStorage.getItem('dla.' + key) ?? fallback; } catch { return fallback; }
}
/** @param {string} key @param {string} value */
function savePref(key, value) {
  try { localStorage.setItem('dla.' + key, value); } catch { /* storage unavailable: preference lasts for this page only */ }
}

/** @param {string} theme */
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  $('theme-toggle').textContent = theme === 'dark' ? 'Light' : 'Dark';
  savePref('theme', theme);
}

/** Text colour (black/white) with the better contrast on the chosen highlight colour. @param {string} hex */
function contrastText(hex) {
  const m = /^#([0-9a-f]{6})$/i.exec(hex);
  if (!m) return '#111111';
  const n = parseInt(m[1], 16);
  const lin = (/** @type {number} */ c) => { const s = c / 255; return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4); };
  const L = 0.2126 * lin((n >> 16) & 255) + 0.7152 * lin((n >> 8) & 255) + 0.0722 * lin(n & 255);
  return L > 0.35 ? '#111111' : '#ffffff';
}

/** @param {string} color */
function applyHighlight(color) {
  if (!/^#[0-9a-f]{6}$/i.test(color)) color = HIGHLIGHT_PRESETS[0].color;
  document.documentElement.style.setProperty('--hl-bg', color);
  document.documentElement.style.setProperty('--hl-fg', contrastText(color));
  $('hl-custom').value = color;
  for (const b of document.querySelectorAll('.swatch')) {
    b.classList.toggle('selected', /** @type {HTMLElement} */ (b).dataset.color?.toLowerCase() === color.toLowerCase());
  }
  savePref('highlight', color);
}

function setupPreferences() {
  const sw = $('swatches');
  for (const p of HIGHLIGHT_PRESETS) {
    const b = el('button', 'swatch swatch-' + p.name);
    b.type = 'button';
    b.dataset.color = p.color;
    b.title = 'Highlight in ' + p.name;
    b.setAttribute('aria-label', 'Highlight in ' + p.name);
    b.addEventListener('click', () => applyHighlight(p.color));
    sw.append(b);
  }
  $('hl-custom').addEventListener('input', (/** @type {any} */ e) => applyHighlight(e.target.value));
  $('theme-toggle').addEventListener('click', () => applyTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark'));
  applyTheme(pref('theme', 'dark'));
  applyHighlight(pref('highlight', HIGHLIGHT_PRESETS[0].color));
}

// ---------- Highlighting ----------

/** Builds a regular expression matching the letter/digit tokens of the positive search terms (as the index does). @param {string} q */
function buildHighlight(q) {
  /** @type {string[]} */
  const tokens = [];
  const re = /"([^"]*)"|(\S+)/g;
  let m;
  while ((m = re.exec(q)) !== null) {
    let term = m[1] ?? m[2];
    if (m[2] && term.startsWith('-') && term.length > 1) continue;
    term = term.replace(/\*$/, '');
    for (const t of term.split(/[^\p{L}\p{N}]+/u)) if (t.length > 0) tokens.push(t);
  }
  if (tokens.length === 0) return null;
  const unique = [...new Set(tokens.map((t) => t.toLowerCase()))].sort((a, b) => b.length - a.length).slice(0, 64);
  const escaped = unique.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  return new RegExp('(?<![\\p{L}\\p{N}])(' + escaped.join('|') + ')', 'giu');
}

/** Appends text to a node, wrapping matches in <mark>. @param {HTMLElement} node @param {string} text */
function appendHighlighted(node, text) {
  if (!highlightRegex || !text) { node.append(text ?? ''); return; }
  highlightRegex.lastIndex = 0;
  let last = 0;
  let m;
  let guard = 0;
  while ((m = highlightRegex.exec(text)) !== null && guard++ < 5000) {
    if (m.index > last) node.append(text.slice(last, m.index));
    node.append(el('mark', '', m[0]));
    last = m.index + m[0].length;
    if (m[0].length === 0) highlightRegex.lastIndex++;
  }
  node.append(text.slice(last));
}

// ---------- API ----------

/** @param {string} path @param {RequestInit} [options] */
async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  const method = (options.method || 'GET').toUpperCase();
  if (method !== 'GET' && session) headers.set('X-CSRF-Token', session.csrfToken);
  if (options.body && typeof options.body === 'string') headers.set('Content-Type', 'application/json');
  const res = await fetch(path, { ...options, headers, credentials: 'same-origin', cache: 'no-store' });
  if (res.status === 401) { showSignIn(); throw new Error('Not signed in.'); }
  const type = res.headers.get('Content-Type') || '';
  const body = type.includes('application/json') ? await res.json() : await res.text();
  if (!res.ok) {
    const msg = body && typeof body === 'object' ? (body.messages ? body.messages.join(' ') : body.message || body.error) : String(body);
    throw new Error(msg || ('Request failed (' + res.status + ').'));
  }
  return body;
}

/** @param {string} text @param {boolean} [info] */
function notify(text, info = false) {
  const n = $('notice');
  n.textContent = text;
  n.classList.toggle('info', info);
  n.classList.remove('hidden');
  window.clearTimeout(Number(n.dataset.timer || 0));
  n.dataset.timer = String(window.setTimeout(() => n.classList.add('hidden'), info ? 6000 : 12000));
}

function showSignIn() {
  for (const p of document.querySelectorAll('.panel')) p.classList.add('hidden');
  $('tab-signin').classList.remove('hidden');
}

// ---------- Formatting ----------

/** @param {number} n */
const num = (n) => Number(n || 0).toLocaleString('en-GB');
/** @param {number} b */
function bytes(b) {
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0; let v = Number(b || 0);
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return v.toFixed(i === 0 ? 0 : 1) + ' ' + u[i];
}
/** @param {number | null} ms */
function utc(ms) {
  if (ms === null || ms === undefined) return '';
  return new Date(ms).toISOString().replace('T', ' ').replace('Z', '').slice(0, 23);
}
/** Time cell text: UTC when the zone is known, otherwise the time exactly as written in the log. @param {any} e */
function eventTime(e) {
  if (e.timestamp === null || e.timestamp === undefined) return { text: '(no time)', note: 'This block of lines has no timestamp.' };
  if (e.utcKnown) return { text: utc(e.timestamp) + ' UTC', note: e.timestampText ? 'Written in the log as ' + e.timestampText : '' };
  return { text: (e.timestampText || utc(e.timestamp)) + ' local', note: 'Local time of the logging computer; its time zone is not configured for this source.' };
}
/** @param {string} level */
function levelBadge(level) { return el('span', 'lvl lvl-' + level, level === 'unknown' ? 'UNPARSED' : level.toUpperCase()); }
/** @param {string | null} iso */
const when = (iso) => (iso ? iso.replace('T', ' ').slice(0, 19) + ' UTC' : 'never');

// ---------- Tabs ----------

const loaders = /** @type {Record<string, () => Promise<void>>} */ ({
  overview: loadOverview,
  search: async () => { await loadSources(); $('q').focus(); },
  sources: loadSources,
  import: loadSources,
  maintenance: loadMaintenance,
});

/** @param {string} name */
function showTab(name) {
  for (const b of document.querySelectorAll('#tabs button')) b.classList.toggle('active', /** @type {HTMLElement} */ (b).dataset.tab === name);
  for (const p of document.querySelectorAll('.panel')) p.classList.toggle('hidden', p.id !== 'tab-' + name);
  loaders[name]?.().catch((e) => notify(e.message));
}

// ---------- Overview ----------

async function loadOverview() {
  const ov = await api('/api/overview');
  const cards = $('ov-cards');
  cards.replaceChildren();
  /** @param {string} label @param {number} n @param {string} [cls] */
  const card = (label, n, cls = '') => { const c = el('div', 'card ' + cls); c.append(el('div', 'n', num(n)), el('div', 'l', label)); cards.append(c); };
  card('Events indexed', ov.events);
  card('Errors', ov.errors, 'err');
  card('Warnings', ov.warnings, 'wrn');
  card('Events with exception traces', ov.exceptions);
  card('Unparsed line blocks', ov.unparsed);
  card('Sources', ov.sources.length);

  const gaps = [];
  if (ov.sources.length === 0) gaps.push('No sources are configured. Add a folder under Sources or import a bundle.');
  for (const s of ov.sources) {
    if (!s.enabled) gaps.push(`"${s.label}" is disabled; its events are not updated.`);
    if (!s.lastScanUtc) gaps.push(`"${s.label}" has not been scanned yet.`);
    else if (s.lastScanStatus === 'unreachable') gaps.push(`"${s.label}" was unreachable at the last scan (${when(s.lastScanUtc)}). Events after its last successful scan are missing.`);
    if (s.hasUnzonedTimes) gaps.push(`"${s.label}" has local times without a time zone. Set its time zone so its events line up with other sources.`);
    if (s.problemFiles > 0) gaps.push(`"${s.label}" has ${s.problemFiles} file(s) that are unreadable, missing or unrecognised.`);
  }
  const gapBox = $('ov-gaps');
  gapBox.replaceChildren();
  if (gaps.length) {
    const box = el('div', 'gaps');
    box.append(el('strong', '', 'Gaps that can make results incomplete'));
    const ul = el('ul');
    for (const g of gaps) ul.append(el('li', '', g));
    box.append(ul);
    gapBox.append(box);
  }

  fillTable('ov-sources', ov.sources, (s) => [
    s.label + (s.kind === 'bundle' ? ' (bundle)' : ''),
    statusCell(s.enabled ? s.lastScanStatus : 'disabled', s.lastScanMessage),
    num(s.events), num(s.errors), num(s.warnings),
    s.firstTs ? utc(s.firstTs).slice(0, 16) + ' → ' + utc(s.lastTs).slice(0, 16) + (s.hasUnzonedTimes ? ' (local)' : ' UTC') : '—',
    num(s.files) + (s.problemFiles ? ` (${s.problemFiles} with problems)` : ''),
    when(s.lastScanUtc),
  ]);

  const days = $('ov-days');
  days.replaceChildren();
  const max = Math.max(1, ...ov.errorsPerDay.map((/** @type {any} */ d) => d.errors + d.warnings));
  if (!ov.errorsPerDay.length) days.append(el('div', 'muted', 'No errors or warnings indexed.'));
  for (const d of ov.errorsPerDay) {
    const row = el('div', 'bar');
    const track = el('div', 'track');
    const e = el('div', 'e'); e.style.width = (100 * d.errors / max) + '%';
    const w = el('div', 'w'); w.style.width = (100 * d.warnings / max) + '%';
    track.append(e, w);
    row.append(el('span', '', d.day), track, el('span', '', num(d.errors) + ' / ' + num(d.warnings)));
    row.title = `${d.day}: ${d.errors} errors, ${d.warnings} warnings (by UTC day where known)`;
    days.append(row);
  }

  fillTable('ov-cats', ov.categories, (c) => [linkSearch(c.category, { category: c.category }), num(c.events), num(c.errors), num(c.warnings)]);
  fillTable('ov-top', ov.topErrors, (t) => [num(t.count), t.category, linkEvent(t.signature, t.exampleId), utc(t.lastSortTs).slice(0, 16)]);
  fillTable('ov-problems', ov.problemFiles, (p) => [p.sourceLabel, p.file, statusCell(p.status, null), num(p.unparsedLines), p.message || (p.unparsedLines ? 'Some lines did not match the ' + (p.format || '') + ' format and are kept as unparsed blocks.' : '')]);
}

/** @param {string | null} status @param {string | null} message */
function statusCell(status, message) {
  const s = status || 'not scanned';
  const cls = s === 'ok' ? 'ok-text' : (s === 'unreachable' || s === 'unreadable') ? 'err-text' : 'warn-text';
  const span = el('span', cls, s);
  if (message) span.title = message;
  return span;
}

/** @param {string} id @param {any[]} rows @param {(r: any) => (string | Node)[]} cells */
function fillTable(id, rows, cells) {
  const tbody = $(id).querySelector('tbody');
  tbody.replaceChildren();
  if (!rows.length) {
    const tr = el('tr'); const td = el('td', 'muted', 'Nothing to show.');
    td.colSpan = $(id).querySelectorAll('th').length; tr.append(td); tbody.append(tr); return;
  }
  for (const r of rows) {
    const tr = el('tr');
    for (const c of cells(r)) { const td = el('td'); td.append(c); tr.append(td); }
    tbody.append(tr);
  }
}

/** @param {string} text @param {{category?: string}} filters */
function linkSearch(text, filters) {
  const a = el('a', '', text); a.href = '#';
  a.addEventListener('click', (ev) => { ev.preventDefault(); $('q').value = ''; $('f-category').value = filters.category || ''; showTab('search'); runSearch(false); });
  return a;
}
/** @param {string} text @param {number} id */
function linkEvent(text, id) {
  const a = el('a', '', text); a.href = '#';
  a.addEventListener('click', (ev) => { ev.preventDefault(); highlightRegex = null; openEvent(id); });
  return a;
}

// ---------- Search ----------

function searchParams() {
  const p = new URLSearchParams();
  const q = $('q').value.trim();
  if (q) p.set('q', q);
  if ($('f-level').value) p.set('minLevel', $('f-level').value);
  if ($('f-source').value) p.set('sources', $('f-source').value);
  if ($('f-category').value.trim()) p.set('category', $('f-category').value.trim());
  if ($('f-from').value) p.set('from', $('f-from').value + 'Z');
  if ($('f-to').value) p.set('to', $('f-to').value + 'Z');
  if ($('f-exc').checked) p.set('exceptions', '1');
  if (!$('f-unparsed').checked) p.set('unparsed', '0');
  if ($('f-order').value === 'oldest') p.set('order', 'oldest');
  return p;
}

/** @param {boolean} append */
async function runSearch(append) {
  const params = searchParams();
  params.set('limit', '100');
  if (append && nextCursor) params.set('cursor', nextCursor);
  const res = await api('/api/search?' + params.toString());
  highlightRegex = buildHighlight($('q').value);
  const tbody = $('results').querySelector('tbody');
  if (!append) tbody.replaceChildren();
  for (const e of res.items) tbody.append(resultRow(e));
  nextCursor = res.nextCursor;
  $('more').classList.toggle('hidden', !nextCursor);
  const exp = $('export');
  params.delete('limit'); params.delete('cursor');
  exp.href = '/api/export.csv?' + params.toString();
  exp.classList.toggle('hidden', res.matchCount === 0);
  if (!append) renderSummary(res, params.has('q'));
}

/** @param {any} e */
function resultRow(e) {
  const tr = el('tr');
  const t = eventTime(e);
  const tdTime = el('td', 'time', t.text); tdTime.title = t.note;
  const tdLvl = el('td'); tdLvl.append(levelBadge(e.level));
  const tdMsg = el('td', 'msg');
  appendHighlighted(tdMsg, e.message);
  if (e.hasException) tdMsg.append(el('span', 'muted small', '  [exception trace]'));
  else if (e.hasDetail) tdMsg.append(el('span', 'muted small', '  [+ more lines]'));
  const tdSrc = el('td', '', e.sourceLabel); tdSrc.title = e.file + ' line ' + e.lineStart;
  tr.append(tdTime, tdLvl, tdSrc, el('td', '', e.category), tdMsg);
  tr.tabIndex = 0;
  tr.addEventListener('click', () => openEvent(e.id));
  tr.addEventListener('keydown', (/** @type {KeyboardEvent} */ ev) => { if (ev.key === 'Enter') openEvent(e.id); });
  return tr;
}

/** @param {any} res @param {boolean} hasText */
function renderSummary(res, hasText) {
  const box = $('search-summary');
  box.replaceChildren();
  const c = res.levelCounts || {};
  const errors = (c.error || 0) + (c.fatal || 0);
  const warnings = c.warning || 0;
  const total = res.matchCount;
  const div = el('div', 'summary');
  if (res.queryIgnored) div.append(el('p', 'warn-text', 'The search text contains no letters or digits, so it was ignored.'));
  if (total === 0) {
    div.classList.add('none');
    div.append(el('p', '', 'No matching event found in the indexed logs.'));
    div.append(el('p', 'muted', 'This does not mean that a transfer failed, nor that it succeeded. The events may be in a source that is not configured, disabled, unreachable or not yet scanned, outside the indexed period, or in a log whose times are in another time zone.'));
  } else {
    const countText = (res.matchCountCapped ? 'More than ' : '') + num(total) + ' matching event(s)';
    if (errors > 0) {
      div.classList.add('bad');
      div.append(el('p', '', `${countText}, including ${num(errors)} error(s) and ${num(warnings)} warning(s).`));
      div.append(el('p', 'muted', hasText ? 'At least one error was logged for this search. Open the errors to read the cause, and check later events: a retry may have succeeded.' : 'Filter by text (for example an accession number or Study Instance UID) to follow a single transfer.'));
    } else if (warnings > 0) {
      div.classList.add('warn');
      div.append(el('p', '', `${countText}, including ${num(warnings)} warning(s) and no errors.`));
    } else {
      div.classList.add('good');
      div.append(el('p', '', `${countText}, none at warning or error level.`));
      if (hasText) div.append(el('p', 'muted', 'No failure was logged for this search in the indexed logs. This suggests, but does not prove, that the transfer succeeded: confirm on the receiving system.'));
    }
  }
  if (res.coverageNotes?.length) {
    const ul = el('ul');
    for (const n of res.coverageNotes) ul.append(el('li', 'small', n));
    const head = el('p', 'small warn-text', 'Coverage notes:');
    div.append(head, ul);
  }
  box.append(div);
}

/** @param {number} id */
async function openEvent(id) {
  const d = await api('/api/events/' + id);
  currentEventId = id;
  const e = d.event;
  const meta = $('d-meta');
  meta.replaceChildren();
  const t = eventTime(e);
  /** @param {string} k @param {string | Node} v */
  const add = (k, v) => { const dd = el('dd'); dd.append(v); meta.append(el('dt', '', k), dd); };
  add('Time', t.text + (t.note ? '. ' + t.note : ''));
  const lv = el('span'); lv.append(levelBadge(e.level), ' ' + (e.levelText || '')); add('Level', lv);
  add('Category', e.category + (e.component ? ' (' + e.component + ')' : ''));
  add('Source', e.sourceLabel + (d.sourceKind === 'bundle' ? ' (imported bundle)' : ''));
  add('File', e.file + (d.fileStatus !== 'ok' ? ' [' + d.fileStatus + ']' : ''));
  add('Location', `lines ${e.lineStart}–${e.lineEnd}, bytes ${d.byteStart}–${d.byteEnd}`);
  add('Format', d.format || 'unknown');
  const msg = $('d-message'); msg.replaceChildren(); appendHighlighted(msg, e.message);
  $('d-detail-wrap').classList.toggle('hidden', !d.detail);
  const det = $('d-detail'); det.replaceChildren(); if (d.detail) appendHighlighted(det, d.detail);
  const status = {
    verified: ['ok-text', 'Read again from the source file just now; the file has not been replaced since it was indexed.'],
    file_changed: ['warn-text', 'The source file has changed or was replaced since indexing, so the original bytes cannot be shown. The indexed copy is above; scan again to refresh.'],
    file_unavailable: ['warn-text', 'The source file is not reachable now (share offline or file removed). The indexed copy is above.'],
    too_large: ['muted', 'The record is too large to show here; the indexed copy is above.'],
  }[/** @type {string} */ (d.evidenceStatus)] || ['muted', d.evidenceStatus];
  $('d-evidence-status').className = 'small ' + status[0];
  $('d-evidence-status').textContent = status[1];
  const orig = $('d-original'); orig.replaceChildren(); if (d.originalText) appendHighlighted(orig, d.originalText);
  orig.classList.toggle('hidden', !d.originalText);
  $('d-prev').disabled = !d.previousId; $('d-prev').dataset.id = d.previousId || '';
  $('d-next').disabled = !d.nextId; $('d-next').dataset.id = d.nextId || '';
  const dlg = $('detail');
  if (!dlg.open) dlg.showModal();
}

// ---------- Sources ----------

async function loadSources() {
  sourcesCache = await api('/api/sources');
  const sel = $('f-source');
  const keep = sel.value;
  sel.replaceChildren(new Option('All sources', ''));
  for (const s of sourcesCache) sel.append(new Option(s.label + (s.kind === 'bundle' ? ' (bundle)' : ''), String(s.id)));
  sel.value = keep;
  fillTable('src-table', sourcesCache.filter((s) => s.kind === 'folder'), (s) => [
    s.label, 'Folder', s.path, s.timeZone || el('span', 'warn-text', 'not set'),
    s.enabled ? 'Yes' : 'No', statusLine(s), sourceActions(s),
  ]);
  fillTable('bundle-table', sourcesCache.filter((s) => s.kind === 'bundle'), (s) => [
    s.label, when(s.createdUtc), bytes(s.bundleBytes), (s.bundleSha256 || '').slice(0, 16) + '…', sourceActions(s),
  ]);
}

/** @param {any} s */
function statusLine(s) {
  const span = el('span');
  span.append(statusCell(s.lastScanStatus, s.lastScanMessage), ' ' + when(s.lastScanUtc));
  if (s.lastScanMessage) span.append(el('div', 'small muted', s.lastScanMessage));
  return span;
}

/** @param {any} s */
function sourceActions(s) {
  const wrap = el('div', 'row');
  const scan = el('button', 'ghost', 'Scan'); scan.type = 'button';
  scan.addEventListener('click', () => api('/api/scan', { method: 'POST', body: JSON.stringify({ sourceIds: [s.id] }) }).then(() => notify('Scan queued.', true)).catch((e) => notify(e.message)));
  wrap.append(scan);
  if (s.kind === 'folder') {
    const edit = el('button', 'ghost', 'Edit'); edit.type = 'button';
    edit.addEventListener('click', () => editSource(s));
    wrap.append(edit);
  }
  const del = el('button', 'danger', 'Delete'); del.type = 'button';
  del.addEventListener('click', async () => {
    const what = s.kind === 'bundle' ? 'the imported files and all their indexed events' : 'its indexed events (the original folder is not touched)';
    if (!window.confirm(`Delete "${s.label}" and ${what}?`)) return;
    try { await api('/api/sources/' + s.id, { method: 'DELETE' }); notify('Deletion queued.', true); pollScan(); } catch (e) { notify(/** @type {Error} */ (e).message); }
  });
  wrap.append(del);
  return wrap;
}

/** @param {any} s */
function editSource(s) {
  $('s-id').value = s.id; $('s-label').value = s.label; $('s-path').value = s.path; $('s-path').disabled = true;
  $('s-tz').value = s.timeZone || ''; $('s-format').value = s.formatHint; $('s-patterns').value = s.includePatterns;
  $('s-recursive').checked = s.recursive; $('s-enabled').checked = s.enabled;
  $('src-form-title').textContent = 'Edit source'; $('s-save').textContent = 'Save'; $('s-cancel').classList.remove('hidden');
  $('s-result').replaceChildren();
  $('src-form').scrollIntoView({ behavior: 'smooth' });
}

function resetSourceForm() {
  $('src-form').reset(); $('s-id').value = ''; $('s-path').disabled = false;
  $('src-form-title').textContent = 'Add a folder source'; $('s-save').textContent = 'Add source and scan'; $('s-cancel').classList.add('hidden');
}

/** @param {any} v */
function renderValidation(v) {
  const box = $('s-result');
  box.replaceChildren();
  const ul = el('ul', 'plain');
  for (const e of v.errors || []) ul.append(el('li', 'err-text', e));
  for (const w of v.warnings || []) ul.append(el('li', 'warn-text', w));
  for (const i of v.info || []) ul.append(el('li', 'ok-text', i));
  if (v.errors && v.errors.length === 0) ul.prepend(el('li', 'ok-text', 'The folder is reachable and readable from the analyser.'));
  box.append(ul);
}

function sourceBody() {
  return {
    label: $('s-label').value, path: $('s-path').value, enabled: $('s-enabled').checked, recursive: $('s-recursive').checked,
    includePatterns: $('s-patterns').value, timeZone: $('s-tz').value, formatHint: $('s-format').value, scanNow: true,
  };
}

// ---------- Scan status ----------

let pollTimer = 0;
async function pollScan() {
  window.clearTimeout(pollTimer);
  try {
    const s = await api('/api/scan/status');
    const bar = $('scanbar');
    if (s.state === 'running') {
      bar.classList.remove('hidden');
      bar.textContent = `${s.operation}${s.sourceLabel ? ' "' + s.sourceLabel + '"' : ''}: ${s.filesDone}/${s.filesTotal} files, ${bytes(s.bytesRead)} read, ${num(s.eventsAdded)} events indexed${s.queued ? ', ' + s.queued + ' more queued' : ''}.`;
      pollTimer = window.setTimeout(pollScan, 1500);
    } else {
      if (!bar.classList.contains('hidden') && s.lastOutcome) { notify(s.lastOutcome, true); refreshVisible(); }
      bar.classList.add('hidden');
      pollTimer = window.setTimeout(pollScan, 10000);
    }
  } catch { pollTimer = window.setTimeout(pollScan, 15000); }
}

function refreshVisible() {
  const active = document.querySelector('#tabs button.active');
  const name = active ? /** @type {HTMLElement} */ (active).dataset.tab : 'overview';
  if (name && name !== 'search') loaders[name]?.().catch(() => {});
}

// ---------- Import ----------

function upload() {
  const file = $('i-file').files[0];
  if (!file) return;
  if (session && file.size > session.maxUploadMb * 1024 * 1024) { notify(`The file is larger than the ${session.maxUploadMb} MB limit.`); return; }
  const p = new URLSearchParams({ label: $('i-label').value, timeZone: $('i-tz').value, name: file.name });
  const xhr = new XMLHttpRequest();
  xhr.open('POST', '/api/bundles?' + p.toString());
  xhr.setRequestHeader('X-CSRF-Token', session ? session.csrfToken : '');
  xhr.setRequestHeader('Content-Type', 'application/octet-stream');
  const prog = $('i-progress'); prog.classList.remove('hidden'); prog.value = 0;
  xhr.upload.onprogress = (ev) => { if (ev.lengthComputable) prog.value = Math.round(100 * ev.loaded / ev.total); };
  xhr.onload = () => {
    prog.classList.add('hidden');
    const box = $('i-result'); box.replaceChildren();
    let body = null; try { body = JSON.parse(xhr.responseText); } catch { /* not JSON */ }
    if (xhr.status >= 200 && xhr.status < 300 && body) {
      box.append(el('p', 'ok-text', `Imported ${body.filesExtracted} file(s) (${bytes(body.bytesExtracted)}). Indexing has started.`));
      if (body.skipped.length) box.append(el('p', 'warn-text small', `${body.skipped.length} file(s) were skipped because they are not log files: ` + body.skipped.slice(0, 10).join(', ') + (body.skipped.length > 10 ? ', …' : '')));
      $('imp-form').reset(); loadSources(); pollScan();
    } else {
      box.append(el('p', 'err-text', (body && (body.message || body.error)) || ('Upload failed (' + xhr.status + ').')));
    }
  };
  xhr.onerror = () => { prog.classList.add('hidden'); notify('The upload was interrupted.'); };
  xhr.send(file);
}

// ---------- Maintenance ----------

async function loadMaintenance() {
  const m = await api('/api/maintenance');
  const info = $('m-info'); info.replaceChildren();
  /** @param {string} l @param {string} v */
  const card = (l, v) => { const c = el('div', 'card'); c.append(el('div', 'n', v), el('div', 'l', l)); info.append(c); };
  card('Index database', bytes(m.databaseBytes));
  card('Imported bundles', bytes(m.bundlesBytes));
  card('Temporary files', `${m.tempEntries} (${bytes(m.tempBytes)})`);
  const about = $('m-about'); about.replaceChildren();
  if (session) {
    about.append(el('li', '', `Version ${session.version}, ${session.mode === 'server' ? 'central mode (identity from the access proxy)' : 'local mode (this computer only)'}.`));
    about.append(el('li', '', 'Signed in as ' + session.identity + '.'));
    if (session.sourceRoots.length) about.append(el('li', '', 'Folder sources must be inside: ' + session.sourceRoots.join(', ')));
    about.append(el('li', '', session.scanIntervalMinutes ? `Automatic scan every ${session.scanIntervalMinutes} minutes.` : 'Automatic scans are off; scan from the Sources page.'));
  }
  about.append(el('li', '', 'Data folder: ' + m.dataDirectory));
  about.append(el('li', '', 'No telemetry, no online services and no AI: logs and results stay in the data folder.'));
  fillTable('m-audit', m.audit, (a) => [a.timestampUtc.replace('T', ' ').slice(0, 19), a.actor, a.action, a.detail || '']);
}

// ---------- Start ----------

async function start() {
  setupPreferences();
  for (const b of document.querySelectorAll('#tabs button')) b.addEventListener('click', () => showTab(/** @type {HTMLElement} */ (b).dataset.tab || 'overview'));
  $('ov-refresh').addEventListener('click', () => loadOverview().catch((e) => notify(e.message)));
  $('search-form').addEventListener('submit', (/** @type {Event} */ ev) => { ev.preventDefault(); runSearch(false).catch((e) => notify(e.message)); });
  $('more').addEventListener('click', () => runSearch(true).catch((e) => notify(e.message)));
  $('d-close').addEventListener('click', () => $('detail').close());
  for (const id of ['d-prev', 'd-next']) $(id).addEventListener('click', () => { const v = Number($(id).dataset.id); if (v) openEvent(v).catch((e) => notify(e.message)); });
  $('scan-all').addEventListener('click', () => api('/api/scan', { method: 'POST', body: '{}' }).then(() => { notify('Scan queued.', true); pollScan(); }).catch((e) => notify(e.message)));
  $('scan-cancel').addEventListener('click', () => api('/api/scan/cancel', { method: 'POST', body: '{}' }).catch((e) => notify(e.message)));
  $('s-validate').addEventListener('click', () => api('/api/sources/validate', { method: 'POST', body: JSON.stringify({ path: $('s-path').value }) }).then(renderValidation).catch((e) => notify(e.message)));
  $('s-cancel').addEventListener('click', resetSourceForm);
  $('src-form').addEventListener('submit', async (/** @type {Event} */ ev) => {
    ev.preventDefault();
    const id = $('s-id').value;
    try {
      if (id) {
        const r = await api('/api/sources/' + id, { method: 'PUT', body: JSON.stringify(sourceBody()) });
        notify(r.reindexed ? 'Saved. The time zone or format changed, so the source is being re-indexed.' : 'Saved.', true);
        resetSourceForm();
      } else {
        const r = await api('/api/sources', { method: 'POST', body: JSON.stringify(sourceBody()) });
        renderValidation(r.validation);
        notify('Source added. The first scan has started.', true);
        resetSourceForm();
      }
      await loadSources(); pollScan();
    } catch (e) { notify(/** @type {Error} */ (e).message); }
  });
  $('imp-form').addEventListener('submit', (/** @type {Event} */ ev) => { ev.preventDefault(); upload(); });
  $('m-clean').addEventListener('click', () => api('/api/maintenance/clean-temp', { method: 'POST', body: '{}' }).then((r) => { notify(r.remaining ? `${r.remaining} item(s) could not be removed.` : 'Temporary files deleted.', true); loadMaintenance(); }).catch((e) => notify(e.message)));
  $('m-purge').addEventListener('click', async () => {
    const typed = window.prompt('This deletes every indexed event (source settings are kept, original logs are not touched). Type PURGE to confirm.');
    if (typed !== 'PURGE') return;
    try { await api('/api/maintenance/purge-index', { method: 'POST', body: JSON.stringify({ confirm: 'PURGE' }) }); notify('Purge queued.', true); pollScan(); } catch (e) { notify(/** @type {Error} */ (e).message); }
  });

  try {
    session = await api('/api/session');
  } catch { return; }
  const fmt = $('s-format');
  for (const f of session.formats) fmt.append(new Option(f.name, f.id));
  const tzl = $('tz-list');
  try { for (const z of /** @type {any} */ (Intl).supportedValuesOf('timeZone')) tzl.append(new Option(z)); } catch { /* older browser: free text only */ }
  $('s-path-help').textContent = session.mode === 'server'
    ? 'Container path inside ' + session.sourceRoots.join(' or ') + ' (a read-only volume in compose.yaml). Windows drive letters and UNC paths cannot be used directly here.'
    : session.platform === 'windows' ? 'Examples: C:\\3dnet\\DicomRouter\\logs, Z:\\3dnet\\audits\\Gateway\\0 or \\\\server\\share\\logs'
      : 'Examples: /Volumes/ClinicalLogs/Gateway/US or /var/log/dicomrouter';
  $('i-limit').textContent = `Maximum upload size: ${session.maxUploadMb} MB. Archives are checked for unsafe paths, links and decompression bombs before anything is kept.`;
  if (new URLSearchParams(location.search).has('expired')) notify('That sign-in link was already used or has expired. Press O in the analyser window for a new one.');
  showTab('overview');
  pollScan();
}

start();
