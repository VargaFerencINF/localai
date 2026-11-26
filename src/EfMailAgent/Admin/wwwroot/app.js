'use strict';
// EfMailAgent admin UI. Vanilla JS, no dependencies. Every text from the server is inserted with textContent
// (emails, prompts and model answers are untrusted data).

const STATUS_LABELS = { queued: 'sorban', processing: 'feldolgozás alatt', done: 'kész', needs_review: 'ellenőrzendő', failed: 'hiba' };
const ROW_STATUS_LABELS = { ok: 'rendben', ellenorzendo: 'ellenőrzendő' };
const FILE_KINDS = { rule: 'szabály', prompt: 'prompt', skill: 'skill', base: 'rendszerprompt', other: 'egyéb' };

// ---------- helpers ----------

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs ?? {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key.startsWith('on') && typeof value === 'function') node.addEventListener(key.slice(2), value);
    else if (key === 'dataset') Object.assign(node.dataset, value);
    else if (value === true) node.setAttribute(key, '');
    else node.setAttribute(key, String(value));
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

const $ = (id) => document.getElementById(id);

/** Element.append() would print null as the text "null"; this skips empty children. */
function add(parent, ...children) {
  parent.append(...children.flat().filter((c) => c !== null && c !== undefined && c !== false));
}

class ApiError extends Error {
  constructor(status, body) {
    super(body?.error ?? body?.errors?.join(' ') ?? `HTTP ${status}`);
    this.status = status;
    this.body = body;
  }
}

async function api(method, path, body) {
  const response = await fetch(`/admin/api${path}`, {
    method,
    credentials: 'same-origin',
    headers: { 'X-EfMailAgent-Admin': '1', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { json = null; }
  if (response.status === 401 && path !== '/login') {
    showLogin();
    throw new ApiError(401, { error: 'A munkamenet lejárt, jelentkezz be újra.' });
  }
  if (!response.ok) throw new ApiError(response.status, json);
  return json;
}

function toast(message, kind = 'info', ms = 4000) {
  const node = el('div', { class: `toast ${kind}`, role: 'status', text: message });
  $('toasts').append(node);
  setTimeout(() => node.remove(), ms);
}

function formatDate(value) {
  if (!value) return '–';
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? String(value) : d.toLocaleString('hu-HU', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function badge(status, labels = STATUS_LABELS) {
  return el('span', { class: `badge ${status}`, text: labels[status] ?? status });
}

function pretty(text) {
  if (text === null || text === undefined) return '';
  try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return String(text); }
}

function cellText(value) {
  if (value === null || value === undefined) return '';
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

function messageList(errors = [], warnings = [], ok = null) {
  const list = el('ul', { class: 'messages' });
  for (const e of errors) list.append(el('li', { class: 'error', text: e }));
  for (const w of warnings) list.append(el('li', { class: 'warn', text: w }));
  if (ok) list.append(el('li', { class: 'ok', text: ok }));
  return list;
}

function rowsTable(columns, rows) {
  if (!rows?.length) return el('p', { class: 'empty', text: 'Nincs sor.' });
  const names = columns.map((c) => c.name ?? c);
  return el('div', { class: 'table-wrap' }, el('table', {},
    el('thead', {}, el('tr', {}, el('th', { text: '#' }), el('th', { text: 'Állapot' }), ...names.map((n) => el('th', { text: n })))),
    el('tbody', {}, rows.map((row, i) => el('tr', { class: row.status === 'ellenorzendo' ? 'row-review' : '' },
      el('td', { class: 'num', text: i + 1 }),
      el('td', {}, badge(row.status, ROW_STATUS_LABELS), row.reasons?.length ? el('div', { class: 'small muted', text: row.reasons.join(' ') }) : null),
      ...names.map((n) => el('td', { text: cellText(row.values?.[n]) })))))));
}

function codeBlock(title, text, open = false) {
  if (text === null || text === undefined || text === '') return null;
  return el('details', { open }, el('summary', { text: title }), el('pre', { class: 'block', text }));
}

// ---------- login ----------

function showLogin() {
  stopTimers();
  hasUnsavedChanges = () => false;
  $('app').hidden = true;
  closeDrawer();
  $('login').hidden = false;
  $('login-token').focus();
}

async function showApp() {
  $('login').hidden = true;
  $('app').hidden = false;
  route();
}

$('login-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  $('login-error').textContent = '';
  try {
    await api('POST', '/login', { token: $('login-token').value });
    $('login-token').value = '';
    showApp();
  } catch (error) {
    $('login-error').textContent = error.status === 401 ? 'Hibás token.' : error.message;
  }
});

$('quit').addEventListener('click', async () => {
  if (!confirmLeave()) return;
  if (!confirm('Kilépsz az EfMailAgentből? A program leáll, és amíg újra el nem indítod, a beérkező levelek nem kerülnek feldolgozásra.')) return;
  try {
    await api('POST', '/quit');
  } catch (error) {
    toast(error.message, 'error', 8000);
    return;
  }
  hasUnsavedChanges = () => false;
  stopTimers();
  closeDrawer();
  $('app').hidden = true;
  $('login').hidden = true;
  $('stopped').hidden = false;
});

$('logout').addEventListener('click', async () => {
  if (!confirmLeave()) return;
  hasUnsavedChanges = () => false;
  await api('POST', '/logout').catch(() => {});
  showLogin();
});

// ---------- router ----------

const PAGES = { attekintes: renderOverview, szabalyok: renderRules, tesztkonzol: renderTestConsole, beallitasok: renderSettings, naplo: renderLogs };
const UNSAVED = 'A módosítások nincsenek mentve. Biztosan továbblépsz?';
let timers = [];
let cleanups = [];
/** Set by a page with an editor; navigation away asks before unsaved changes are lost. */
let hasUnsavedChanges = () => false;

function stopTimers() {
  timers.forEach(clearInterval);
  timers = [];
  cleanups.forEach((fn) => fn());
  cleanups = [];
}

/** Registers a document event listener that is removed when the page is left. */
function onPageEvent(type, handler) {
  document.addEventListener(type, handler);
  cleanups.push(() => document.removeEventListener(type, handler));
}

function confirmLeave() {
  return !hasUnsavedChanges() || confirm(UNSAVED);
}

function route() {
  stopTimers();
  hasUnsavedChanges = () => false;
  const [name, ...rest] = (location.hash.replace(/^#\/?/, '').split('?')[0] || 'attekintes').split('/');
  const page = PAGES[name] ? name : 'attekintes';
  for (const link of document.querySelectorAll('.sidebar nav a')) link.classList.toggle('active', link.dataset.page === page);
  const container = $('page');
  container.textContent = '';
  PAGES[page](container, rest.map(decodeURIComponent)).catch((error) => {
    if (error.status !== 401) container.append(el('div', { class: 'card' }, messageList([error.message])));
  });
  container.focus({ preventScroll: true });
}

window.addEventListener('hashchange', (event) => {
  if (!confirmLeave()) {
    history.replaceState(null, '', event.oldURL); // stay on the page, keep the edits
    return;
  }
  route();
});
window.addEventListener('beforeunload', (event) => { if (hasUnsavedChanges()) event.preventDefault(); });

// ---------- Áttekintés ----------

async function renderOverview(container, args) {
  const params = new URLSearchParams(location.hash.split('?')[1] ?? '');
  let filter = params.get('allapot') ?? args[0] ?? '';
  const cards = el('div', { class: 'grid-cards' });
  const chips = el('div', { class: 'chips' });
  const table = el('div');
  const banner = el('div');
  container.append(
    el('div', { class: 'page-header' }, el('div', {}, el('h1', { text: 'Áttekintés' }), el('p', { class: 'muted', text: 'A szolgáltatás állapota és a legutóbbi 50 feladat.' })),
      el('button', { type: 'button', onclick: () => refresh() }, 'Frissítés')),
    banner,
    cards,
    el('div', { class: 'row' }, el('h2', { text: 'Feladatok' }), el('span', { class: 'spacer' }), chips),
    table);

  const drawChips = (counts = {}) => {
    chips.textContent = '';
    const total = Object.values(counts).reduce((a, b) => a + b, 0);
    for (const [value, label] of [['', 'Mind'], ['queued', 'Sorban'], ['processing', 'Feldolgozás alatt'], ['done', 'Kész'], ['needs_review', 'Ellenőrzendő'], ['failed', 'Hiba']]) {
      const count = value ? counts[value] ?? 0 : total;
      chips.append(el('button', { type: 'button', class: `chip ${filter === value ? 'active' : ''}`, onclick: () => { filter = value; refresh(); } }, `${label} (${count})`));
    }
  };

  async function refresh() {
    const [overview, jobs] = await Promise.all([api('GET', '/overview'), api('GET', `/jobs?limit=50${filter ? `&status=${filter}` : ''}`)]);
    $('version').textContent = `v${overview.version}`;
    banner.textContent = '';
    if (!overview.llm.reachable || (overview.llm.provider !== 'mock' && !overview.llm.model)) {
      banner.append(el('div', { class: 'banner warn', id: 'setup-banner' },
        el('div', {}, el('strong', { text: overview.llm.reachable ? 'Nincs kiválasztva AI modell.' : 'Az AI (helyi LLM) nem érhető el.' }),
          el('p', { text: overview.llm.reachable
            ? 'Add meg a Beállítások oldalon, melyik modellel dolgozzon a program.'
            : 'A beérkező levelek a sorban várnak, amíg az AI el nem érhető. Indítsd el az Ollamát / LM Studiót, vagy add meg a helyes címet és modellt a Beállítások oldalon.' })),
        el('a', { class: 'button primary', href: '#/beallitasok' }, 'Beállítások megnyitása')));
    }
    cards.textContent = '';
    const llmState = overview.llm.reachable ? 'ok' : 'error';
    cards.append(
      statCard('AI (LLM)', llmState, overview.llm.reachable ? 'elérhető' : 'nem érhető el',
        `${overview.llm.provider}${overview.llm.model ? ` · ${overview.llm.model}` : ''}${overview.llm.error ? ` · ${overview.llm.error}` : ''}`),
      statCard('Sor', overview.queueLength > 0 ? 'warn' : 'ok', `${overview.queueLength} feladat`, overview.llm.outage ? 'A feldolgozás szünetel, amíg az AI nem érhető el.' : 'várakozik vagy fut'),
      statCard('Szabályok', overview.rules.invalid.length ? 'error' : 'ok', `${overview.rules.enabled} bekapcsolva`,
        `${overview.rules.valid} érvényes${overview.rules.invalid.length ? `, hibás: ${overview.rules.invalid.join(', ')}` : ''} · verzió ${overview.rules.version}`),
      statCard('Thunderbird add-on', overview.addon.ok ? 'ok' : 'warn', overview.addon.ok ? 'rendben' : 'nincs telepítve / javítandó', addonDetail(overview.addon)),
      statCard('Ellenőrzendő', (overview.counts.needs_review ?? 0) > 0 ? 'warn' : 'ok', `${overview.counts.needs_review ?? 0} feladat`,
        el('a', { href: '#/attekintes?allapot=needs_review', onclick: () => { filter = 'needs_review'; } }, 'Ellenőrzendők megnyitása')));
    drawChips(overview.counts);
    // Keep the keyboard focus on the same job when the list is redrawn (auto refresh every 10 s).
    const focusedId = document.activeElement?.closest?.('#page tr[data-id]')?.dataset.id;
    drawJobs(jobs);
    if (focusedId) table.querySelector(`tr[data-id="${CSS.escape(focusedId)}"]`)?.focus({ preventScroll: true });
  }

  function drawJobs(jobs) {
    table.textContent = '';
    if (!jobs.length) {
      table.append(el('div', { class: 'card empty', text: filter ? 'Nincs ilyen állapotú feladat.' : 'Még nem érkezett feladat.' }));
      return;
    }
    table.append(el('div', { class: 'table-wrap' }, el('table', { class: 'jobs' },
      el('colgroup', {}, ['c-date', 'c-status', 'c-rule', 'c-subject', 'c-sender', 'c-rows', 'c-time'].map((c) => el('col', { class: c }))),
      el('thead', {}, el('tr', {}, ...['Érkezett', 'Állapot', 'Szabály', 'Tárgy', 'Feladó', 'Sorok', 'Idő'].map((h) => el('th', { text: h })))),
      el('tbody', {}, jobs.map((job) => el('tr', { class: 'clickable', tabindex: 0, dataset: { id: job.id }, onclick: () => openJob(job.id), onkeydown: (e) => {
        // preventDefault: otherwise the same Enter would also press the panel's close button, which gets the focus.
        if (e.key === 'Enter') { e.preventDefault(); openJob(job.id); }
      } },
        el('td', { class: 'nowrap', text: formatDate(job.createdAt) }),
        el('td', {}, badge(job.status), job.attempts > 1 ? el('span', { class: 'small muted', text: ` ${job.attempts}. kísérlet` }) : null),
        el('td', {}, el('span', { class: 'ellipsis', title: job.ruleId, text: job.ruleId })),
        el('td', { class: 'subject' }, el('span', { class: 'ellipsis', title: job.subject ?? '', text: job.subject ?? '' }), job.error ? el('span', { class: 'small muted ellipsis', text: job.error }) : null),
        el('td', {}, el('span', { class: 'ellipsis', title: job.sender ?? '', text: job.sender ?? '' })),
        el('td', { class: 'num', text: job.rowCount ?? '–' }),
        el('td', { class: 'num nowrap', text: job.durationMs ? `${(job.durationMs / 1000).toFixed(1)} s` : '–' })))))));
  }

  // After the job panel closes (possibly after a retry) the list is refreshed at once and the row gets the focus back.
  onPageEvent('efm:drawer-closed', async (event) => {
    await refresh().catch(() => {});
    const active = document.activeElement;
    if (!active || active === document.body || !active.isConnected) table.querySelector(`tr[data-id="${CSS.escape(event.detail.id)}"]`)?.focus();
  });
  await refresh();
  timers.push(setInterval(() => refresh().catch(() => {}), 10_000));
}

function statCard(label, state, value, detail) {
  return el('div', { class: 'card stat' },
    el('div', { class: 'label', text: label }),
    el('div', { class: 'value' }, el('span', { class: `dot ${state}` }), value),
    el('div', { class: 'detail' }, detail));
}

function addonDetail(addon) {
  if (addon.error) return addon.error;
  const parts = [];
  if (addon.installations?.length) parts.push(addon.installations.map((i) => `Thunderbird ${i.version ?? '?'}${i.entry ? '' : ' (nincs bejegyzés)'}`).join(', '));
  else parts.push('Thunderbird nem található');
  if (addon.bundledVersion) parts.push(`add-on ${addon.installedVersion ?? '–'} / ${addon.bundledVersion}`);
  if (addon.mode) parts.push(`mód: ${addon.mode}`);
  return parts.join(' · ');
}

// ---------- job drawer ----------

let drawerJobId = null;
let drawerOpener = null;

function closeDrawer() {
  if ($('drawer').hidden) return;
  $('drawer').hidden = true;
  $('drawer-backdrop').hidden = true;
  if (drawerOpener?.isConnected) drawerOpener.focus();
  drawerOpener = null;
  if (drawerJobId) document.dispatchEvent(new CustomEvent('efm:drawer-closed', { detail: { id: drawerJobId } }));
  drawerJobId = null;
}

$('drawer-close').addEventListener('click', closeDrawer);
$('drawer-backdrop').addEventListener('click', closeDrawer);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeDrawer(); });

async function openJob(id) {
  const body = $('drawer-body');
  body.textContent = '';
  body.append(el('div', { class: 'row' }, el('span', { class: 'spinner' }), 'Betöltés…'));
  if ($('drawer').hidden) drawerOpener = document.activeElement;
  drawerJobId = id;
  $('drawer').hidden = false;
  $('drawer-backdrop').hidden = false;
  $('drawer-close').focus();
  const job = await api('GET', `/jobs/${encodeURIComponent(id)}`);
  $('drawer-title').textContent = job.subject || 'Feladat';
  body.textContent = '';

  const canRetry = job.status === 'failed' || job.status === 'needs_review';
  add(body,
    el('div', { class: 'row' }, badge(job.status), el('span', { class: 'muted small', text: job.id }), el('span', { class: 'spacer' }),
      canRetry ? el('button', { type: 'button', class: 'primary', onclick: async () => {
        await api('POST', `/jobs/${encodeURIComponent(id)}/retry`);
        toast('A feladat újra sorba került.', 'ok');
        openJob(id);
      } }, 'Újrafuttatás') : null),
    job.error ? messageList(job.status === 'failed' ? [job.error] : [], job.status !== 'failed' ? [job.error] : []) : null,
    el('dl', { class: 'meta' },
      ...meta('Szabály', job.ruleId), ...meta('Forrás', job.source === 'manual' ? 'kézi beküldés' : 'automatikus'),
      ...meta('Feladó (továbbító)', job.sender), ...meta('Eredeti feladó', job.originalSender ?? 'nem azonosítható'),
      ...meta('Címzett', job.recipients), ...meta('Message-ID', job.messageId), ...meta('Postafiók / mappa', [job.account, job.folder].filter(Boolean).join(' / ')),
      ...meta('Beérkezett', formatDate(job.receivedAt)), ...meta('Sorba került', formatDate(job.createdAt)), ...meta('Befejeződött', formatDate(job.finishedAt)),
      ...meta('Kísérletek', job.attempts), ...meta('Feldolgozási idő', job.durationMs ? `${(job.durationMs / 1000).toFixed(1)} s` : '–'),
      ...meta('Sorok', job.rowCount ?? '–'), ...meta('CSV fájl', job.outputFile ?? '–'),
      ...meta('Mellékletek', job.attachmentNames?.length ? job.attachmentNames.join(', ') : '–')),
    el('h3', { text: 'Kinyert sorok' }),
    job.parsedRows?.length ? rowsTable(Object.keys(job.parsedRows[0].values ?? {}), job.parsedRows) : el('p', { class: 'muted', text: 'Nincs kinyert sor.' }),
    el('h3', { text: 'Részletek' }),
    codeBlock('Nyers AI válasz', pretty(job.rawResponse), !job.parsedRows?.length),
    codeBlock('Javító hívás válasza', pretty(job.repairResponse)),
    codeBlock('Összeállított prompt: rendszer', job.promptSystem),
    codeBlock('Összeállított prompt: felhasználói üzenet', job.promptUser),
    codeBlock('E-mail törzse', job.bodyText));
}

function meta(label, value) {
  return [el('dt', { text: label }), el('dd', { text: value === null || value === undefined || value === '' ? '–' : String(value) })];
}

// ---------- Szabályok ----------

async function renderRules(container, args) {
  let rules = await api('GET', '/rules');
  let selected = args[0] ?? rules.find((r) => !r.isBase)?.folder ?? rules[0]?.folder;
  let currentFile = null;
  let dirty = false;
  hasUnsavedChanges = () => dirty;

  const list = el('div', { class: 'rule-list' });
  const editor = el('div', { class: 'card' });
  container.append(
    el('div', { class: 'page-header' },
      el('div', {}, el('h1', { text: 'Szabályok' }), el('p', { class: 'muted', text: 'A mentés azonnal érvényes (a szolgáltatás újratölti a szabályokat); minden mentés előtt .bak másolat készül.' })),
      el('button', { type: 'button', class: 'primary', onclick: () => newRuleDialog() }, 'Új szabály')),
    el('div', { class: 'split' }, list, editor));

  function drawList() {
    list.textContent = '';
    for (const rule of rules) {
      const toggle = rule.isBase || !rule.valid ? null : el('label', { class: 'switch', title: rule.enabled ? 'Kikapcsolás' : 'Bekapcsolás', onclick: (e) => e.stopPropagation() },
        el('input', { type: 'checkbox', checked: rule.enabled, onchange: async (e) => {
          try {
            const result = await api('POST', `/rules/${encodeURIComponent(rule.folder)}/enabled`, { enabled: e.target.checked });
            toast(e.target.checked ? `Bekapcsolva: ${rule.name}` : `Kikapcsolva: ${rule.name}`, 'ok');
            if (result.warnings?.length) toast(result.warnings.join(' '), 'warn');
          } catch (error) {
            e.target.checked = !e.target.checked;
            toast(error.message, 'error', 8000);
          }
          await reload();
        } }));
      const state = rule.isBase ? null : !rule.valid ? badge('invalid', { invalid: 'hibás' }) : !rule.enabled ? badge('disabled', { disabled: 'kikapcsolva' }) : null;
      list.append(el('div', { class: `rule-item ${rule.folder === selected ? 'active' : ''}`, tabindex: 0, onclick: () => select(rule.folder), onkeydown: (e) => { if (e.key === 'Enter') { e.preventDefault(); select(rule.folder); } } },
        el('div', { class: 'title' }, el('strong', { title: rule.name ?? rule.folder, text: rule.name ?? rule.folder }),
          el('span', { class: 'small muted', text: rule.isBase ? 'minden szabály elé kerül' : `${rule.folder}${rule.priority !== null ? ` · prioritás ${rule.priority}` : ''} ` }), state),
        toggle));
    }
  }

  async function reload() {
    rules = await api('GET', '/rules');
    drawList();
  }

  function select(folder) {
    if (dirty && !confirm(UNSAVED)) return;
    selected = folder;
    currentFile = null;
    dirty = false;
    history.replaceState(null, '', `#/szabalyok/${encodeURIComponent(folder)}`);
    drawList();
    drawEditor();
  }

  async function drawEditor() {
    editor.textContent = '';
    const rule = rules.find((r) => r.folder === selected);
    if (!rule) {
      editor.append(el('p', { class: 'empty', text: 'Válassz egy szabályt.' }));
      return;
    }
    currentFile ??= rule.files[0]?.path ?? (rule.isBase ? 'system.md' : 'rule.yaml');

    const tabs = el('div', { class: 'tabs' }, rule.files.map((f) => el('button', { type: 'button', class: `tab ${f.path === currentFile ? 'active' : ''}`, onclick: () => {
      if (dirty && !confirm(UNSAVED)) return;
      currentFile = f.path;
      dirty = false;
      drawEditor();
    } }, f.path, el('span', { class: 'kind', text: FILE_KINDS[f.kind] ?? '' }))));

    const textarea = el('textarea', { class: 'code', spellcheck: 'false', 'aria-label': currentFile });
    const status = el('div');
    const fileInfo = rule.files.find((f) => f.path === currentFile);
    try {
      textarea.value = (await api('GET', `/rules/${encodeURIComponent(rule.folder)}/file?path=${encodeURIComponent(currentFile)}`)).content;
    } catch (error) {
      textarea.value = '';
      status.append(messageList([], [error.status === 404 ? 'A fájl még nem létezik; mentéskor létrejön.' : error.message]));
    }
    textarea.addEventListener('input', () => { dirty = true; });
    textarea.addEventListener('keydown', (e) => {
      if (e.key === 'Tab') {
        e.preventDefault();
        const start = textarea.selectionStart;
        textarea.setRangeText('  ', start, textarea.selectionEnd, 'end');
        dirty = true;
      }
      if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); save(); }
    });

    async function save() {
      status.textContent = '';
      try {
        const result = await api('PUT', `/rules/${encodeURIComponent(rule.folder)}/file?path=${encodeURIComponent(currentFile)}`, { content: textarea.value });
        dirty = false;
        status.append(messageList(result.errors, result.warnings, 'Mentve. A szabályok újratöltve.'));
        await reload();
      } catch (error) {
        status.append(messageList(error.body?.errors ?? [error.message], error.body?.warnings ?? []));
        status.prepend(el('p', { class: 'error-text', text: 'Nem mentettem: a szabály hibás lenne.' }));
      }
    }

    const actions = el('div', { class: 'row' },
      el('button', { type: 'button', class: 'primary', onclick: save }, 'Mentés'),
      rule.isBase ? null : el('button', { type: 'button', onclick: () => { location.hash = `#/tesztkonzol/${encodeURIComponent(rule.id ?? rule.folder)}`; } }, 'Tesztelés a Tesztkonzolban'),
      el('span', { class: 'spacer' }),
      rule.isBase ? null : el('button', { type: 'button', onclick: () => addSkillDialog(rule) }, '+ Skill fájl'),
      fileInfo?.kind === 'skill' ? el('button', { type: 'button', class: 'danger', onclick: async () => {
        if (!confirm(`Törlöd a(z) ${currentFile} skill fájlt? (a tartalma .bak fájlba kerül)`)) return;
        try {
          await api('DELETE', `/rules/${encodeURIComponent(rule.folder)}/file?path=${encodeURIComponent(currentFile)}`);
          toast('Skill törölve.', 'ok');
          currentFile = null;
          await reload();
          drawEditor();
        } catch (error) {
          toast(error.message, 'error', 8000);
        }
      } }, 'Skill törlése') : null,
      rule.isBase || !rule.valid ? null : el('button', { type: 'button', onclick: () => newRuleDialog(rule) }, 'Másolat készítése'));

    add(editor,
      el('div', { class: 'row' }, el('h2', { text: rule.name ?? rule.folder }), el('span', { class: 'spacer' }),
        rule.isBase ? null : rule.valid ? badge(rule.enabled ? 'valid' : 'disabled', { valid: 'érvényes', disabled: 'kikapcsolva' }) : badge('invalid', { invalid: 'hibás' })),
      rule.errors?.length || rule.warnings?.length ? messageList(rule.errors, rule.warnings) : null,
      tabs, textarea, status, actions);
  }

  async function addSkillDialog(rule) {
    const name = prompt('Az új skill fájl neve (pl. cikklista.md):', 'uj-skill.md');
    if (!name) return;
    try {
      const result = await api('POST', `/rules/${encodeURIComponent(rule.folder)}/skills`, { fileName: name.trim(), content: `Ide írd a(z) ${name.trim()} tartalmát (pl. egy lista: név;kód soronként).\n` });
      toast('Skill hozzáadva a szabályhoz.', 'ok');
      currentFile = result.path;
      await reload();
      drawEditor();
    } catch (error) {
      toast(error.body?.errors?.join(' ') ?? error.message, 'error', 8000);
    }
  }

  async function newRuleDialog(copyFrom) {
    const id = prompt(copyFrom ? `Az új szabály azonosítója (a(z) "${copyFrom.name}" másolata):` : 'Az új szabály azonosítója (kisbetű, szám, -, _; pl. arajanlat):', copyFrom ? `${copyFrom.folder}-masolat` : '');
    if (!id) return;
    const name = prompt('Az új szabály neve (magyarul, pl. Árajánlatkérés feldolgozás):', copyFrom ? `${copyFrom.name} (másolat)` : '');
    if (!name) return;
    try {
      await api('POST', '/rules', { id: id.trim(), name: name.trim(), copyFrom: copyFrom?.folder ?? null });
      toast('A szabály létrejött (kikapcsolva). Írd meg a promptot, teszteld, majd kapcsold be.', 'ok', 7000);
      rules = await api('GET', '/rules');
      dirty = false;
      select(id.trim());
    } catch (error) {
      toast(error.body?.errors?.join(' ') ?? error.message, 'error', 8000);
    }
  }

  drawList();
  await drawEditor();
}

// ---------- Tesztkonzol ----------

async function renderTestConsole(container, args) {
  const [rules, samples] = await Promise.all([api('GET', '/rules'), api('GET', '/samples').catch(() => [])]);
  const usable = rules.filter((r) => !r.isBase && r.valid);
  const ruleSelect = el('select', { id: 'test-rule' }, usable.map((r) => el('option', { value: r.id, text: `${r.name} (${r.id})${r.enabled ? '' : ' – kikapcsolva'}` })));
  if (args[0]) ruleSelect.value = args[0];
  const sampleSelect = el('select', { id: 'test-sample' }, el('option', { value: '', text: '– minta e-mail betöltése –' }), samples.map((s, i) => el('option', { value: i, text: `${s.file} · ${s.subject}` })));
  const subject = el('input', { id: 'test-subject', type: 'text', placeholder: 'pl. Fwd: Előrendelés tavaszra' });
  const from = el('input', { id: 'test-from', type: 'text', placeholder: 'Név <cim@example.hu>' });
  const body = el('textarea', { id: 'test-body', class: 'body', placeholder: 'Másold ide az e-mail szövegét…' });
  const run = el('button', { type: 'button', class: 'primary', id: 'test-run' }, 'Futtatás');
  const result = el('div', { class: 'stack' }, el('div', { class: 'card empty', text: 'Válassz szabályt, tölts be egy minta e-mailt vagy másolj be egyet, majd kattints a Futtatás gombra. Semmi nem kerül a sorba és a CSV-be.' }));

  sampleSelect.addEventListener('change', () => {
    const sample = samples[Number(sampleSelect.value)];
    if (!sample) return;
    subject.value = sample.subject ?? '';
    from.value = sample.from ?? '';
    body.value = sample.body ?? '';
  });

  container.append(
    el('div', { class: 'page-header' }, el('div', {}, el('h1', { text: 'Tesztkonzol' }),
      el('p', { class: 'muted', text: 'A szabály lefuttatása egy e-mailen az AI-val, mentett prompttal – a sort és a CSV-t nem érinti.' }))),
    el('div', { class: 'split-test' },
      el('div', { class: 'card' },
        el('label', { class: 'field' }, el('span', { text: 'Szabály' }), ruleSelect),
        el('label', { class: 'field' }, el('span', { text: 'Minta e-mail' }), sampleSelect),
        el('label', { class: 'field' }, el('span', { text: 'Tárgy' }), subject),
        el('label', { class: 'field' }, el('span', { text: 'Feladó (továbbító)' }), from),
        el('label', { class: 'field' }, el('span', { text: 'E-mail szövege' }), body),
        el('div', { class: 'row' }, run, el('span', { class: 'muted small', text: 'Ctrl+Enter' }))),
      result));

  body.addEventListener('keydown', (e) => { if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') run.click(); });

  run.addEventListener('click', async () => {
    if (!ruleSelect.value) { toast('Nincs érvényes szabály.', 'warn'); return; }
    run.disabled = true;
    const started = Date.now();
    const timer = el('span', { text: '0 s' });
    result.textContent = '';
    result.append(el('div', { class: 'card row' }, el('span', { class: 'spinner' }), 'Az AI dolgozik… ', timer));
    const tick = setInterval(() => { timer.textContent = `${Math.round((Date.now() - started) / 1000)} s`; }, 500);
    try {
      const r = await api('POST', '/test', { ruleId: ruleSelect.value, subject: subject.value, from: from.value, body: body.value });
      result.textContent = '';
      drawResult(r);
    } catch (error) {
      result.textContent = '';
      result.append(el('div', { class: 'card' }, messageList([error.message])));
    } finally {
      clearInterval(tick);
      run.disabled = false;
    }
  });

  function drawResult(r) {
    const seconds = (r.durationMs / 1000).toFixed(1);
    if (!r.ok) {
      result.append(el('div', { class: 'card' }, el('h2', { text: 'Az AI nem válaszolt' }), messageList([r.transportError]),
        el('p', { class: 'muted', text: `${r.provider}${r.model ? ` · ${r.model}` : ''} · ${seconds} s` })));
      return;
    }
    const verdict = r.valid
      ? messageList([], r.status === 'needs_review' ? ['Érvényes válasz, de van ellenőrzendő sor (vagy nincs egy sor sem).'] : [], r.status === 'done' ? 'Érvényes válasz, minden sor rendben.' : null)
      : messageList(['A válasz a javító hívás után sem felel meg a sémának (a feladat „hiba” lenne):', ...r.finalErrors]);
    result.append(
      el('div', { class: 'card' },
        el('div', { class: 'row' }, el('h2', { text: 'Eredmény' }), el('span', { class: 'spacer' }), badge(r.status),
          el('span', { class: 'muted small', text: `${r.provider}${r.model ? ` · ${r.model}` : ''} · ${seconds} s${r.repairResponse ? ' · javító hívással' : ''}` })),
        verdict,
        el('p', { class: 'muted small', text: `Eredeti feladó: ${r.originalSender ?? 'nem azonosítható'}${r.bodyTruncated ? ' · a levél törzse le lett vágva' : ''}` }),
        r.valid ? rowsTable(r.columns, r.rows) : null),
      el('div', {},
        codeBlock('Nyers AI válasz', pretty(r.rawResponse), !r.valid),
        r.firstErrors?.length && r.repairResponse ? el('details', {}, el('summary', { text: 'Az első válasz hibái (ezek mentek a javító hívásba)' }), messageList(r.firstErrors)) : null,
        codeBlock('Javító hívás válasza', pretty(r.repairResponse)),
        codeBlock('Összeállított prompt: rendszer', r.prompt.system),
        codeBlock('Összeállított prompt: felhasználói üzenet', r.prompt.user)));
  }
}

// ---------- Beállítások ----------

async function renderSettings(container) {
  const [s, rules] = await Promise.all([api('GET', '/settings'), api('GET', '/rules')]);
  const field = (label, input, hint) => el('label', { class: 'field' }, el('span', {}, label, hint ? el('span', { class: 'hint', text: ` – ${hint}` }) : null), input);
  const input = (id, value, attrs = {}) => el('input', { id, value: value ?? '', ...attrs });

  const provider = el('select', { id: 'set-provider' },
    el('option', { value: 'ollama', text: 'Ollama' }), el('option', { value: 'openai_compatible', text: 'OpenAI-kompatibilis (LM Studio, llama.cpp)' }), el('option', { value: 'mock', text: 'Mock (fejlesztői szimuláció)' }));
  provider.value = s.llm.provider;
  const baseUrl = input('set-baseurl', s.llm.baseUrl, { type: 'url' });
  const model = input('set-model', s.llm.model, { list: 'model-list', placeholder: 'pl. qwen3.5:9b' });
  const modelList = el('datalist', { id: 'model-list' });
  const temperature = input('set-temperature', s.llm.temperature, { type: 'number', min: 0, max: 2, step: 0.1 });
  const timeout = input('set-timeout', s.llm.timeoutSeconds, { type: 'number', min: 1, max: 3600 });
  const numCtx = input('set-numctx', s.llm.numCtx, { type: 'number', min: 0, step: 1024 });
  const think = el('input', { id: 'set-think', type: 'checkbox', checked: s.llm.think });
  const mockDir = input('set-mockdir', s.llm.mockDir);
  const mockDelay = input('set-mockdelay', s.llm.mockDelayMs, { type: 'number', min: 0 });
  const mockFields = el('div', { class: 'form-grid' }, field('Mock válaszok mappája', mockDir), field('Mock késleltetés (ms)', mockDelay));
  const delimiter = el('select', { id: 'set-delimiter' }, el('option', { value: ';', text: '; (pontosvessző)' }), el('option', { value: ',', text: ', (vessző)' }), el('option', { value: '\t', text: 'tabulátor' }));
  delimiter.value = s.csv.delimiter;
  const decimal = el('select', { id: 'set-decimal' }, el('option', { value: ',', text: ', (vessző)' }), el('option', { value: '.', text: '. (pont)' }));
  decimal.value = s.csv.decimalSeparator;
  const encoding = el('select', { id: 'set-encoding' }, el('option', { value: 'utf-8-bom', text: 'UTF-8 BOM-mal (Excel)' }), el('option', { value: 'utf-8', text: 'UTF-8 BOM nélkül' }));
  encoding.value = s.csv.encoding;
  const lockRetry = input('set-lockretry', s.csv.lockRetrySeconds, { type: 'number', min: 0 });
  const output = input('set-output', s.outputFolder);
  const port = input('set-port', s.port, { type: 'number', min: 1, max: 65535 });
  const llmStatus = el('div');
  const saveStatus = el('div');

  const updateMock = () => { mockFields.hidden = provider.value !== 'mock'; baseUrl.disabled = provider.value === 'mock'; };
  provider.addEventListener('change', updateMock);
  updateMock();

  async function testLlm() {
    llmStatus.textContent = '';
    llmStatus.append(el('div', { class: 'row' }, el('span', { class: 'spinner' }), 'Kapcsolódás…'));
    try {
      const r = await api('POST', '/settings/test-llm', { provider: provider.value, baseUrl: baseUrl.value, mockDir: mockDir.value });
      llmStatus.textContent = '';
      modelList.textContent = '';
      for (const m of r.models ?? []) modelList.append(el('option', { value: m }));
      llmStatus.append(r.reachable
        ? messageList([], model.value && r.models?.length && !r.models.includes(model.value) ? [`A(z) "${model.value}" modell nincs a szerver listáján.`] : [],
          `Kapcsolat rendben (${r.durationMs} ms). Elérhető modellek: ${r.models?.length ? r.models.join(', ') : 'nincs'}.`)
        : messageList([`Nem érhető el: ${r.error}`]));
    } catch (error) {
      llmStatus.textContent = '';
      llmStatus.append(messageList([error.message]));
    }
  }

  async function save() {
    saveStatus.textContent = '';
    try {
      const r = await api('PUT', '/settings', {
        llm: { provider: provider.value, baseUrl: baseUrl.value, model: model.value, timeoutSeconds: Number(timeout.value), temperature: Number(temperature.value),
          numCtx: Number(numCtx.value), think: think.checked, mockDir: mockDir.value, mockDelayMs: Number(mockDelay.value) },
        csv: { delimiter: delimiter.value, decimalSeparator: decimal.value, encoding: encoding.value, lockRetrySeconds: Number(lockRetry.value) },
        outputFolder: output.value,
        port: Number(port.value),
      });
      const notes = [];
      if (r.appliedNow.length) notes.push(`Azonnal érvényes: ${r.appliedNow.join(', ')}.`);
      saveStatus.append(messageList([], r.restartRequired.map((x) => `Újraindítás szükséges: ${x}`), `Mentve az appsettings.json-ba. ${notes.join(' ')}`));
    } catch (error) {
      saveStatus.append(messageList(error.body?.errors ?? [error.message]));
    }
  }

  const ollamaCard = el('section', { class: 'card', id: 'ollama-wizard' });
  const addonCard = el('section', { class: 'card', id: 'addon-section' });
  container.append(
    el('div', { class: 'page-header' }, el('div', {}, el('h1', { text: 'Beállítások' }), el('p', { class: 'muted', text: `Fájl: ${s.settingsFile}` }))),
    el('div', { class: 'stack' },
      ollamaCard,
      el('section', { class: 'card' }, el('h2', { text: 'AI (LLM) kapcsolat – kézi beállítás' }),
        el('div', { class: 'form-grid' }, field('Szolgáltató', provider), field('Szerver címe', baseUrl, 'Ollama: http://127.0.0.1:11434'), field('Modell', model), modelList,
          field('Temperature', temperature, '0 = determinisztikus'), field('Időkorlát (mp)', timeout), field('Kontextus (num_ctx)', numCtx, 'Ollama, token')),
        el('label', { class: 'switch' }, think, 'Gondolkodó mód (think) – általában kikapcsolva'),
        mockFields,
        el('div', { class: 'row' }, el('button', { type: 'button', onclick: testLlm }, 'Kapcsolat tesztelése'), el('span', { class: 'muted small', text: 'A modell-lista a tesztelés után a Modell mező javaslataiban jelenik meg.' })),
        llmStatus),
      el('section', { class: 'card', id: 'output-section' }, el('h2', { text: 'Kimenet: hova kerülnek a CSV fájlok' }),
        field('Kimeneti mappa', output, 'teljes út, pl. D:\\CSV vagy \\\\szerver\\megosztas\\csv; a mentés ellenőrzi, hogy írható-e'),
        el('p', { class: 'muted small', text: `Jelenleg: ${s.outputFolderResolved}` }),
        el('h3', { text: 'CSV fájlok szabályonként' }),
        outputRules(rules, s.outputFolderResolved)),
      addonCard,
      el('section', { class: 'card' }, el('h2', { text: 'CSV formátum' }),
        el('div', { class: 'form-grid' }, field('Elválasztó', delimiter), field('Tizedesjel', decimal), field('Kódolás', encoding),
          field('Zárolt fájl: várakozás (mp)', lockRetry, 'utána _pending_ fájl'))),
      el('section', { class: 'card' }, el('h2', { text: 'Szolgáltatás' }),
        el('div', { class: 'form-grid' }, field('Port', port, 'újraindítás kell'),
          field('Token', el('input', { type: 'text', value: s.tokenPreview, readonly: true }), 'csak olvasható'),
          field('Időzóna', el('input', { type: 'text', value: s.timeZone, readonly: true })),
          field('Adatmappa', el('input', { type: 'text', value: s.dataDir, readonly: true })))),
      el('div', { class: 'row' }, el('button', { type: 'button', class: 'primary', onclick: save }, 'Mentés'), el('span', { class: 'muted small', text: 'Az AI-, CSV- és mappabeállítások újraindítás nélkül érvényesek; a port változásához újraindítás kell.' })),
      saveStatus));
  drawOllamaWizard(ollamaCard);
  drawAddonCard(addonCard);
}

// ---------- Beállítások: Ollama varázsló ----------

function progressBar(percent, label) {
  const bar = el('progress', { max: 100, 'aria-label': label });
  if (percent !== null && percent !== undefined) bar.value = percent;
  return bar;
}

async function drawOllamaWizard(card, baseUrlOverride) {
  card.textContent = '';
  card.append(el('h2', { text: 'AI telepítése és modellválasztás (Ollama)' }),
    el('p', { class: 'muted', text: 'Lépésről lépésre: az Ollama (helyi AI szerver) telepítése, egy modell letöltése, majd a program beállítása erre a modellre. A levelek ezen a gépen maradnak.' }),
    el('div', { class: 'row' }, el('span', { class: 'spinner' }), 'Ellenőrzés…'));
  let st;
  try {
    st = await api('GET', `/ollama${baseUrlOverride ? `?baseUrl=${encodeURIComponent(baseUrlOverride)}` : ''}`);
  } catch (error) {
    card.lastChild.replaceWith(messageList([error.message]));
    return;
  }
  card.lastChild.remove();

  const url = el('input', { id: 'ollama-url', type: 'url', value: st.baseUrl, 'aria-label': 'Az Ollama címe' });
  const recheck = el('button', { type: 'button', onclick: () => drawOllamaWizard(card, url.value) }, 'Újraellenőrzés');
  const taskArea = el('div', { id: 'ollama-task' });
  const step = (n, title, done, ...body) => el('div', { class: `wizard-step ${done ? 'done' : ''}` },
    el('div', { class: 'step-num', text: done ? '✓' : String(n) }), el('div', { class: 'step-body' }, el('h3', { text: title }), ...body));

  // 1. Ollama
  const step1 = st.reachable
    ? [el('p', { class: 'ok-text', text: `Az Ollama fut (verzió: ${st.version}).` })]
    : [messageList([], [`Az Ollama nem érhető el ezen a címen${st.error ? ` (${st.error})` : ''}.`]),
      st.installed ? el('p', { text: 'Az Ollama telepítve van, de nem fut: indítsd el a Start menüből (Ollama), majd kattints az Újraellenőrzésre.' }) : null,
      st.canInstall
        ? el('div', { class: 'row' }, el('button', { type: 'button', class: 'primary', id: 'ollama-install', onclick: async () => {
          try { await api('POST', '/ollama/install'); watchTask(); } catch (error) { toast(error.message, 'error', 8000); }
        } }, st.installed ? 'Ollama újratelepítése' : 'Ollama letöltése és telepítése'), el('span', { class: 'muted small', text: 'kb. 1 GB letöltés; rendszergazdai jog nem kell' }))
        : el('div', {}, el('p', { class: 'muted', text: st.cannotInstallReason }),
          el('a', { href: st.downloadPage, target: '_blank', rel: 'noopener' }, 'Az Ollama letöltése (ollama.com)'))];

  // 2. Model
  const installedNames = new Set((st.models ?? []).map((m) => m.name));
  const isInstalled = (name) => installedNames.has(name) || installedNames.has(name.includes(':') ? name : `${name}:latest`);
  const choices = [...st.choices];
  for (const m of st.models ?? []) if (!choices.some((c) => c.name === m.name)) choices.push({ name: m.name, title: m.name, description: 'Már letöltött modell.', downloadGb: m.sizeGb, minRamGb: null });
  let selected = st.currentProvider === 'ollama' && st.currentModel ? st.currentModel : (choices.find((c) => isInstalled(c.name))?.name ?? st.recommended);
  const custom = el('input', { id: 'ollama-custom', type: 'text', placeholder: 'egyéb modell, pl. mistral-nemo:12b', 'aria-label': 'Egyéb modell neve', spellcheck: 'false' });
  const rows = choices.map((c) => {
    const radio = el('input', { type: 'radio', name: 'ollama-model', value: c.name, 'aria-label': c.title, checked: c.name === selected, onchange: () => { selected = c.name; custom.value = ''; updateButtons(); } });
    return el('tr', {},
      el('td', {}, radio),
      el('td', {}, el('strong', { text: c.title }), c.name === st.recommended ? el('span', { class: 'badge done', text: 'ajánlott ehhez a géphez' }) : null,
        isInstalled(c.name) ? el('span', { class: 'badge valid', text: 'letöltve' }) : null,
        el('div', { class: 'small muted', text: `${c.name} · ${c.description}` })),
      el('td', { class: 'nowrap small', text: c.downloadGb ? `kb. ${String(c.downloadGb).replace('.', ',')} GB` : '' }),
      el('td', { class: 'nowrap small', text: c.minRamGb ? `${c.minRamGb} GB RAM-tól` : '' }));
  });
  custom.addEventListener('input', () => {
    if (custom.value.trim()) { selected = custom.value.trim(); for (const r of card.querySelectorAll('input[name=ollama-model]')) r.checked = false; }
    updateButtons();
  });
  const pull = el('button', { type: 'button', id: 'ollama-pull', onclick: async () => {
    try { await api('POST', '/ollama/pull', { model: selected, baseUrl: url.value }); watchTask(); } catch (error) { toast(error.body?.error ?? error.message, 'error', 8000); }
  } }, 'Letöltés');
  const apply = el('button', { type: 'button', class: 'primary', id: 'ollama-apply', onclick: async () => {
    applyStatus.textContent = '';
    try {
      await api('POST', '/ollama/apply', { model: selected, baseUrl: url.value });
      toast(`Az AI beállítva: ${selected}`, 'ok');
      route(); // redraw the settings page with the new values
    } catch (error) {
      applyStatus.append(messageList(error.body?.errors ?? [error.body?.error ?? error.message]));
    }
  } }, 'Használat ezzel a modellel');
  const applyStatus = el('div');
  function updateButtons() {
    const have = isInstalled(selected);
    pull.disabled = !st.reachable || have || !selected;
    apply.disabled = !st.reachable || !have;
    pull.textContent = selected ? `${selected} letöltése` : 'Letöltés';
  }
  updateButtons();

  const step2 = st.reachable
    ? [el('p', { class: 'muted small', text: `Ennek a gépnek kb. ${st.ramGb} GB memóriája van. Nagyobb modell pontosabb, de lassabb; videókártya nélkül a 4–8B modellek ajánlottak.` }),
      el('div', { class: 'table-wrap' }, el('table', { class: 'model-table' }, el('tbody', {}, rows))), custom,
      el('div', { class: 'row' }, pull, apply), applyStatus]
    : [el('p', { class: 'muted', text: 'Előbb az Ollamának futnia kell (1. lépés).' })];

  const configured = st.currentProvider === 'ollama' && st.currentModel && isInstalled(st.currentModel);
  add(card, el('div', { class: 'row' }, el('label', { class: 'field grow' }, el('span', { text: 'Az Ollama címe' }), url), recheck),
    step(1, 'Ollama (helyi AI szerver)', st.reachable, ...step1),
    taskArea,
    step(2, 'Modell letöltése és kiválasztása', configured, ...step2),
    configured ? el('p', { class: 'ok-text', text: `Kész: a program az Ollama ${st.currentModel} modelljével dolgozik. A Tesztkonzolban ki is próbálhatod.` }) : null);

  function drawTask(t) {
    taskArea.textContent = '';
    if (!t || t.phase === 'idle') return;
    add(taskArea, el('div', { class: `task-box ${t.failed ? 'failed' : ''}` },
      el('div', { class: 'row' }, t.running ? el('span', { class: 'spinner' }) : null, el('span', { text: t.message })),
      t.running ? progressBar(t.percent, t.message) : null));
  }
  async function watchTask() {
    for (;;) {
      let t;
      try { t = await api('GET', '/ollama/task'); } catch { return; }
      if (!card.isConnected) return;
      drawTask(t);
      if (!t.running) {
        if (!t.failed) { await drawOllamaWizard(card, url.value); drawTaskAfter(t); }
        return;
      }
      await new Promise((r) => setTimeout(r, 1000));
    }
  }
  function drawTaskAfter(t) {
    const area = card.querySelector('#ollama-task');
    if (area) { area.textContent = ''; area.append(el('div', { class: 'task-box' }, el('span', { text: t.message }))); }
  }
  if (st.task?.running) watchTask();
  else drawTask(st.task?.failed ? st.task : null);
}

// ---------- Beállítások: Thunderbird bővítmény ----------

async function drawAddonCard(card, resultMessages) {
  card.textContent = '';
  card.append(el('h2', { text: 'Thunderbird bővítmény' }));
  let st;
  try {
    st = await api('GET', '/addon');
  } catch (error) {
    card.append(messageList([error.message]));
    return;
  }
  const lines = el('dl', { class: 'meta' });
  const installs = st.installations ?? [];
  add(lines,
    ...meta('Állapot', st.ok ? 'rendben, telepítve' : installs.length ? 'nincs telepítve vagy javítandó' : 'a Thunderbird nem található ezen a gépen'),
    ...meta('Thunderbird', installs.length ? installs.map((i) => `${i.dir} (${i.version ?? '?'})${i.ok ? ' – rendben' : i.entry ? ' – javítandó' : ' – nincs telepítve'}`).join('; ') : '–'),
    ...meta('Bővítmény verzió', `${st.installedVersion ?? '–'} telepítve / ${st.bundledVersion ?? '–'} a programban`));
  const result = el('div');
  if (resultMessages) result.append(resultMessages);
  const button = el('button', { type: 'button', class: st.ok ? '' : 'primary', id: 'addon-install', onclick: async () => {
    button.disabled = true;
    result.textContent = '';
    result.append(el('div', { class: 'row' }, el('span', { class: 'spinner' }), st.needsElevation ? 'Telepítés… (fogadd el a Windows engedélykérését)' : 'Telepítés…'));
    let messages;
    try {
      const r = await api('POST', '/addon/install');
      messages = messageList([], [], r.messages.join(' '));
    } catch (error) {
      messages = messageList(error.body?.messages ?? [error.message]);
    }
    drawAddonCard(card, messages);
  } }, st.ok ? 'Újratelepítés / javítás' : 'Telepítés a Thunderbirdbe');
  add(card,
    el('p', { class: 'muted', text: 'A bővítmény figyeli a beérkező leveleket, és a megfelelő tárgyúakat elküldi a programnak. Egy gombnyomással telepíthető a gépen lévő Thunderbirdbe (a szerver címe és a token automatikusan beállítódik).' }),
    lines,
    st.thunderbirdRunning ? messageList([], ['A Thunderbird most fut: a telepítés után zárd be teljesen és indítsd újra.']) : null,
    st.error ? messageList([st.error]) : null,
    el('div', { class: 'row' }, button, st.needsElevation ? el('span', { class: 'muted small', text: 'A Windows rendszergazdai jóváhagyást fog kérni.' }) : null),
    result);
}

/** Per-rule CSV file name and mode (rule.yaml output.file / output.mode), saved one rule at a time. */
function outputRules(rules, folder) {
  const usable = rules.filter((r) => !r.isBase && r.valid);
  if (!usable.length) return el('p', { class: 'muted', text: 'Nincs érvényes szabály.' });
  const example = (pattern) => {
    const now = new Date();
    const pad = (n) => String(n).padStart(2, '0');
    return pattern.replaceAll('{yyyy}', String(now.getFullYear())).replaceAll('{MM}', pad(now.getMonth() + 1)).replaceAll('{dd}', pad(now.getDate()))
      .replaceAll('{jobId}', '<feladat-azonosító>');
  };
  const sep = folder.includes('\\') ? '\\' : '/';
  const rows = usable.map((rule) => {
    const mode = el('select', { 'aria-label': `${rule.name}: mód` },
      el('option', { value: 'append', text: 'Egy gyűjtő fájl (a sorok hozzáfűzve)' }), el('option', { value: 'per_job', text: 'Levelenként külön fájl' }));
    mode.value = rule.outputMode ?? 'append';
    const file = el('input', { type: 'text', value: rule.outputFile ?? '', 'aria-label': `${rule.name}: fájlnév`, spellcheck: 'false' });
    const preview = el('div', { class: 'small muted' });
    const status = el('div', { class: 'small' });
    const update = () => {
      preview.textContent = `Pl.: ${folder}${folder.endsWith(sep) ? '' : sep}${example(file.value.replaceAll('{ruleId}', rule.id))}`;
    };
    file.addEventListener('input', update);
    update();
    const save = el('button', { type: 'button', onclick: async () => {
      status.textContent = '';
      try {
        const r = await api('POST', `/rules/${encodeURIComponent(rule.folder)}/output`, { mode: mode.value, file: file.value });
        status.append(messageList([], r.warnings ?? [], 'Mentve.'));
      } catch (error) {
        status.append(messageList(error.body?.errors ?? [error.message]));
      }
    } }, 'Mentés');
    return el('tr', {},
      el('td', {}, el('strong', { text: rule.name ?? rule.folder }), rule.enabled ? null : el('div', { class: 'small muted', text: 'kikapcsolva' })),
      el('td', {}, mode),
      el('td', {}, file, preview, status),
      el('td', {}, save));
  });
  return el('div', {},
    el('div', { class: 'table-wrap' }, el('table', { class: 'output-rules' },
      el('thead', {}, el('tr', {}, ...['Szabály', 'Mód', 'Fájlnév', ''].map((h) => el('th', { text: h })))),
      el('tbody', {}, rows))),
    el('p', { class: 'muted small', text: 'Helyettesítők a fájlnévben: {yyyy} év, {MM} hónap, {dd} nap, {ruleId} a szabály azonosítója, {jobId} a feladat azonosítója (levelenkénti fájlhoz). Mappát nem tartalmazhat.' }));
}

// ---------- Napló ----------

async function renderLogs(container) {
  const level = el('select', { id: 'log-level', 'aria-label': 'Naplószint' }, el('option', { value: 'DBG', text: 'Debug és fölötte' }), el('option', { value: 'INF', text: 'Információ és fölötte' }),
    el('option', { value: 'WRN', text: 'Figyelmeztetés és hiba' }), el('option', { value: 'ERR', text: 'Csak hibák' }));
  level.value = 'INF';
  const lines = el('select', { id: 'log-lines', 'aria-label': 'Megjelenített sorok száma' }, ['100', '300', '1000'].map((n) => el('option', { value: n, text: `utolsó ${n}` })));
  lines.value = '300';
  const auto = el('input', { type: 'checkbox', id: 'log-auto' });
  const file = el('span', { class: 'muted small' });
  const log = el('div', { class: 'log', role: 'log' });

  async function refresh() {
    const r = await api('GET', `/logs?level=${level.value}&lines=${lines.value}`);
    file.textContent = r.file ? `Fájl: ${r.file}` : 'Még nincs naplófájl.';
    const atBottom = log.scrollTop + log.clientHeight >= log.scrollHeight - 30;
    log.textContent = '';
    for (const entry of r.entries) {
      log.append(el('div', { class: `log-line ${entry.level}` }, el('span', { class: 'time', text: entry.time }), el('span', { class: 'lvl', text: entry.level }),
        el('span', { class: 'src', text: `${entry.source}: ` }), entry.message));
    }
    if (!r.entries.length) log.append(el('div', { class: 'empty', text: 'Nincs megjeleníthető bejegyzés.' }));
    if (atBottom) log.scrollTop = log.scrollHeight;
  }

  level.addEventListener('change', refresh);
  lines.addEventListener('change', refresh);
  auto.addEventListener('change', () => {
    stopTimers();
    if (auto.checked) timers.push(setInterval(() => refresh().catch(() => {}), 5000));
  });

  container.append(
    el('div', { class: 'page-header' }, el('div', {}, el('h1', { text: 'Napló' }), file),
      el('div', { class: 'row' }, level, lines, el('label', { class: 'switch' }, auto, 'Automatikus frissítés'), el('button', { type: 'button', onclick: refresh }, 'Frissítés'))),
    log);
  await refresh();
  log.scrollTop = log.scrollHeight;
}

// ---------- start ----------

(async () => {
  try {
    const session = await api('GET', '/session');
    $('version').textContent = `v${session.version}`;
    $('quit').hidden = !session.canQuit;
    const expiredLink = new URLSearchParams(location.search).get('link') === 'lejart';
    if (expiredLink) history.replaceState(null, '', location.pathname + location.hash);
    if (session.authenticated) showApp();
    else {
      showLogin();
      if (expiredLink) $('login-error').textContent = 'Az indítási link lejárt vagy már felhasználták. Indítsd újra a programot (vagy a Start menü „EfMailAgent kezelőfelület” ikonját), vagy add meg a tokent.';
    }
  } catch {
    showLogin();
  }
})();
