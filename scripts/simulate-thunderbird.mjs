#!/usr/bin/env node
// End-to-end simulation (docs/DEV_MODE.md section 4): the real add-on core runs against the fake Thunderbird API
// and talks to the REAL running EfMailAgent (real token, real HTTP, mock LLM). Exits non-zero on any mismatch.
// Usage: node scripts/simulate-thunderbird.mjs --data-dir .e2edata
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { createAgent, ALARM_POLL } from '../addon/src/core/agent.js';
import { wireAgent } from '../addon/src/core/wiring.js';
import { createFakeMessenger } from '../addon/test/fake-messenger.js';

const ROOT = resolve(import.meta.dirname, '..');
const args = process.argv.slice(2);
const dataDir = resolve(args[args.indexOf('--data-dir') + 1] ?? '.e2edata');
const settings = JSON.parse(readFileSync(join(dataDir, 'appsettings.json'), 'utf8'));
const serverUrl = `http://127.0.0.1:${settings.Server.Port ?? 47811}`;
const token = settings.Server.Token;
const INBOX = 'account1://INBOX';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const step = (text) => console.log(`\n== ${text}`);

// Every request the add-on makes, with the parsed response, for the assertions below.
const exchanges = [];
async function recordingFetch(url, init = {}) {
  const response = await fetch(url, init);
  const copy = response.clone();
  const text = await copy.text();
  exchanges.push({ method: init.method ?? 'GET', path: new URL(url).pathname, request: init.body ? JSON.parse(init.body) : null, status: response.status, body: text ? JSON.parse(text) : null });
  return response;
}
const submissions = () => exchanges.filter((e) => e.method === 'POST' && e.path === '/api/jobs');

async function waitForApi() {
  for (let i = 0; i < 90; i++) {
    try {
      const response = await fetch(`${serverUrl}/api/health`, { headers: { 'X-EfMailAgent-Token': token } });
      if (response.ok) return response.json();
    } catch {
      // not up yet
    }
    await sleep(1000);
  }
  throw new Error(`Az EfMailAgent nem indult el (${serverUrl}).`);
}

function maskCsv(bytes) {
  return bytes.toString('utf8').split('\r\n').map((line, i) => {
    if (i === 0 || !line) return line;
    const [, , ...rest] = line.split(';');
    return ['<job_id>', '<feldolgozva>', ...rest].join(';');
  }).join('\r\n');
}

try {
  step('EfMailAgent elérhetősége');
  const health = await waitForApi();
  console.log(`EfMailAgent ${health.version}, LLM: ${health.llmProvider}, szabályverzió: ${health.rulesVersion}`);

  // The policy path: server URL and token come from storage.managed, exactly as policies.json "3rdparty" provides them.
  const fake = createFakeMessenger({ managed: { serverUrl, token } });
  fake.addFolder({ id: INBOX, name: 'Inbox', path: '/INBOX', specialUse: ['inbox'] });
  const agent = createAgent({ messenger: fake.messenger, fetch: recordingFetch, log: { info: (...a) => console.log(...a), warn: (...a) => console.warn(...a) } });
  wireAgent(fake.messenger, agent);

  step('Thunderbird indulás (címkék, szabályok, menü)');
  await fake.events.startup.fire();
  assert.deepEqual(fake.tags.map((t) => t.tag), ['AI: sorban', 'AI: kész', 'AI: ellenőrzendő', 'AI: hiba']);
  assert.ok(fake.menus.has('efmailagent-rule:eloreendeles'), 'a helyi menüben szerepel a szabály');

  step('Új levél: demo előrendelés');
  const demo = fake.addEml(join(ROOT, 'samples/emails/demo_eloreendeles_01.eml'), INBOX);
  await fake.fireNewMail(INBOX, [demo]);
  assert.equal(submissions().length, 1, 'pontosan egy beküldés');
  const first = submissions()[0];
  assert.deepEqual(first.request, JSON.parse(readFileSync(join(ROOT, 'samples/expected/demo_payload.json'), 'utf8')), 'a payload egyezik a demo_payload.json-nal');
  assert.equal(first.status, 202);
  assert.deepEqual(fake.tagNamesOf(demo), ['AI: sorban']);
  const jobId = first.body.jobId;
  console.log(`Feladat: ${jobId}, címke: AI: sorban`);

  step('Állapot lekérdezése, amíg a feladat elkészül');
  for (let i = 0; i < 120 && fake.tagNamesOf(demo).includes('AI: sorban'); i++) {
    await sleep(500);
    await fake.fireAlarm(ALARM_POLL);
  }
  assert.deepEqual(fake.tagNamesOf(demo), ['AI: ellenőrzendő'], 'a demo levél ellenőrzendő (az utolsó tétel bizonytalan)');

  const job = await (await fetch(`${serverUrl}/api/jobs/${jobId}`, { headers: { 'X-EfMailAgent-Token': token } })).json();
  assert.equal(job.status, 'needs_review');
  assert.equal(job.rowCount, 5);
  console.log(`Kész: ${job.status}, ${job.rowCount} sor, CSV: ${job.outputFile}`);

  step('CSV ellenőrzése');
  const csv = readFileSync(job.outputFile);
  const expected = readFileSync(join(ROOT, 'samples/expected/demo_eloreendeles_01.csv'));
  assert.deepEqual([...csv.subarray(0, 3)], [0xef, 0xbb, 0xbf], 'UTF-8 BOM');
  const text = csv.toString('utf8');
  assert.ok(text.slice(1).startsWith('job_id;feldolgozva;email_datum;kuldo;eredeti_kuldo;targy;message_id;statusz;'), "';' elválasztó és fejléc");
  assert.equal(text.split('\n').length - 1, text.split('\r\n').length - 1, 'minden sorvég CRLF');
  assert.ok(text.includes('"Kartononként 12 db; ár: a szokásos; szállítás: március eleje"'), "a ';'-t tartalmazó cella idézőjelben");
  assert.equal(maskCsv(csv), maskCsv(expected), 'a CSV egyezik a samples/expected/demo_eloreendeles_01.csv fájllal (job_id és feldolgozva nélkül)');

  step('Nem illeszkedő levél');
  const other = fake.addEml(join(ROOT, 'samples/emails/demo_nem_illeszkedo.eml'), INBOX);
  await fake.fireNewMail(INBOX, [other]);
  assert.equal(submissions().length, 1, 'a nem illeszkedő levél nem lett beküldve');
  assert.deepEqual(fake.tagsOf(other), []);

  step('Ugyanaz a levél még egyszer (duplikátum)');
  const again = fake.addEml(join(ROOT, 'samples/emails/demo_eloreendeles_01.eml'), INBOX);
  await fake.fireNewMail(INBOX, [again]);
  assert.equal(submissions().length, 2);
  const second = submissions()[1];
  assert.equal(second.status, 200, 'a második beküldés 200 (már ismert)');
  assert.equal(second.body.jobId, jobId, 'ugyanaz a feladat');
  assert.equal(second.body.duplicate, true);
  assert.deepEqual(readFileSync(job.outputFile), csv, 'nem lettek duplikált sorok');
  assert.deepEqual(fake.tagNamesOf(again), ['AI: ellenőrzendő']);

  console.log('\nE2E SZIMULÁCIÓ SIKERES');
} catch (error) {
  console.error(`\nE2E SZIMULÁCIÓ SIKERTELEN: ${error.message}`);
  if (error.actual !== undefined) console.error({ actual: error.actual, expected: error.expected });
  process.exit(1);
}
