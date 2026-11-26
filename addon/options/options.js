// Options page (Hungarian UI). Reads/writes storage.local directly; the background picks changes up on its next run.
import { createApi } from '../src/core/api.js';
import { DEFAULT_OPTIONS, loadConfig, saveOptions } from '../src/core/config.js';

const HOST_PERMISSION = { origins: ['http://127.0.0.1/*'] };
const STATUS_LABELS = {
  queued: 'sorban',
  processing: 'feldolgozás alatt',
  done: 'kész',
  needs_review: 'ellenőrzendő',
  failed: 'hiba',
};

const $ = (id) => document.getElementById(id);

function setStatus(element, text, kind) {
  element.textContent = text;
  element.className = `status ${kind ?? ''}`;
}

async function render() {
  const config = await loadConfig(messenger);
  const managed = config.managed.length > 0;
  $('managed-info').hidden = !managed;
  $('serverUrl').value = config.serverUrl;
  $('token').value = config.token;
  $('serverUrl').readOnly = config.managed.includes('serverUrl');
  $('token').readOnly = config.managed.includes('token');
  $('enabled').checked = config.enabled;
  $('backfillDays').value = String(config.backfillDays);
  await renderFolders(config.watchedFolderIds);
  await renderHistory();
  $('permission-box').hidden = await messenger.permissions.contains(HOST_PERMISSION);
}

async function renderFolders(selected) {
  const container = $('folders');
  container.textContent = '';
  const [accounts, folders] = await Promise.all([messenger.accounts.list(false), messenger.folders.query({})]);
  const accountNames = Object.fromEntries(accounts.map((a) => [a.id, a.name]));
  const usable = folders.filter((f) => !f.isRoot && !f.isVirtual && !f.isUnified && !f.isTag);
  let lastAccount = null;
  for (const folder of usable.sort((a, b) => `${a.accountId}${a.path}`.localeCompare(`${b.accountId}${b.path}`))) {
    if (folder.accountId !== lastAccount) {
      const heading = document.createElement('div');
      heading.className = 'account';
      heading.textContent = accountNames[folder.accountId] ?? folder.accountId;
      container.append(heading);
      lastAccount = folder.accountId;
    }
    const isInbox = (folder.specialUse ?? []).includes('inbox');
    const label = document.createElement('label');
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.value = folder.id;
    box.checked = isInbox || selected.includes(folder.id);
    box.disabled = isInbox;
    label.append(box, document.createTextNode(isInbox ? `${folder.path} (mindig figyelt)` : folder.path));
    container.append(label);
  }
  if (!usable.length) container.textContent = 'Nincs elérhető mappa.';
}

async function renderHistory() {
  const { history = [], outbox = [], openJobs = {} } = await messenger.storage.local.get(['history', 'outbox', 'openJobs']);
  const body = $('history');
  body.textContent = '';
  if (!history.length) {
    const row = body.insertRow();
    const cell = row.insertCell();
    cell.colSpan = 4;
    cell.className = 'muted';
    cell.textContent = 'Még nem volt beküldés.';
  }
  for (const entry of history) {
    const row = body.insertRow();
    row.insertCell().textContent = new Date(entry.at).toLocaleString('hu-HU');
    row.insertCell().textContent = entry.subject;
    row.insertCell().textContent = entry.ruleId ?? '';
    const badge = document.createElement('span');
    badge.className = `badge ${entry.status}`;
    badge.textContent = STATUS_LABELS[entry.status] ?? entry.status;
    row.insertCell().append(badge);
  }
  $('queue-info').textContent =
    `Feldolgozásra vár: ${Object.keys(openJobs).length} feladat. Kimenő sorban (az EfMailAgent nem volt elérhető): ${outbox.length} levél.`;
}

$('test').addEventListener('click', async () => {
  setStatus($('test-result'), 'Kapcsolódás…');
  try {
    const config = { ...(await loadConfig(messenger)), serverUrl: $('serverUrl').value.replace(/\/+$/, ''), token: $('token').value };
    const health = await createApi({ fetch, config }).health();
    const llm = health.llmReachable ? 'az AI elérhető' : 'az AI NEM érhető el';
    setStatus($('test-result'), `Kapcsolat rendben (EfMailAgent ${health.version}, ${llm}, sorban: ${health.queueLength}).`,
      health.llmReachable ? 'ok' : 'warn');
  } catch (error) {
    setStatus($('test-result'), error.message, 'error');
  }
});

$('grant').addEventListener('click', async () => {
  const granted = await messenger.permissions.request(HOST_PERMISSION);
  $('permission-box').hidden = granted;
});

$('save').addEventListener('click', async () => {
  const config = await loadConfig(messenger);
  const days = Number.parseInt($('backfillDays').value, 10);
  const options = {
    enabled: $('enabled').checked,
    backfillDays: Number.isFinite(days) ? Math.max(0, Math.min(60, days)) : DEFAULT_OPTIONS.backfillDays,
    watchedFolderIds: [...document.querySelectorAll('#folders input:checked:not(:disabled)')].map((i) => i.value),
  };
  if (!config.managed.includes('serverUrl')) options.serverUrl = $('serverUrl').value.trim() || DEFAULT_OPTIONS.serverUrl;
  if (!config.managed.includes('token')) options.token = $('token').value.trim();
  await saveOptions(messenger, options);
  setStatus($('save-result'), 'Mentve.', 'ok');
  messenger.runtime.sendMessage({ type: 'refreshMatchers' }).catch(() => {});
});

$('refresh').addEventListener('click', renderHistory);
$('rescan').addEventListener('click', async () => {
  const result = await messenger.runtime.sendMessage({ type: 'backfill' }).catch(() => null);
  $('queue-info').textContent = result ? `Átnézve: ${result.scanned} levél, beküldve: ${result.submitted}.` : 'A keresés nem sikerült.';
  await renderHistory();
});

render().catch((error) => setStatus($('test-result'), `Hiba a beállítások betöltésekor: ${error.message}`, 'error'));
