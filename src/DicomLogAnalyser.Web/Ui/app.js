// @ts-check
// DICOM Log Analyser UI. Plain JavaScript, no external libraries or network calls other than this application's API.
// All log content is inserted with textContent (never as HTML).

/** @type {{csrfToken: string, mode: string, role: string, identity: string, version: string, platform: string, sourceRoots: string[], maxUploadMb: number, scanIntervalMinutes: number, liveIntervalSeconds: number, folderDialog: boolean, formats: {id: string, name: string}[]} | null} */
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

/** Regular expression marking each searched text literally (case-insensitive), as the search matches substrings. @param {string} q */
function buildHighlight(q) {
  /** @type {string[]} */
  const terms = [];
  const re = /"([^"]*)"|(\S+)/g;
  let m;
  while ((m = re.exec(q)) !== null) {
    let term = m[1] ?? m[2];
    if (m[2] && term.startsWith('-') && term.length > 1) continue;
    if (m[2]) term = term.replace(/\*+$/, '');
    if (term.trim().length > 0) terms.push(term);
  }
  if (terms.length === 0) return null;
  const unique = [...new Set(terms.map((t) => t.toLowerCase()))].sort((a, b) => b.length - a.length).slice(0, 64);
  return new RegExp('(' + unique.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|') + ')', 'giu');
}

/** Appends text to a node, wrapping matches in <mark>. @param {HTMLElement} node @param {string} text @param {RegExp | null} [regex] */
function appendHighlighted(node, text, regex = highlightRegex) {
  if (!regex || !text) { node.append(text ?? ''); return; }
  const highlightRegex = regex;
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

/** Administrators configure, import, export and delete; viewers search and read. */
function isAdmin() { return session?.role === 'admin'; }

/** IANA time zone of this computer (from the browser), or '' when it cannot be determined. */
function localTimeZone() {
  try {
    const tz = Intl.DateTimeFormat().resolvedOptions().timeZone || '';
    return /^[A-Za-z_]+(\/[A-Za-z0-9_+\-]+)*$/.test(tz) && tz !== 'Etc/Unknown' ? tz : '';
  } catch { return ''; }
}

/** Pre-fills a time-zone field for new sources and bundles; the value can always be changed. @param {string} inputId @param {string} helpId */
function prefillTimeZone(inputId, helpId) {
  const tz = localTimeZone();
  if (tz && !$(inputId).value) $(inputId).value = tz;
  $(helpId).textContent = tz
    ? `Pre-filled with this computer's time zone (${tz}). Change it if the logs were written in another time zone. DICOM Router logs include their own offset and ignore this setting.`
    : 'Enter the time zone in which the logs were written, for example Europe/Lisbon.';
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
  search: async () => { await Promise.all([loadSources(), loadCategories()]); $('q').focus(); },
  live: openLive,
  exam: async () => { await loadExamLanguages(); $('ex-q').focus(); },
  sources: loadSources,
  import: loadSources,
  maintenance: loadMaintenance,
});

/** @param {string} name */
function showTab(name) {
  for (const b of document.querySelectorAll('#tabs button')) b.classList.toggle('active', /** @type {HTMLElement} */ (b).dataset.tab === name);
  for (const p of document.querySelectorAll('.panel')) p.classList.toggle('hidden', p.id !== 'tab-' + name);
  if (name !== 'live') stopLive();
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
    else if (s.lastScanStatus === 'failed') gaps.push(`"${s.label}": the last scan (${when(s.lastScanUtc)}) stopped with an error. ${s.lastScanMessage || ''}`);
    if (s.hasUnzonedTimes) gaps.push(`"${s.label}" has local times without a time zone. Set its time zone so its events line up with other sources.`);
    if (s.newFiles > 0) gaps.push(`"${s.label}" has ${s.newFiles} new file(s) that are not chosen for analysis. Choose them under Sources, Files (at most 5 days per folder).`);
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
  const cls = s === 'ok' ? 'ok-text' : (s === 'unreachable' || s === 'unreadable' || s === 'failed') ? 'err-text' : 'warn-text';
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
  a.addEventListener('click', async (ev) => {
    ev.preventDefault(); $('q').value = '';
    await loadCategories();
    setCategory(filters.category || '');
    showTab('search'); runSearch(false);
  });
  return a;
}
/** @param {string} text @param {number} id */
function linkEvent(text, id) {
  const a = el('a', '', text); a.href = '#';
  a.addEventListener('click', (ev) => { ev.preventDefault(); highlightRegex = null; openEvent(id); });
  return a;
}

// ---------- Search ----------

/** Fills the category filter with the categories present in the index (with counts). */
async function loadCategories() {
  const cats = await api('/api/categories');
  const sel = $('f-category');
  const keep = sel.value;
  sel.replaceChildren(new Option('All categories', ''));
  for (const c of cats) sel.append(new Option(`${c.category}  (${num(c.events)}${c.errors ? ', ' + num(c.errors) + ' errors' : ''})`, c.category));
  setCategory(keep);
}

/** @param {string} value */
function setCategory(value) {
  const sel = $('f-category');
  if (value && ![...sel.options].some((o) => o.value === value)) sel.append(new Option(value, value));
  sel.value = value;
}

function searchParams() {
  const p = new URLSearchParams();
  const q = $('q').value.trim();
  if (q) p.set('q', q);
  if ($('f-level').value) p.set('minLevel', $('f-level').value);
  if ($('f-source').value) p.set('sources', $('f-source').value);
  if ($('f-category').value) p.set('category', $('f-category').value);
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
  exp.classList.toggle('hidden', res.matchCount === 0 || !isAdmin());
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
  const folders = sourcesCache.filter((s) => s.kind === 'folder');
  $('src-count').textContent = folders.length === 0 ? 'Your folders: none yet' : `Your folders (${folders.length})`;
  if (!$('s-id').value) $('src-form-title').textContent = folders.length === 0 ? 'Add your first folder' : 'Add another folder';
  fillTable('src-table', folders, (s) => [
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
    const files = el('button', 'ghost', 'Files'); files.type = 'button';
    files.title = 'Choose which files are analysed (at most 5 days per folder)';
    files.addEventListener('click', () => openChooser(s.id, s.label).catch((e) => notify(e.message)));
    wrap.append(files);
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
  $('s-tz-help').textContent = 'Saved time zone of this source. Changing it re-indexes the source.';
  $('s-result').replaceChildren();
  $('src-form').scrollIntoView({ behavior: 'smooth' });
}

function resetSourceForm() {
  $('src-form').reset(); $('s-id').value = ''; $('s-path').disabled = false;
  $('src-form-title').textContent = sourcesCache.some((x) => x.kind === 'folder') ? 'Add another folder' : 'Add your first folder';
  $('s-save').textContent = 'Add folder and scan'; $('s-cancel').classList.add('hidden');
  prefillTimeZone('s-tz', 's-tz-help');
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

// ---------- Live view (tail -f / BareTail style) ----------

const live = { source: '', file: '', offset: -1, timer: 0, paused: false, level: '', regex: /** @type {RegExp | null} */ (null), busy: false };

async function openLive() {
  const sel = $('lv-source');
  const keep = sel.value;
  sourcesCache = await api('/api/sources');
  sel.replaceChildren();
  for (const s of sourcesCache.filter((x) => x.enabled)) sel.append(new Option(s.label + (s.kind === 'bundle' ? ' (bundle)' : ''), String(s.id)));
  if (!sel.options.length) { $('lv-status').textContent = 'Add a source first (Sources).'; return; }
  if (keep && [...sel.options].some((o) => o.value === keep)) sel.value = keep;
  await loadLiveFiles();
}

async function loadLiveFiles() {
  const files = await api('/api/tail/files?source=' + encodeURIComponent($('lv-source').value));
  const sel = $('lv-file');
  const keep = sel.value;
  sel.replaceChildren();
  for (const f of files) sel.append(new Option(f.path + '  (' + new Date(f.modifiedUtc).toLocaleString('en-GB') + ')', f.path));
  if (!files.length) { stopLive(); $('lv-out').replaceChildren(); $('lv-status').textContent = 'No files are chosen for this source (Sources, Files).'; return; }
  if (keep && files.some((/** @type {any} */ f) => f.path === keep)) sel.value = keep;
  startLive();
}

function stopLive() { window.clearTimeout(live.timer); live.timer = 0; }

function startLive() {
  stopLive();
  live.source = $('lv-source').value; live.file = $('lv-file').value; live.offset = -1; live.level = '';
  $('lv-out').replaceChildren();
  pollLive();
}

/** @param {any} line @param {boolean} fresh */
function liveLine(line, fresh) {
  if (line.l) live.level = line.l;
  const div = el('div', 'll' + (live.level ? ' lvl-' + live.level : '') + (line.l ? '' : ' cont') + (fresh ? ' fresh' : ''));
  appendHighlighted(div, line.t, live.regex);
  if ($('lv-only').checked && live.regex && !matches(line.t)) div.classList.add('hide');
  return div;
}

/** @param {string} text */
function matches(text) { if (!live.regex) return true; live.regex.lastIndex = 0; return live.regex.test(text); }

async function pollLive() {
  if ($('tab-live').classList.contains('hidden') || live.busy) return;
  const out = $('lv-out');
  if (!live.paused) {
    live.busy = true;
    try {
      const p = new URLSearchParams({ source: live.source, file: live.file, lines: $('lv-lines').value });
      if (live.offset >= 0) p.set('from', String(live.offset));
      const r = await api('/api/tail?' + p.toString());
      if (r.reset) { out.replaceChildren(el('div', 'live-note', '[The file was truncated, rotated or replaced; showing its end again.]')); live.level = ''; }
      if (live.offset < 0 && r.headTruncated) out.append(el('div', 'live-note', '[Earlier lines are not shown. Use Search for the whole indexed history.]'));
      for (const old of out.querySelectorAll('.fresh')) old.classList.remove('fresh');
      const fresh = live.offset >= 0 && !r.reset;
      const frag = document.createDocumentFragment();
      for (const line of r.lines) frag.append(liveLine(line, fresh));
      out.append(frag);
      const max = Number($('lv-lines').value);
      while (out.childElementCount > max + 2) out.firstElementChild?.remove();
      live.offset = r.offset;
      if ($('lv-follow').checked) out.scrollTop = out.scrollHeight;
      $('lv-status').className = 'small muted';
      $('lv-status').textContent = `${live.file} · ${bytes(r.size)} · last change ${new Date(r.modifiedUtc).toLocaleTimeString('en-GB')} · checked ${new Date().toLocaleTimeString('en-GB')}` + (r.lines.length && fresh ? ` · ${r.lines.length} new line(s)` : '');
    } catch (e) {
      $('lv-status').className = 'small err-text';
      $('lv-status').textContent = /** @type {Error} */ (e).message + ' Retrying…';
    } finally { live.busy = false; }
  }
  live.timer = window.setTimeout(pollLive, 1000);
}

function applyLiveFilter() {
  live.regex = buildHighlight($('lv-hl').value);
  const only = $('lv-only').checked && live.regex;
  for (const div of $('lv-out').querySelectorAll('.ll')) {
    const text = div.textContent || '';
    div.replaceChildren(); appendHighlighted(div, text, live.regex);
    div.classList.toggle('hide', Boolean(only) && !matches(text));
  }
}

// ---------- Folder choice (native dialog or in-app browser) ----------

let fbPath = '';

async function browseFolder() {
  if (session?.folderDialog) {
    $('s-browse').disabled = true;
    $('s-browse').textContent = 'Choose in the dialog…';
    notify('A folder dialog has opened on this computer (it may be behind the browser window).', true);
    try {
      const r = await api('/api/folders/pick', { method: 'POST', body: '{}' });
      if (r.path) await useFolder(r.path);
      return;
    } catch (e) {
      notify(/** @type {Error} */ (e).message + ' Opening the folder browser instead.');
    } finally {
      $('s-browse').disabled = false; $('s-browse').textContent = 'Browse…';
    }
  }
  await openFolderBrowser($('s-path').value.trim());
}

/** @param {string} path */
async function useFolder(path) {
  $('s-path').value = path;
  if (!$('s-label').value.trim()) $('s-label').value = path.split(/[\\/]/).filter(Boolean).pop() || path;
  const v = await api('/api/sources/validate', { method: 'POST', body: JSON.stringify({ path }) });
  renderValidation(v);
}

/** @param {string} path */
async function openFolderBrowser(path) {
  const dlg = $('fb');
  if (!dlg.open) dlg.showModal();
  try {
    await showFolder(path);
  } catch {
    await showFolder('');
  }
}

/** @param {string} path */
async function showFolder(path) {
  const r = await api('/api/folders?path=' + encodeURIComponent(path || ''));
  fbPath = r.path || '';
  $('fb-path').textContent = r.path ? r.path : 'Choose where to start.';
  $('fb-info').textContent = r.path ? `${r.folders.length} folder(s), ${r.files} file(s) here` + (r.truncated ? ' (list shortened)' : '') : '';
  $('fb-up').disabled = !r.parent; $('fb-up').dataset.path = r.parent || '';
  $('fb-use').disabled = !r.path;
  const list = $('fb-list'); list.replaceChildren();
  if (!r.folders.length) list.append(el('li', 'muted small', r.path ? 'No sub-folders. Use this folder if it contains the log files.' : 'Nothing to show.'));
  for (const f of r.folders) {
    const li = el('li'); const b = el('button', '', '📁 ' + f.name); b.type = 'button';
    b.addEventListener('click', () => showFolder(f.path).catch((e) => notify(e.message)));
    li.append(b); list.append(li);
  }
}

// ---------- Fresh session ----------

async function startFreshSession() {
  const keep = $('fs-keep').checked; const bundles = $('fs-bundles').checked; const audit = $('fs-audit').checked; const scan = $('fs-scan').checked;
  const parts = ['all indexed events', 'temporary files'];
  if (bundles) parts.push('imported bundles');
  if (!keep) parts.push('all folder sources');
  if (audit) parts.push('the activity record');
  if (!window.confirm('Start a fresh session?\n\nThis deletes ' + parts.join(', ') + '.' + (scan ? ' The newest files are then analysed again.' : '') + '\nOriginal log files are not touched.')) return;
  try {
    await api('/api/maintenance/fresh-session', { method: 'POST', body: JSON.stringify({ confirm: 'FRESH', keepFolderSources: keep, deleteBundles: bundles, clearActivityRecord: audit, scanAgain: scan }) });
  } catch (e) { notify(/** @type {Error} */ (e).message); return; }
  // Clear what this page holds in memory as well.
  stopLive();
  for (const id of ['lv-out', 'search-summary']) $(id).replaceChildren();
  $('results').querySelector('tbody').replaceChildren();
  $('more').classList.add('hidden'); $('export').classList.add('hidden');
  $('chooser').classList.add('hidden'); chooser = null; nextCursor = null;
  notify('Fresh session started. Cleaning up' + (scan ? ' and analysing the newest files again…' : '…'), true);
  for (let i = 0; i < 600; i++) {
    await new Promise((res) => setTimeout(res, 500));
    const st = await api('/api/scan/status').catch(() => null);
    if (st && st.state === 'idle' && st.queued === 0) { notify(st.lastOutcome || 'Fresh session ready.', true); break; }
  }
  showTab('overview');
}

// ---------- File chooser (at most 5 days of files per folder) ----------

/** @type {{id: number, maxDays: number, files: any[]} | null} */
let chooser = null;

/** @param {string} d yyyy-MM-dd */
const dayNumber = (d) => Math.round(Date.parse(d + 'T00:00:00Z') / 86400000);

/** @param {number} id @param {string} label */
async function openChooser(id, label) {
  const data = await api('/api/sources/' + id + '/files');
  chooser = { id, maxDays: data.maxDays, files: data.files };
  $('ch-title').textContent = 'Files analysed for "' + label + '"';
  $('ch-help').textContent = `Tick the files to analyse. Their last-modified dates (time zone ${data.timeZone}) must lie within ${data.maxDays} consecutive days. `
    + 'Chosen files are read completely; unticking a file removes its events from the index. New files are not analysed until you tick them.'
    + (data.selectionSaved ? '' : ' The latest days are pre-selected.') + (data.truncated ? ' Only the newest files are listed.' : '');
  const tbody = $('ch-table').querySelector('tbody');
  tbody.replaceChildren();
  if (!data.files.length) {
    const tr = el('tr'); const td = el('td', 'muted', 'No files found in this folder.'); td.colSpan = 5; tr.append(td); tbody.append(tr);
  }
  data.files.forEach((/** @type {any} */ f, /** @type {number} */ i) => {
    const tr = el('tr');
    const box = el('input'); box.type = 'checkbox'; box.checked = f.selected; box.dataset.index = String(i);
    box.setAttribute('aria-label', 'Analyse ' + f.path);
    box.addEventListener('change', () => { f.selected = box.checked; updateChooser(); });
    const tdBox = el('td'); tdBox.append(box);
    const status = el('td', f.selected ? 'ok-text' : f.isNew ? 'warn-text' : 'muted', '');
    status.dataset.role = 'status';
    tr.append(tdBox, el('td', 'file', f.path), el('td', 'time', new Date(f.modifiedUtc).toLocaleString('en-GB')), el('td', '', bytes(f.size)), status);
    tbody.append(tr);
  });
  $('ch-result').textContent = '';
  $('chooser').classList.remove('hidden');
  updateChooser();
  $('chooser').scrollIntoView({ behavior: 'smooth' });
}

function updateChooser() {
  if (!chooser) return;
  const chosen = chooser.files.filter((f) => f.selected);
  const days = chosen.length ? Math.max(...chosen.map((f) => dayNumber(f.modifiedDate))) - Math.min(...chosen.map((f) => dayNumber(f.modifiedDate))) + 1 : 0;
  const ok = chosen.length > 0 && days <= chooser.maxDays;
  const summary = $('ch-summary');
  summary.className = 'small ' + (ok ? 'ok-text' : 'err-text');
  summary.textContent = chosen.length === 0 ? 'No file chosen.'
    : `${chosen.length} file(s) chosen, ${days} day(s) of ${chooser.maxDays} maximum` + (ok ? '.' : `: remove files to stay within ${chooser.maxDays} days.`);
  $('ch-save').disabled = !ok;
  $('ch-table').querySelectorAll('tbody tr').forEach((/** @type {any} */ tr, /** @type {number} */ i) => {
    const f = chooser?.files[i]; const cell = tr.querySelector('[data-role=status]');
    if (!f || !cell) return;
    cell.textContent = f.selected ? 'chosen' : f.isNew ? 'new, not chosen' : 'not chosen';
    cell.className = f.selected ? 'ok-text' : f.isNew ? 'warn-text' : 'muted';
  });
}

function chooseLatest() {
  if (!chooser || !chooser.files.length) return;
  const newest = Math.max(...chooser.files.map((f) => dayNumber(f.modifiedDate)));
  for (const f of chooser.files) f.selected = dayNumber(f.modifiedDate) > newest - chooser.maxDays;
  $('ch-table').querySelectorAll('tbody input[type=checkbox]').forEach((/** @type {any} */ b) => { b.checked = chooser?.files[Number(b.dataset.index)].selected; });
  updateChooser();
}

async function saveChooser() {
  if (!chooser) return;
  const files = chooser.files.filter((f) => f.selected).map((f) => f.path);
  try {
    await api('/api/sources/' + chooser.id + '/files', { method: 'PUT', body: JSON.stringify({ files }) });
    $('ch-result').className = 'small ok-text';
    $('ch-result').textContent = 'Saved. The folder is being scanned with this choice.';
    pollScan();
  } catch (e) {
    $('ch-result').className = 'small err-text';
    $('ch-result').textContent = /** @type {Error} */ (e).message;
  }
}

// ---------- Scan status ----------

let pollTimer = 0;
async function pollScan() {
  window.clearTimeout(pollTimer);
  try {
    const s = await api('/api/scan/status');
    const bar = $('scanbar');
    if (s.state === 'running' && s.operation !== 'Live update') {
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
      $('imp-form').reset(); prefillTimeZone('i-tz', 'i-tz-help'); loadSources(); pollScan();
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
    about.append(el('li', '', 'Signed in as ' + session.identity + (isAdmin() ? ' (administrator).' : ' (viewer).')));
    if (session.sourceRoots.length) about.append(el('li', '', 'Folder sources must be inside: ' + session.sourceRoots.join(', ')));
    about.append(el('li', '', session.liveIntervalSeconds
      ? `New lines in chosen files are indexed automatically (checked every ${session.liveIntervalSeconds} seconds).`
      : 'Automatic indexing of new lines is off; scan from the Sources page.'));
    if (session.scanIntervalMinutes) about.append(el('li', '', `Full scan of all sources every ${session.scanIntervalMinutes} minutes.`));
  }
  about.append(el('li', '', 'Data folder: ' + m.dataDirectory));
  about.append(el('li', '', 'No telemetry, no online services and no AI: logs and results stay in the data folder.'));
  fillTable('m-audit', m.audit, (a) => [a.timestampUtc.replace('T', ' ').slice(0, 19), a.actor, a.action, a.detail || '']);
}

// ---------- Exam history ----------

/** Exam summary state: language strings (loaded once from this application), chosen language, last result. */
const exam = { i18n: /** @type {any} */ (null), lang: 'en', data: /** @type {any} */ (null), query: '' };

async function loadExamLanguages() {
  if (exam.i18n) return;
  const res = await fetch('/exam-i18n.json', { credentials: 'same-origin', cache: 'no-store' });
  if (!res.ok) throw new Error('The language file could not be loaded.');
  exam.i18n = await res.json();
  const saved = pref('examLang', 'en');
  exam.lang = exam.i18n.languages[saved] ? saved : 'en';
  renderLanguageButtons();
  applyExamLabels();
}

/** Text in the chosen language (English when a key is missing), with {placeholders} filled in. @param {string} key @param {Record<string, string | number>} [vars] */
function tr(key, vars) {
  const langs = exam.i18n.languages;
  let text = String(langs[exam.lang]?.[key] ?? langs.en[key] ?? key);
  for (const [k, v] of Object.entries(vars || {})) text = text.split('{' + k + '}').join(String(v));
  return text;
}

function renderLanguageButtons() {
  const box = $('ex-langs');
  box.replaceChildren();
  for (const code of exam.i18n.order) {
    const l = exam.i18n.languages[code];
    const b = el('button', 'lang');
    b.type = 'button';
    b.lang = l.locale;
    b.dataset.lang = code;
    b.title = l.name;
    b.setAttribute('aria-pressed', String(code === exam.lang));
    const img = /** @type {HTMLImageElement} */ (el('img'));
    img.src = '/flags/' + l.flag + '.svg';
    img.alt = '';
    img.width = 24; img.height = 16;
    b.append(img, el('span', '', l.name));
    b.addEventListener('click', () => setExamLanguage(code));
    box.append(b);
  }
}

/** @param {string} code */
function setExamLanguage(code) {
  if (!exam.i18n.languages[code]) return;
  exam.lang = code;
  savePref('examLang', code);
  for (const b of $('ex-langs').querySelectorAll('button')) b.setAttribute('aria-pressed', String(/** @type {HTMLElement} */ (b).dataset.lang === code));
  applyExamLabels();
  if (exam.data) renderExam();
}

function applyExamLabels() {
  const l = exam.i18n.languages[exam.lang];
  $('tab-exam').lang = l.locale;
  $('ex-title').textContent = tr('title');
  $('ex-q').placeholder = tr('placeholder');
  $('ex-button').textContent = tr('button');
  $('ex-hint').textContent = tr('hint');
  $('ex-langs').setAttribute('aria-label', tr('language'));
}

/** Date and time in the chosen language. Times are UTC, or the source's wall-clock time when its zone is unknown. @param {number | null | undefined} ms */
function examTime(ms) {
  if (ms === null || ms === undefined) return tr('none');
  const locale = exam.i18n.languages[exam.lang].locale;
  let text;
  try {
    text = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'UTC' }).format(new Date(ms));
  } catch { text = utc(ms); }
  return text + ' ' + (exam.data && !exam.data.utcKnown ? '(' + tr('local') + ')' : tr('utc'));
}

/** @param {number} ms */
function duration(ms) {
  const t = Math.max(0, Math.round(ms / 1000));
  const p = (/** @type {number} */ n) => String(n).padStart(2, '0');
  return `${p(Math.floor(t / 3600))}:${p(Math.floor((t % 3600) / 60))}:${p(t % 60)}`;
}

/** @param {string} milestone camelCase name from the API, for example sendFailed */
const milestoneKey = (milestone) => 'm_' + milestone.charAt(0).toUpperCase() + milestone.slice(1);

/** @param {string} text */
async function runExam(text) {
  const q = (text ?? $('ex-q').value).trim();
  $('ex-q').value = q;
  if (q.length < 3) { notify('Enter an accession number, patient ID or Study Instance UID (at least 3 characters).'); return; }
  await loadExamLanguages();
  $('ex-out').replaceChildren(el('p', 'muted', '…'));
  exam.data = await api('/api/exam?q=' + encodeURIComponent(q));
  exam.query = q;
  renderExam();
}

function renderExam() {
  const d = exam.data;
  const out = $('ex-out');
  out.replaceChildren();
  if (d.entries === 0) {
    out.append(el('p', 'exam-summary', tr('n_none', { id: exam.query })));
    renderCoverage(out, d.coverageNotes);
    return;
  }

  // Plain-language summary.
  const summary = el('div', 'exam-summary');
  summary.append(el('h2', '', tr('summary')));
  /** @param {string} key @param {Record<string, string | number>} [vars] */
  const say = (key, vars) => summary.append(el('p', '', tr(key, vars)));
  say(d.firstReceivedEstimated ? 'n_firstEst' : 'n_first', { time: examTime(d.firstReceivedTs), source: d.firstReceivedSource || '' });
  if (d.priorActivityTs !== null && d.priorActivityTs !== undefined) say('n_prior', { time: examTime(d.priorActivityTs) });
  if (d.updates === 0) say('n_noUpdate');
  else say('n_update', { count: d.updates, time: examTime(d.updateTimes[d.updateTimes.length - 1]) });
  if (!d.seriesKnown && !d.firstReceivedEstimated) say('n_seriesUnknown');
  if (d.destinations.length === 0) say('n_noSends');
  for (const s of d.destinations) {
    say('n_dest', { dest: s.destination, attempts: s.attempts, resends: s.resends, failures: s.failures, result: tr('r_' + s.lastResult), time: examTime(s.lastResultTs) });
  }
  if (d.firstQueuedTs !== null && d.firstQueuedTs !== undefined) say('n_queued', { time: examTime(d.firstQueuedTs) });
  if (d.finalSeriesTs !== null && d.finalSeriesTs !== undefined) {
    say('n_final', { time: examTime(d.finalSeriesTs) });
    if (d.lastEntryTs !== null && d.lastEntryTs > d.finalSeriesTs) say('n_lastLater', { time: examTime(d.lastEntryTs) });
    else say('n_noMore');
  }
  if (d.truncated) say('n_truncated', { count: d.entries });
  if (!d.utcKnown) say('n_tzUnknown');
  summary.append(el('p', 'muted small', tr('n_caveat')));
  out.append(summary);

  // Key facts; each value opens the log entry it is based on.
  const facts = el('div', 'cards exam-facts');
  /** @param {string} key @param {string | number} value @param {number | null} [eventId] */
  const fact = (key, value, eventId) => {
    const c = el('div', 'card');
    const v = el('div', 'n fact');
    v.append(eventId ? linkEvent(String(value), eventId) : String(value));
    c.append(v, el('div', 'l', tr(key)));
    facts.append(c);
  };
  fact(d.firstReceivedEstimated ? 'f_firstEst' : 'f_first', examTime(d.firstReceivedTs), d.firstReceivedEventId);
  fact('f_updates', d.updates);
  fact('f_series', d.seriesKnown ? d.series.length : tr('none'));
  fact('f_resends', d.totalResends);
  fact('f_failures', d.totalFailures);
  fact('f_final', examTime(d.finalSeriesTs), d.finalSeriesEventId);
  fact('f_last', examTime(d.lastEntryTs), d.lastEntryEventId);
  if (d.firstReceivedTs !== null && d.finalSeriesTs !== null && d.finalSeriesTs !== undefined && !d.firstReceivedEstimated) fact('f_duration', duration(d.finalSeriesTs - d.firstReceivedTs));
  fact('f_entries', num(d.entries));
  out.append(facts);

  if (d.destinations.length > 0) {
    out.append(el('h2', '', tr('d_title')));
    out.append(examTable([tr('d_dest'), tr('d_attempts'), tr('d_resends'), tr('d_failures'), tr('d_last')], d.destinations, (/** @type {any} */ s) => {
      const res = el('span', s.lastResult === 'failure' ? 'err-text' : s.lastResult === 'success' ? 'ok-text' : 'muted', tr('r_' + s.lastResult));
      const cell = el('span');
      cell.append(res);
      if (s.lastResultEventId) cell.append(' (', linkEvent(examTime(s.lastResultTs), s.lastResultEventId), ')');
      return [s.destination, s.attempts, s.resends, s.failures, cell];
    }));
  }

  if (d.series.length > 0) {
    out.append(el('h2', '', tr('s_title')));
    out.append(examTable([tr('s_uid'), tr('s_first'), tr('s_last'), tr('s_receipts'), tr('s_update')], d.series, (/** @type {any} */ s) =>
      [el('code', '', s.seriesUid), examTime(s.firstTs), examTime(s.lastTs), s.receipts, s.isUpdate ? '✓' : '']));
  }

  out.append(el('h2', '', tr('t_title')));
  const timeline = examTable([tr('t_time'), tr('t_event'), tr('t_source'), tr('t_message')], d.timeline, (/** @type {any} */ t) => {
    const badge = el('span', 'milestone ms-' + t.milestone, tr(milestoneKey(t.milestone)));
    const cell = el('span');
    cell.append(badge);
    if (t.isUpdate) cell.append(' ', el('span', 'milestone ms-update', tr('m_update')));
    return [examTime(t.event.timestamp), cell, t.event.sourceLabel, t.event.message];
  }, (/** @type {any} */ t) => t.event.id);
  timeline.querySelector('table')?.classList.add('results');
  out.append(timeline);
  renderCoverage(out, d.coverageNotes);
}

/** @param {HTMLElement} out @param {string[]} notes */
function renderCoverage(out, notes) {
  if (!notes || notes.length === 0) return;
  out.append(el('h2', '', tr('notes')));
  const ul = el('ul', 'plain small');
  for (const n of notes) ul.append(el('li', '', n));
  out.append(ul);
}

/** @param {string[]} heads @param {any[]} rows @param {(row: any) => (string | number | Node)[]} cells @param {(row: any) => number} [openId] */
function examTable(heads, rows, cells, openId) {
  const wrap = el('div', 'table-wrap');
  const table = el('table');
  const tr0 = el('tr');
  for (const h of heads) tr0.append(el('th', '', h));
  const thead = el('thead'); thead.append(tr0);
  const tbody = el('tbody');
  for (const r of rows) {
    const row = el('tr');
    for (const c of cells(r)) { const td = el('td'); td.append(typeof c === 'object' ? c : String(c)); row.append(td); }
    if (openId) { row.classList.add('clickable'); row.tabIndex = 0; row.addEventListener('click', () => { highlightRegex = null; openEvent(openId(r)).catch((e) => notify(e.message)); }); }
    tbody.append(row);
  }
  table.append(thead, tbody);
  wrap.append(table);
  return wrap;
}

// ---------- Start ----------

async function start() {
  setupPreferences();
  for (const b of document.querySelectorAll('#tabs button')) b.addEventListener('click', () => showTab(/** @type {HTMLElement} */ (b).dataset.tab || 'overview'));
  $('ov-refresh').addEventListener('click', () => loadOverview().catch((e) => notify(e.message)));
  $('search-form').addEventListener('submit', (/** @type {Event} */ ev) => { ev.preventDefault(); runSearch(false).catch((e) => notify(e.message)); });
  $('more').addEventListener('click', () => runSearch(true).catch((e) => notify(e.message)));
  $('exam-form').addEventListener('submit', (/** @type {Event} */ ev) => { ev.preventDefault(); runExam(undefined).catch((e) => notify(e.message)); });
  $('q-exam').addEventListener('click', () => { const q = $('q').value.trim(); showTab('exam'); if (q) runExam(q).catch((e) => notify(e.message)); });
  $('d-close').addEventListener('click', () => $('detail').close());
  for (const id of ['d-prev', 'd-next']) $(id).addEventListener('click', () => { const v = Number($(id).dataset.id); if (v) openEvent(v).catch((e) => notify(e.message)); });
  $('scan-all').addEventListener('click', () => api('/api/scan', { method: 'POST', body: '{}' }).then(() => { notify('Scan queued.', true); pollScan(); }).catch((e) => notify(e.message)));
  $('scan-cancel').addEventListener('click', () => api('/api/scan/cancel', { method: 'POST', body: '{}' }).catch((e) => notify(e.message)));
  $('s-validate').addEventListener('click', () => api('/api/sources/validate', { method: 'POST', body: JSON.stringify({ path: $('s-path').value }) }).then(renderValidation).catch((e) => notify(e.message)));
  $('s-cancel').addEventListener('click', resetSourceForm);
  $('s-browse').addEventListener('click', () => { browseFolder().catch((e) => notify(e.message)); });
  $('fb-cancel').addEventListener('click', () => $('fb').close());
  $('fb-roots').addEventListener('click', () => showFolder('').catch((e) => notify(e.message)));
  $('fb-up').addEventListener('click', () => showFolder($('fb-up').dataset.path).catch((e) => notify(e.message)));
  $('fb-use').addEventListener('click', () => { $('fb').close(); useFolder(fbPath).catch((e) => notify(e.message)); });
  $('lv-source').addEventListener('change', () => loadLiveFiles().catch((e) => notify(e.message)));
  $('lv-file').addEventListener('change', startLive);
  $('lv-lines').addEventListener('change', startLive);
  $('lv-hl').addEventListener('input', applyLiveFilter);
  $('lv-only').addEventListener('change', applyLiveFilter);
  $('lv-pause').addEventListener('click', () => {
    live.paused = !live.paused;
    $('lv-pause').textContent = live.paused ? 'Resume' : 'Pause';
    if (live.paused) $('lv-status').textContent = 'Paused. New lines are collected again when you resume.';
  });
  $('lv-clear').addEventListener('click', () => $('lv-out').replaceChildren());
  // Like a tail viewer: scrolling up stops following; scrolling back to the end follows again.
  $('lv-out').addEventListener('scroll', () => {
    const o = $('lv-out');
    $('lv-follow').checked = o.scrollTop + o.clientHeight >= o.scrollHeight - 24;
  });
  $('ch-close').addEventListener('click', () => { $('chooser').classList.add('hidden'); chooser = null; });
  $('ch-latest').addEventListener('click', chooseLatest);
  $('ch-none').addEventListener('click', () => {
    if (!chooser) return;
    for (const f of chooser.files) f.selected = false;
    $('ch-table').querySelectorAll('tbody input[type=checkbox]').forEach((/** @type {any} */ b) => { b.checked = false; });
    updateChooser();
  });
  $('ch-save').addEventListener('click', () => { saveChooser(); });
  $('src-form').addEventListener('submit', async (/** @type {Event} */ ev) => {
    ev.preventDefault();
    const id = $('s-id').value;
    try {
      if (id) {
        const r = await api('/api/sources/' + id, { method: 'PUT', body: JSON.stringify(sourceBody()) });
        notify(r.reindexed ? 'Saved. The time zone or format changed, so the source is being re-indexed.' : 'Saved.', true);
        resetSourceForm();
      } else {
        const label = $('s-label').value;
        const r = await api('/api/sources', { method: 'POST', body: JSON.stringify(sourceBody()) });
        renderValidation(r.validation);
        notify('Folder added with the files of its latest 5 days; the first scan has started. Check the file choice below. To add another folder, fill in the form again.', true);
        resetSourceForm();
        await openChooser(r.id, label).catch(() => {});
      }
      await loadSources(); pollScan();
    } catch (e) { notify(/** @type {Error} */ (e).message); }
  });
  $('imp-form').addEventListener('submit', (/** @type {Event} */ ev) => { ev.preventDefault(); upload(); });
  $('m-clean').addEventListener('click', () => api('/api/maintenance/clean-temp', { method: 'POST', body: '{}' }).then((r) => { notify(r.remaining ? `${r.remaining} item(s) could not be removed.` : 'Temporary files deleted.', true); loadMaintenance(); }).catch((e) => notify(e.message)));
  $('fs-start').addEventListener('click', startFreshSession);

  try {
    session = await api('/api/session');
  } catch { return; }
  if (!isAdmin()) {
    for (const tab of ['sources', 'import', 'maintenance']) document.querySelector(`#tabs button[data-tab="${tab}"]`)?.classList.add('hidden');
    const note = el('p', 'muted small', 'You have read-only access (viewer): overview, search and event details. Adding sources, importing, exporting and deleting need the administrator role.');
    $('tab-overview').insertBefore(note, $('ov-cards'));
  }
  const fmt = $('s-format');
  for (const f of session.formats) fmt.append(new Option(f.name, f.id));
  const tzl = $('tz-list');
  try { for (const z of /** @type {any} */ (Intl).supportedValuesOf('timeZone')) tzl.append(new Option(z)); } catch { /* older browser: free text only */ }
  $('s-path-help').textContent = session.mode === 'server'
    ? 'Container path inside ' + session.sourceRoots.join(' or ') + ' (a read-only volume in compose.yaml). Windows drive letters and UNC paths cannot be used directly here.'
    : session.platform === 'windows' ? 'Examples: C:\\3dnet\\DicomRouter\\logs, Z:\\3dnet\\audits\\Gateway\\0 or \\\\server\\share\\logs'
      : 'Examples: /Volumes/ClinicalLogs/Gateway/US or /var/log/dicomrouter';
  prefillTimeZone('s-tz', 's-tz-help');
  prefillTimeZone('i-tz', 'i-tz-help');
  $('i-limit').textContent = `Maximum upload size: ${session.maxUploadMb} MB. Archives are checked for unsafe paths, links and decompression bombs before anything is kept.`;
  if (new URLSearchParams(location.search).has('expired')) notify('That sign-in link was already used or has expired. Press O in the analyser window for a new one.');
  showTab('overview');
  pollScan();
}

start();
