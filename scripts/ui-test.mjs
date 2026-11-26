#!/usr/bin/env node
// Admin UI usability test: walks through the operator tasks of docs/USER_GUIDE_HU.md in a real browser (Playwright).
//
// Local (starts its own EfMailAgent with a fresh data folder):
//   node scripts/ui-test.mjs [--exe "dotnet src/EfMailAgent/bin/Debug/net10.0/EfMailAgent.dll"] [--out dist/ui-test]
// Against a running / installed service (e.g. after the installer in CI):
//   node scripts/ui-test.mjs --url http://127.0.0.1:47811 --token <token> [--out dist/ui-test]
//
// The AI is switched to the mock provider through the Settings page (part of the test), so no LLM is needed.
// Exit code 0 = every check passed. Screenshots of every page (light, dark, phone width) go to --out.
import { createRequire } from 'node:module';
import { spawn, execFileSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createHmac, randomBytes } from 'node:crypto';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = parseArgs(process.argv.slice(2));
const OUT = resolve(args.out ?? join(ROOT, 'dist', 'ui-test'));
const URL_BASE = (args.url ?? 'http://127.0.0.1:47811').replace(/\/$/, '');
const ADMIN = `${URL_BASE}/admin/`;
mkdirSync(OUT, { recursive: true });

const results = [];
const consoleErrors = [];
let server = null;
let token = args.token;
let activePage = null;
const work = mkdtempSync(join(tmpdir(), 'efui-'));

// ---------- fixtures ----------

const mockDir = join(work, 'mock');
cpSync(join(ROOT, 'samples', 'mock-responses'), mockDir, { recursive: true });
mkdirSync(join(mockDir, 'reklamacio'), { recursive: true });
writeFileSync(join(mockDir, 'reklamacio', 'default.json'), JSON.stringify({
  rows: [{ partner_nev: 'Horgász Centrum Kft.', cikk_megnevezes: 'Feeder bot 3,6 m', hibas_mennyiseg: 2, szamlaszam: 'SZ-2026/1234',
    hiba_leirasa: 'Szállításkor eltört a spicc.', kert_megoldas: 'csere', bizonyossag: 'magas' }],
}, null, 2));

// The rule of the worked example in docs/USER_GUIDE_HU.md section 4.1.
const REKLAMACIO_YAML = `id: reklamacio
name: Reklamáció feldolgozás
enabled: false
priority: 20
match:
  subject_contains: ["reklamáció", "reklamacio", "panasz"]
  subject_regex: null
prompt_file: prompt.md
skills: []
output:
  mode: append
  file: "reklamacio_{yyyy}-{MM}.csv"
  rows_path: rows
  columns:
    - { name: partner_nev,      type: string,  required: true,  description: "A reklamáló partner neve" }
    - { name: cikk_megnevezes,  type: string,  required: true,  description: "A hibás termék megnevezése" }
    - { name: hibas_mennyiseg,  type: number,  required: false, description: "Hány darab hibás, számként" }
    - { name: szamlaszam,       type: string,  required: false, description: "Számla- vagy szállítólevélszám, ha szerepel" }
    - { name: hiba_leirasa,     type: string,  required: true,  description: "A hiba rövid leírása" }
    - { name: kert_megoldas,    type: enum, values: [csere, javitas, jovairas, egyeb], required: false, description: "Mit kér a partner" }
    - { name: bizonyossag,      type: enum, values: [magas, kozepes, alacsony], required: true, description: "Mennyire biztos a sor helyessége" }
`;
const REKLAMACIO_PROMPT = `## Feladat: reklamáció feldolgozása

Az e-mail egy vevői reklamáció, amelyet egy kolléga továbbított nekünk.

Szabályok:
- Minden reklamált termék külön sor.
- partner_nev: a reklamáló cég neve (nem a továbbító kolléga).
- hiba_leirasa: egy-két mondatban, a partner szavaival.
- Ha a partner nem írja, mit kér, a kert_megoldas legyen null.
- Ha nem egyértelmű, melyik termékről van szó, a bizonyossag legyen "alacsony".
`;

// ---------- runner ----------

/** Starts a section of the walk-through from a clean screen (a failed check may have left the job panel open). */
async function section(title) {
  console.log(`\n${title}`);
  if (activePage && await activePage.isVisible('#drawer').catch(() => false)) await activePage.keyboard.press('Escape');
}

async function check(name, fn) {
  const started = Date.now();
  try {
    await fn();
    results.push({ name, ok: true });
    console.log(`  OK    ${name} (${Date.now() - started} ms)`);
  } catch (error) {
    results.push({ name, ok: false, error: error.message.split('\n')[0] });
    console.log(`  HIBA  ${name}\n        ${error.message.split('\n').slice(0, 3).join('\n        ')}`);
  }
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function until(fn, message, timeoutMs = 15_000, intervalMs = 300) {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    try {
      last = await fn();
      if (last) return last;
    } catch (error) {
      last = error.message;
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(`${message} (utolsó érték: ${typeof last === 'string' ? last : JSON.stringify(last)})`);
}

async function apiGet(path) {
  const r = await fetch(`${URL_BASE}${path}`, { headers: { 'X-EfMailAgent-Token': token } });
  return r.json();
}

function submit(subject, file = 'samples/emails/demo_eloreendeles_01.eml') {
  const out = execFileSync(process.execPath, [join(ROOT, 'scripts', 'dev-submit.mjs'), '--url', URL_BASE, '--token', token,
    '--file', join(ROOT, file), '--subject', subject, '--new-message-id', '--no-wait'], { encoding: 'utf8' });
  return JSON.parse(out.slice(out.indexOf('{')));
}

// ---------- server ----------

async function startServer() {
  const dataDir = join(work, 'data');
  const exe = args.exe ?? `dotnet ${join(ROOT, 'src/EfMailAgent/bin/Debug/net10.0/EfMailAgent.dll')}`;
  const [cmd, ...pre] = exe.split(' ');
  // Like a fresh installation: Ollama at the default address, nothing running there.
  execFileSync(cmd, [...pre, 'configure', '--data-dir', dataDir, '--llm-provider', 'ollama', '--llm-base-url', 'http://127.0.0.1:9'], { stdio: 'ignore' });
  token = JSON.parse(readFileSync(join(dataDir, 'appsettings.json'), 'utf8').replace(/^﻿/, '')).Server.Token;
  server = spawn(cmd, [...pre, '--data-dir', dataDir, '--no-browser'], { stdio: ['ignore', 'pipe', 'pipe'] });
  let log = '';
  server.stdout.on('data', (d) => { log += d; });
  server.stderr.on('data', (d) => { log += d; });
  await until(async () => (await fetch(`${URL_BASE}/api/health`, { headers: { 'X-EfMailAgent-Token': token } })).ok,
    `az EfMailAgent nem indult el:\n${log}`, 30_000);
}

/** The one-time login link the exe opens on start (same format as src/EfMailAgent/Admin/LoginLink.cs). */
function loginLink(page) {
  const payload = `${Math.floor(Date.now() / 1000)}.${randomBytes(12).toString('base64url')}`;
  const signature = createHmac('sha256', `efmailagent-login-link:${token}`).update(payload).digest('base64url');
  return `${ADMIN}login?code=${encodeURIComponent(`${payload}.${signature}`)}${page ? `&page=${page}` : ''}`;
}

/** A tiny stand-in for Ollama (version, tags, streamed model download, chat) for the setup wizard. */
function startFakeOllama() {
  const models = ['llama3.2:1b'];
  const srv = http.createServer(async (req, res) => {
    const json = (code, body) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
    let body = '';
    for await (const chunk of req) body += chunk;
    if (req.url === '/api/version') return json(200, { version: '0.12.3' });
    if (req.url === '/api/tags') return json(200, { models: models.map((m) => ({ name: m, model: m, size: 2_000_000_000 })) });
    if (req.url === '/api/pull') {
      const { model } = JSON.parse(body || '{}');
      res.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
      const send = (o) => res.write(`${JSON.stringify(o)}\n`);
      send({ status: 'pulling manifest' });
      for (let done = 0; done <= 2_600_000_000; done += 520_000_000) {
        send({ status: 'pulling abc', digest: 'sha256:abc', total: 2_600_000_000, completed: done });
        await new Promise((r) => setTimeout(r, 150));
      }
      send({ status: 'verifying sha256 digest' });
      send({ status: 'success' });
      if (!models.includes(model)) models.push(model);
      return res.end();
    }
    if (req.url === '/api/chat') return json(200, { message: { role: 'assistant', content: '{"rows": []}' }, done: true });
    json(404, { error: 'not found' });
  });
  return new Promise((resolve) => srv.listen(0, '127.0.0.1', () => resolve({ srv, url: `http://127.0.0.1:${srv.address().port}` })));
}

// ---------- page helpers ----------

async function newPage(browser, options = {}) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'hu-HU', ...options });
  const page = await context.newPage();
  page.setDefaultTimeout(8000);
  page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(`${m.text()} (${page.url()})`); });
  page.on('pageerror', (e) => consoleErrors.push(`${e.message} (${page.url()})`));
  return page;
}

const dialogAnswers = [];
function answerDialogs(page) {
  page.on('dialog', async (dialog) => {
    const next = dialogAnswers.shift();
    page.lastDialog = dialog.message();
    if (next === undefined) await dialog.dismiss();
    else if (next === false) await dialog.dismiss();
    else await dialog.accept(next === true ? undefined : next);
  });
}

async function login(page) {
  await page.goto(ADMIN);
  await page.fill('#login-token', token);
  await page.press('#login-token', 'Enter');
  await page.waitForSelector('#app:not([hidden])');
}

async function go(page, hash) {
  await page.evaluate((h) => { location.hash = h; }, hash);
  await page.waitForTimeout(150);
  await page.waitForFunction(() => !document.querySelector('#page .spinner'));
}

async function toastText(page) {
  return (await page.locator('#toasts .toast').allTextContents()).join(' | ');
}

async function shot(page, name) {
  await page.screenshot({ path: join(OUT, `${name}.png`) });
}

async function noHorizontalOverflow(page, label) {
  const { sw, iw } = await page.evaluate(() => ({ sw: document.scrollingElement.scrollWidth, iw: window.innerWidth }));
  assert(sw <= iw + 1, `${label}: vízszintes görgetés (${sw} > ${iw} px)`);
}

async function unlabelledControls(page) {
  return page.evaluate(() => {
    const bad = [];
    for (const node of document.querySelectorAll('input, select, textarea, button')) {
      if (node.closest('[hidden]') || node.offsetParent === null) continue;
      const named = node.tagName === 'BUTTON'
        ? (node.textContent.trim() || node.getAttribute('aria-label'))
        : (node.closest('label') || node.getAttribute('aria-label') || (node.id && document.querySelector(`label[for="${node.id}"]`)));
      if (!named) bad.push(node.outerHTML.slice(0, 90));
    }
    return bad;
  });
}

// ---------- scenario ----------

try {
  if (!token) await startServer();
  console.log(`EfMailAgent: ${URL_BASE}, képernyőképek: ${OUT}`);
  const browser = await chromium.launch();
  const page = await newPage(browser);
  activePage = page;
  answerDialogs(page);

  await section('1. Bejelentkezés');
  await check('a bejelentkező oldal magyar útmutatót ad a tokenhez', async () => {
    await page.goto(ADMIN);
    await page.waitForSelector('#login:not([hidden])');
    assert((await page.textContent('#login')).includes('print-token'), 'nincs utalás a print-token parancsra');
    assert(await page.evaluate(() => document.activeElement?.id === 'login-token'), 'a token mező nem kap fókuszt');
    await shot(page, '01-bejelentkezes');
  });
  await check('hibás token: érthető hibaüzenet, nem lép be', async () => {
    await page.fill('#login-token', 'rossz-token');
    await page.press('#login-token', 'Enter');
    await until(async () => (await page.textContent('#login-error')).includes('Hibás token'), 'nincs „Hibás token.” üzenet');
    assert(await page.isHidden('#app'), 'hibás tokennel is belépett');
  });
  await check('helyes token Enterrel: Áttekintés, verziószám látszik', async () => {
    await login(page);
    await page.waitForSelector('text=Feladatok');
    assert(/^v\d+\.\d+\.\d+/.test(await page.textContent('#version')), 'nincs verziószám');
  });

  await check('indítási link (amit a program megnyit): token nélkül belép, a Beállítások oldalon', async () => {
    const fresh = await newPage(browser);
    const link = loginLink('beallitasok');
    await fresh.goto(link);
    await fresh.waitForSelector('#app:not([hidden])');
    await fresh.waitForSelector('#set-provider');
    assert(fresh.url().endsWith('#/beallitasok'), `oldal: ${fresh.url()}`);
    await fresh.context().close();
    const again = await newPage(browser);
    await again.goto(link);
    await again.waitForSelector('#login:not([hidden])');
    await until(async () => (await again.textContent('#login-error')).includes('lejárt'), 'a felhasznált link nem ad érthető üzenetet');
    assert(!again.url().includes('link='), 'a hibaparaméter az URL-ben maradt');
    await again.context().close();
  });

  await section('2. Első indulás: az AI még nincs beállítva');
  await check('az Áttekintés jelzi, hogy az AI nem érhető el, és a Beállításokhoz vezet', async () => {
    await go(page, '#/attekintes');
    await until(async () => (await page.textContent('.grid-cards')).includes('nem érhető el'), 'az AI kártya nem jelzi a hibát');
    await page.waitForSelector('#setup-banner >> text=Beállítások megnyitása');
    await shot(page, '02-ai-nem-erheto-el');
    await page.click('#setup-banner >> text=Beállítások megnyitása');
    await page.waitForSelector('#set-provider');
    await go(page, '#/attekintes');
  });
  let waitingJob;
  await check('levél érkezik AI nélkül: sorban marad, a felület jelzi a szünetet', async () => {
    waitingJob = submit('Fwd: Előrendelés AI nélkül');
    await page.click('text=Frissítés');
    await until(async () => (await page.textContent('.grid-cards')).includes('szünetel') || (await page.textContent('.grid-cards')).includes('1 feladat'),
      'a Sor kártya nem mutatja a várakozó levelet', 20_000, 1000);
    // The worker tries once, finds the AI unreachable and puts the job back without using up an attempt.
    await until(async () => { const j = await apiGet(`/api/jobs/${waitingJob.jobId}`); return j.status === 'queued' && j.attempts === 0; },
      'a levél nem maradt sorban 0 kísérlettel', 15_000, 500);
  });

  await section('3. Beállítások: AI szerver beállítása');
  await check('rossz érték (időkorlát 0) mentése: magyar hibaüzenet, nem ment', async () => {
    await go(page, '#/beallitasok');
    await page.fill('#set-timeout', '0');
    await page.click('button.primary:has-text("Mentés")');
    await until(async () => (await page.locator('.messages li.error').count()) > 0, 'nincs hibaüzenet');
    const text = await page.locator('.messages li.error').first().textContent();
    assert(/[áéíóöőúüű]/i.test(text), `a hibaüzenet nem magyar: ${text}`);
    await go(page, '#/attekintes');
    await go(page, '#/beallitasok');
    assert((await page.inputValue('#set-timeout')) !== '0', 'a hibás érték mégis elmentődött');
  });
  await check('szolgáltató váltása Mockra: a cím mező letiltva, mock mezők megjelennek', async () => {
    await page.selectOption('#set-provider', 'mock');
    assert(await page.isDisabled('#set-baseurl'), 'a szerver címe mező nincs letiltva');
    assert(await page.isVisible('#set-mockdir'), 'a mock mappa mező nem jelent meg');
    await page.fill('#set-mockdir', mockDir);
    await page.fill('#set-mockdelay', '150');
  });
  await check('„Kapcsolat tesztelése” mentés előtt is működik', async () => {
    await page.click('text=Kapcsolat tesztelése');
    await until(async () => (await page.textContent('#page')).includes('Kapcsolat rendben'), 'nincs „Kapcsolat rendben”');
  });
  await check('mentés: azonnal érvényes, újraindítás nélkül', async () => {
    await page.click('button.primary:has-text("Mentés")');
    await until(async () => (await page.textContent('#page')).includes('Mentve az appsettings.json-ba'), 'nincs mentés visszajelzés');
    await shot(page, '03-beallitasok');
  });
  await check('a várakozó levél az AI beállítása után magától feldolgozódik', async () => {
    await until(async () => ['needs_review', 'done'].includes((await apiGet(`/api/jobs/${waitingJob.jobId}`)).status),
      'a várakozó levél nem dolgozódott fel', 60_000, 1000);
    await go(page, '#/attekintes');
    await until(async () => (await page.textContent('.grid-cards')).includes('elérhető'), 'az AI kártya nem lett zöld');
    assert(!(await page.isVisible('#setup-banner')), 'a figyelmeztetés a beállítás után is látszik');
  });

  await section('4. Áttekintés: feladatok követése');
  const failed = submit('Fwd: Előrendelés [mock:invalid]');
  submit('Fwd: Előrendelés tavaszra', 'samples/emails/elorendeles_01_felsorolas.txt');
  submit('TOV: Előrendelési lista', 'samples/emails/elorendeles_03_tablazat.txt');
  await check('a lista frissül, a szűrőgombok darabszámai helyesek', async () => {
    await until(async () => (await apiGet('/api/jobs?status=queued')).jobs.length === 0 && (await apiGet('/api/jobs?status=processing')).jobs.length === 0,
      'a feldolgozás nem ért véget', 60_000, 500);
    await page.click('text=Frissítés');
    await until(async () => (await page.textContent('.chips')).includes('Mind (4)'), 'nem 4 feladat látszik');
    assert((await page.textContent('.chips')).includes('Hiba (1)'), 'a Hiba szűrő nem 1');
  });
  await check('minden oszlop látszik 1440 px szélességen (Sorok, Idő is)', async () => {
    const overflow = await page.evaluate(() => {
      const wrap = document.querySelector('#page .table-wrap');
      return { scroll: wrap.scrollWidth, client: wrap.clientWidth };
    });
    assert(overflow.scroll <= overflow.client + 1, `a feladatlista vízszintesen görgetni kell (${overflow.scroll} > ${overflow.client} px)`);
    await shot(page, '04-attekintes');
  });
  await check('„Hiba” szűrő: csak a hibás feladat, a hiba oka a sorban olvasható', async () => {
    await page.click('.chip:has-text("Hiba")');
    await until(async () => (await page.locator('tbody tr').count()) === 1, 'nem 1 sor maradt');
    assert((await page.textContent('tbody tr')).includes('javító hívás után sem'), 'a hiba oka nem látszik');
  });
  await check('„Ellenőrzendők megnyitása” link: csak az ellenőrzendők', async () => {
    await page.click('.chip:has-text("Mind")');
    await page.click('text=Ellenőrzendők megnyitása');
    await until(async () => (await page.locator('.chip.active').textContent()).startsWith('Ellenőrzendő'), 'a szűrő nem váltott');
    const statuses = await page.locator('tbody tr .badge').allTextContents();
    assert(statuses.length === 3 && statuses.every((s) => s === 'ellenőrzendő'), `státuszok: ${statuses}`);
  });
  await check('feladat megnyitása billentyűzettel (Tab + Enter), a fókusz a panelre kerül', async () => {
    await page.click('.chip:has-text("Hiba")');
    await until(async () => (await page.locator('tbody tr').count()) === 1, 'nincs hibás sor');
    await page.focus('tbody tr');
    await page.keyboard.press('Enter');
    await page.waitForSelector('#drawer:not([hidden]) >> text=Újrafuttatás');
    assert(await page.evaluate(() => document.getElementById('drawer').contains(document.activeElement)), 'a fókusz nem a panelen van');
    await shot(page, '05-feladat-reszletek');
  });
  await check('a panel mutatja a hibát, az eredeti feladót és a nyers AI választ', async () => {
    const text = await page.textContent('#drawer');
    for (const needle of ['javító hívás után sem', 'Eredeti feladó', 'Nagy Gábor', 'Nyers AI válasz']) assert(text.includes(needle), `hiányzik: ${needle}`);
  });
  await check('Escape bezárja a panelt, a fókusz visszakerül a listára', async () => {
    await page.keyboard.press('Escape');
    assert(await page.isHidden('#drawer'), 'a panel nem zárult be');
    assert(await page.evaluate(() => !!document.activeElement?.closest('tbody tr')), 'a fókusz nem tért vissza a sorra');
  });
  await check('Újrafuttatás: visszajelzés, újra lefut, és a lista a panel bezárásakor frissül', async () => {
    const before = (await apiGet(`/api/jobs/${failed.jobId}`)).finishedAt;
    await page.click('tbody tr');
    await page.click('#drawer >> text=Újrafuttatás');
    await until(async () => (await toastText(page)).includes('újra sorba került'), 'nincs visszajelzés');
    await until(async () => { const j = await apiGet(`/api/jobs/${failed.jobId}`); return j.status === 'failed' && j.finishedAt !== before; }, 'nem futott le újra', 30_000, 500);
    const refreshed = page.waitForRequest((r) => r.url().includes('/admin/api/jobs?'), { timeout: 2000 });
    await page.click('#drawer-close');
    await refreshed.catch(() => { throw new Error('a lista nem frissült a panel bezárásakor (csak 10 mp múlva)'); });
  });

  await section('5. Szabályok: új levéltípus felvétele (kézikönyv 4.1)');
  await check('„Új szabály”: azonosító és név bekérése, kikapcsolva jön létre', async () => {
    await go(page, '#/szabalyok');
    dialogAnswers.push('reklamacio', 'Reklamáció feldolgozás');
    await page.click('text=Új szabály');
    await until(async () => (await toastText(page)).includes('kikapcsolva'), 'nincs visszajelzés');
    await page.waitForSelector('.rule-item.active:has-text("Reklamáció feldolgozás")');
    assert((await page.textContent('.rule-item.active')).includes('kikapcsolva'), 'nem kikapcsolva jött létre');
  });
  await check('hibás rule.yaml (type: decimal): nem menti, magyarul megmondja a hibát', async () => {
    await page.fill('textarea.code', REKLAMACIO_YAML.replace('type: number', 'type: decimal'));
    await page.click('#page button.primary:has-text("Mentés")');
    await until(async () => (await page.textContent('#page')).includes('Nem mentettem'), 'nincs „Nem mentettem”');
    assert((await page.textContent('#page')).includes('decimal'), 'a hibaüzenet nem nevezi meg a hibás típust');
    await shot(page, '06-szabaly-hibas');
  });
  await check('mentetlen módosítás: a menüből elnavigálva rákérdez, és maradhatok', async () => {
    dialogAnswers.push(false);
    page.lastDialog = null;
    await page.click('.sidebar >> text=Áttekintés');
    await page.waitForTimeout(300);
    assert(page.lastDialog?.includes('nincsenek mentve'), 'nem kérdezett rá a mentetlen módosításra');
    assert((await page.inputValue('textarea.code')).includes('type: decimal'), 'a szerkesztett szöveg elveszett');
    assert(page.url().includes('#/szabalyok'), `elnavigált: ${page.url()}`);
  });
  await check('javított rule.yaml mentése (Ctrl+S): „Mentve”, érvényes', async () => {
    await page.fill('textarea.code', REKLAMACIO_YAML);
    await page.focus('textarea.code');
    await page.keyboard.press('Control+s');
    await until(async () => (await page.textContent('#page')).includes('Mentve'), 'nincs „Mentve”');
  });
  await check('prompt.md megírása a kézikönyv példájából', async () => {
    await page.click('.tab:has-text("prompt.md")');
    await page.fill('textarea.code', REKLAMACIO_PROMPT);
    await page.click('#page button.primary:has-text("Mentés")');
    await until(async () => (await page.textContent('#page')).includes('Mentve'), 'nincs „Mentve”');
  });
  await check('„+ Skill fájl”: létrejön, és bekerül a rule.yaml-ba', async () => {
    dialogAnswers.push('partnerek.md');
    await page.click('text=+ Skill fájl');
    await until(async () => (await toastText(page)).includes('Skill hozzáadva'), 'nincs visszajelzés');
    await page.waitForSelector('.tab.active:has-text("skills/partnerek.md")');
    await page.fill('textarea.code', 'Horgász Centrum Kft.;P00123\nPecás Paradicsom Bt.;P00456\n');
    await page.click('#page button.primary:has-text("Mentés")');
    await until(async () => (await page.textContent('#page')).includes('Mentve'), 'nincs „Mentve”');
    await page.click('.tab:has-text("rule.yaml")');
    await until(async () => (await page.inputValue('textarea.code')).includes('skills/partnerek.md'), 'a skill nem került a rule.yaml-ba');
    await shot(page, '07-szabalyok');
  });

  await section('6. Tesztkonzol');
  await check('„Tesztelés a Tesztkonzolban”: a szabály előre kiválasztva', async () => {
    await page.click('text=Tesztelés a Tesztkonzolban');
    await page.waitForSelector('#test-rule');
    assert((await page.inputValue('#test-rule')) === 'reklamacio', `kiválasztott: ${await page.inputValue('#test-rule')}`);
  });
  await check('kikapcsolt szabály is tesztelhető; Ctrl+Enter futtat; a sor „rendben”', async () => {
    await page.fill('#test-subject', 'Fwd: Reklamáció - eltört feeder bot');
    await page.fill('#test-from', 'Kovács Péter <kovacs.peter@ertekesites.example>');
    await page.fill('#test-body', 'Feladó: Nagy Gábor <nagy.gabor@horgaszcentrum.example>\n\nA múlt heti szállításban 2 db feeder bot spicce eltört (SZ-2026/1234). Cserét kérünk.');
    await page.focus('#test-body');
    await page.keyboard.press('Control+Enter');
    await until(async () => (await page.textContent('#page')).includes('Érvényes válasz, minden sor rendben'), 'nincs zöld eredmény');
    const text = await page.textContent('#page');
    for (const needle of ['hiba_leirasa', 'Szállításkor eltört a spicc.', 'rendben']) assert(text.includes(needle), `hiányzik: ${needle}`);
  });
  await check('a nyers válasz és az összeállított prompt megnézhető; a skill benne van', async () => {
    const details = page.locator('#page details', { has: page.locator('summary', { hasText: 'Összeállított prompt: rendszer' }) });
    await details.locator('summary').click();
    const system = await details.locator('pre').textContent();
    assert(system.includes('reklamáció feldolgozása'), `a szabály promptja hiányzik: ${system.slice(0, 200)}`);
    assert(system.includes('P00123'), `a skill hiányzik: …${system.slice(system.indexOf('###'), system.indexOf('###') + 300)}`);
    await shot(page, '08-tesztkonzol');
  });
  await check('mintalevél betöltése az előrendelés szabállyal: ellenőrzendő sor kiemelve', async () => {
    await page.selectOption('#test-rule', 'eloreendeles');
    await page.selectOption('#test-sample', { index: 1 });
    assert((await page.inputValue('#test-body')).length > 50, 'a minta nem töltődött be');
    await page.click('#test-run');
    await until(async () => (await page.locator('tr.row-review').count()) > 0, 'nincs kiemelt ellenőrzendő sor');
  });
  await check('üres levél futtatása: nem omlik össze, érthető eredmény', async () => {
    await page.fill('#test-body', '');
    await page.click('#test-run');
    await until(async () => !(await page.isDisabled('#test-run')) && !(await page.locator('#page .spinner').count()), 'a futás nem ért véget');
    assert((await page.locator('#page .card').count()) > 1, 'nincs eredmény kártya');
  });

  await section('7. Szabály bekapcsolása és éles levél');
  await check('bekapcsolás a kapcsolóval: visszajelzés, az Áttekintés 2 szabályt mutat', async () => {
    await go(page, '#/szabalyok/reklamacio');
    await page.click('.rule-item:has-text("Reklamáció") .switch');
    await until(async () => (await toastText(page)).includes('Bekapcsolva: Reklamáció'), 'nincs visszajelzés');
    await go(page, '#/attekintes');
    await until(async () => (await page.textContent('.grid-cards')).includes('2 bekapcsolva'), 'nem 2 bekapcsolt szabály');
  });
  await check('a matcher lista (amit az add-on letölt) tartalmazza az új kulcsszavakat', async () => {
    const m = await apiGet('/api/matchers');
    assert(m.matchers.some((x) => x.ruleId === 'reklamacio' && x.subjectContains.includes('reklamáció')), JSON.stringify(m.matchers));
  });
  await check('„Reklamáció” tárgyú levél: az új szabály dolgozza fel, kész, a CSV-ben megjelenik', async () => {
    const job = submit('Fwd: Reklamáció - eltört feeder bot');
    assert(job.ruleId === 'reklamacio', `szabály: ${job.ruleId}`);
    const done = await until(async () => { const j = await apiGet(`/api/jobs/${job.jobId}`); return j.status === 'done' && j; }, 'nem lett kész', 30_000, 500);
    const csv = readFileSync(done.outputFile, 'utf8');
    assert(csv.startsWith('﻿job_id;') && csv.includes('hiba_leirasa') && csv.includes('Szállításkor eltört a spicc.'), 'a CSV tartalma nem megfelelő');
    await page.click('text=Frissítés');
    await until(async () => (await page.textContent('tbody')).includes('Reklamáció'), 'nem jelent meg a listában');
  });

  await section('8. Kimenet: CSV fájl szabályonként (Beállítások)');
  await check('a Beállításokban szabályonként látszik a fájlnév és a teljes út előnézete', async () => {
    await go(page, '#/beallitasok');
    const row = page.locator('#output-section tr', { hasText: 'Reklamáció feldolgozás' });
    assert((await row.locator('input').inputValue()) === 'reklamacio_{yyyy}-{MM}.csv', 'nem a szabály fájlneve látszik');
    await row.locator('input').fill('panasz_{yyyy}{MM}{dd}.csv');
    const y = new Date().getFullYear();
    assert((await row.textContent()).includes(`panasz_${y}`), 'az előnézet nem frissül');
    await shot(page, '10-kimenet');
  });
  await check('hibás fájlnév (mappával): nem menti, magyar hibaüzenet', async () => {
    const row = page.locator('#output-section tr', { hasText: 'Reklamáció feldolgozás' });
    await row.locator('input').fill('../panasz.csv');
    await row.locator('button:has-text("Mentés")').click();
    await until(async () => (await row.textContent()).includes('nem tartalmazhat mappát'), 'nincs érthető hibaüzenet');
  });
  let newFile;
  await check('új fájlnév mentése: a következő levél már oda kerül', async () => {
    const row = page.locator('#output-section tr', { hasText: 'Reklamáció feldolgozás' });
    await row.locator('input').fill('panasz_{yyyy}{MM}{dd}.csv');
    await row.locator('button:has-text("Mentés")').click();
    await until(async () => (await row.textContent()).includes('Mentve'), 'nincs „Mentve”');
    const job = submit('Fwd: Reklamáció - második eset');
    const done = await until(async () => { const j = await apiGet(`/api/jobs/${job.jobId}`); return j.status === 'done' && j; }, 'nem lett kész', 30_000, 500);
    newFile = done.outputFile;
    assert(/panasz_\d{8}\.csv$/.test(newFile), `fájl: ${newFile}`);
    assert(readFileSync(newFile, 'utf8').startsWith('\uFEFFjob_id;'), 'az új fájl nem fejléccel kezdődik');
  });

  await section('9. Beállítások: Thunderbird bővítmény és AI telepítő (Ollama)');
  await check('Thunderbird bővítmény: állapot és egy gombos telepítés, érthető eredménnyel', async () => {
    await go(page, '#/beallitasok');
    await page.waitForSelector('#addon-section #addon-install');
    await page.click('#addon-section #addon-install');
    await until(async () => !(await page.locator('#addon-section .spinner').count()) && (await page.locator('#addon-section .messages li').count()) > 0,
      'nincs eredmény a telepítés után', 60_000, 500);
    const text = await page.textContent('#addon-section');
    if (process.env.EXPECT_ADDON_INSTALL) assert(text.includes('Indítsd újra a Thunderbirdöt') && text.includes('rendben, telepítve'), `a telepítés nem sikerült: ${text}`);
    else assert(text.includes('Thunderbird'), text);
    await shot(page, '10a-thunderbird');
  });
  const fakeOllama = await startFakeOllama();
  await check('AI telepítő: nem futó Ollamánál érthető teendő (telepítés gomb vagy letöltési link)', async () => {
    await page.waitForSelector('#ollama-wizard #ollama-url');
    await page.fill('#ollama-url', 'http://127.0.0.1:9');
    await page.click('#ollama-wizard >> text=Újraellenőrzés');
    await until(async () => (await page.textContent('#ollama-wizard')).includes('nem érhető el'), 'nem jelzi, hogy az Ollama nem fut');
    const text = await page.textContent('#ollama-wizard');
    assert((await page.locator('#ollama-install').count()) > 0 || text.includes('Az Ollama letöltése (ollama.com)'), 'nincs telepítés gomb és letöltési link sem');
  });
  await check('AI telepítő: futó Ollama, modellek ajánlással, letöltés folyamatjelzővel', async () => {
    await page.fill('#ollama-url', fakeOllama.url);
    await page.click('#ollama-wizard >> text=Újraellenőrzés');
    await until(async () => (await page.textContent('#ollama-wizard')).includes('Az Ollama fut (verzió: 0.12.3)'), 'nem ismerte fel a futó Ollamát');
    assert((await page.textContent('#ollama-wizard')).includes('ajánlott ehhez a géphez'), 'nincs ajánlott modell');
    await page.check('#ollama-wizard input[value="qwen3.5:4b"]');
    await page.click('#ollama-pull');
    await page.waitForSelector('#ollama-task progress');
    await shot(page, '10b-ollama-letoltes');
    await until(async () => (await page.textContent('#ollama-wizard')).includes('qwen3.5:4b modell letöltve'), 'a letöltés nem fejeződött be', 30_000, 300);
    const row = page.locator('#ollama-wizard tr', { hasText: 'Qwen3.5 4B' });
    assert((await row.textContent()).includes('letöltve'), 'a modell nem jelölődött letöltöttnek');
  });
  await check('AI telepítő: „Használat ezzel a modellel” beállítja a programot', async () => {
    await page.check('#ollama-wizard input[value="qwen3.5:4b"]');
    await page.click('#ollama-apply');
    await until(async () => (await page.textContent('#ollama-wizard')).includes('Kész: a program az Ollama qwen3.5:4b modelljével dolgozik'), 'nincs kész állapot', 15_000);
    assert((await page.inputValue('#set-provider')) === 'ollama' && (await page.inputValue('#set-model')) === 'qwen3.5:4b', 'a kézi beállításokban nem az új modell látszik');
    await shot(page, '10c-ollama-kesz');
    await go(page, '#/attekintes');
    await until(async () => (await page.textContent('.grid-cards')).includes('qwen3.5:4b'), 'az Áttekintés nem az új modellt mutatja');
  });

  await section('10. Napló');
  await check('a napló mutatja a feldolgozásokat; a szűrők kompakt méretűek', async () => {
    await go(page, '#/naplo');
    await until(async () => (await page.locator('.log-line').count()) > 5, 'kevés naplósor');
    const width = await page.locator('#log-level').evaluate((n) => n.getBoundingClientRect().width);
    assert(width < 400, `a szintválasztó túl széles (${Math.round(width)} px)`);
    await shot(page, '09-naplo');
  });
  await check('„Csak hibák” szűrő: csak ERR sorok', async () => {
    await page.selectOption('#log-level', 'ERR');
    await until(async () => {
      const levels = await page.locator('.log-line .lvl').allTextContents();
      return levels.length > 0 && levels.every((l) => l === 'ERR');
    }, 'nem csak hibák látszanak');
  });

  await section('11. Akadálymentesség, elrendezés, sötét mód, telefon');
  await check('egyik oldalon sem jelenik meg „null” vagy „undefined” szöveg', async () => {
    const bad = [];
    for (const hash of ['#/attekintes', '#/szabalyok', '#/tesztkonzol', '#/beallitasok']) { // the log shows data, not UI text
      await go(page, hash);
      await page.waitForTimeout(800);
      const text = await page.evaluate(() => document.getElementById('page').innerText);
      if (/(^|\s)(null|undefined)(\s|$)/m.test(text.replace(/"[^"]*"/g, ''))) bad.push(hash);
    }
    assert(!bad.length, `„null” / „undefined” látszik: ${bad.join(', ')}`);
  });
  await check('minden látható mezőnek és gombnak van neve', async () => {
    const bad = [];
    for (const hash of ['#/attekintes', '#/szabalyok', '#/tesztkonzol', '#/beallitasok', '#/naplo']) {
      await go(page, hash);
      for (const b of await unlabelledControls(page)) bad.push(`${hash}: ${b}`);
    }
    assert(!bad.length, bad.slice(0, 4).join('\n'));
  });
  for (const [label, options, prefix] of [
    ['sötét mód (1440 px)', { colorScheme: 'dark' }, '11-sotet'],
    ['telefon (390 px)', { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }, '12-telefon'],
  ]) {
    await check(`${label}: minden oldal görgetés nélkül fér el vízszintesen`, async () => {
      const p = await newPage(browser, options);
      await login(p);
      if (options.colorScheme === 'dark') {
        const bg = await p.evaluate(() => getComputedStyle(document.body).backgroundColor);
        assert(/rgb\((\d+), (\d+), (\d+)\)/.exec(bg).slice(1).every((c) => Number(c) < 60), `a háttér nem sötét: ${bg}`);
      }
      for (const [hash, name] of [['#/attekintes', 'attekintes'], ['#/szabalyok', 'szabalyok'], ['#/tesztkonzol', 'tesztkonzol'], ['#/beallitasok', 'beallitasok'], ['#/naplo', 'naplo']]) {
        await go(p, hash);
        await p.waitForTimeout(300);
        await noHorizontalOverflow(p, hash);
        await p.screenshot({ path: join(OUT, `${prefix}-${name}.png`), fullPage: !options.colorScheme });
      }
      await p.context().close();
    });
  }

  await section('12. Kijelentkezés és kilépés');
  await check('kijelentkezés után a belső oldalak nem érhetők el', async () => {
    await page.click('#logout');
    await page.waitForSelector('#login:not([hidden])');
    await page.goto(`${ADMIN}#/beallitasok`);
    await page.waitForSelector('#login:not([hidden])');
    assert(await page.isHidden('#app'), 'kijelentkezve is látszik az alkalmazás');
  });

  if (server) {
    await check('„Kilépés a programból”: megerősítés után a program leáll, az ablak ezt jelzi', async () => {
      await login(page);
      await page.waitForSelector('#quit:not([hidden])');
      dialogAnswers.push(true, true);
      await page.click('#quit');
      await page.waitForSelector('#stopped:not([hidden])');
      await until(() => server.exitCode !== null, 'a program nem állt le', 15_000, 200);
      await shot(page, '13-kilepes');
    });
  } else if (process.env.EXPECT_SERVICE) {
    await check('szolgáltatásként futva nincs „Kilépés” gomb (a szolgáltatást a Windows kezeli)', async () => {
      await login(page);
      assert(await page.isHidden('#quit'), 'a Kilépés gomb látszik a szolgáltatásnál');
    });
  }

  await check('nem volt JavaScript-hiba a böngésző konzolon', async () => {
    // A refused request (401 wrong token, 422 invalid input) is logged by the browser but is an expected answer.
    const real = consoleErrors.filter((e) => !/status of (401|409|422)/.test(e));
    assert(!real.length, real.slice(0, 5).join('\n'));
  });

  await browser.close();
  fakeOllama.srv.close();
} catch (error) {
  results.push({ name: 'a teszt futása', ok: false, error: error.stack });
  console.error(error);
} finally {
  if (server) server.kill();
  try { rmSync(work, { recursive: true, force: true }); } catch { /* the service may still hold a file */ }
}

const failedChecks = results.filter((r) => !r.ok);
writeFileSync(join(OUT, 'result.json'), JSON.stringify(results, null, 2));
console.log(`\n${results.length - failedChecks.length}/${results.length} ellenőrzés sikeres.`);
for (const f of failedChecks) console.log(`  HIBA: ${f.name}: ${f.error}`);
process.exit(failedChecks.length ? 1 : 0);

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith('--')) continue;
    const key = argv[i].slice(2);
    out[key] = argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[++i] : true;
  }
  return out;
}
