#!/usr/bin/env node
// Posts a sample email to the running EfMailAgent API and waits for the job to finish (development helper).
// Usage: node scripts/dev-submit.mjs [--file samples/emails/demo_eloreendeles_01.eml] [--data-dir .devdata]
//        [--url http://127.0.0.1:47811] [--token <token>] [--subject "..."] [--from "..."] [--rule <ruleId>]
//        [--new-message-id] [--no-wait]
import { readFileSync, existsSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { randomUUID } from 'node:crypto';

const args = parseArgs(process.argv.slice(2));
const dataDir = resolve(args['data-dir'] ?? '.devdata');
const settingsPath = join(dataDir, 'appsettings.json');
const settings = existsSync(settingsPath) ? JSON.parse(readFileSync(settingsPath, 'utf8')) : {};
const url = (args.url ?? `http://127.0.0.1:${settings.Server?.Port ?? 47811}`).replace(/\/$/, '');
const token = args.token ?? settings.Server?.Token;
if (!token) fail(`Nincs token: add meg a --token kapcsolóval, vagy indítsd el egyszer az EfMailAgent-et (${settingsPath}).`);

const file = resolve(args.file ?? 'samples/emails/demo_eloreendeles_01.eml');
const email = loadEmail(file);
const payload = {
  messageId: args['new-message-id'] || !email.messageId ? `<dev-submit-${randomUUID()}@efmailagent.local>` : email.messageId,
  account: 'dev-submit',
  folder: 'Inbox',
  receivedAt: email.date ? new Date(email.date).toISOString() : new Date().toISOString(),
  from: args.from ?? email.from ?? 'Fejlesztő <dev@efmailagent.local>',
  to: email.to ?? 'eloreendeles@efmailagent.local',
  subject: args.subject ?? email.subject ?? '',
  bodyText: email.body,
  attachmentNames: [],
  source: args.rule ? 'manual' : 'auto',
  ...(args.rule ? { ruleId: args.rule } : {}),
};

const headers = { 'X-EfMailAgent-Token': token, 'Content-Type': 'application/json' };
const response = await fetch(`${url}/api/jobs`, { method: 'POST', headers, body: JSON.stringify(payload) }).catch((e) =>
  fail(`Az API nem érhető el (${url}): ${e.cause?.code ?? e.message}. Fut az EfMailAgent?`));
const body = await response.json().catch(() => ({}));
console.log(`POST /api/jobs -> HTTP ${response.status}`, JSON.stringify(body));
if (response.status !== 202 && response.status !== 200) process.exit(1);
if (args['no-wait']) process.exit(0);

const deadline = Date.now() + 180_000;
for (;;) {
  const job = await (await fetch(`${url}/api/jobs/${body.jobId}`, { headers })).json();
  if (['done', 'needs_review', 'failed'].includes(job.status)) {
    console.log(`Feladat ${job.id}: ${job.status}, sorok: ${job.rowCount ?? '-'}, kísérletek: ${job.attempts}`);
    if (job.outputFile) console.log(`CSV: ${job.outputFile}`);
    if (job.error) console.log(`Megjegyzés / hiba: ${job.error}`);
    process.exit(job.status === 'failed' ? 1 : 0);
  }
  if (Date.now() > deadline) fail(`A feladat 3 perc alatt sem fejeződött be (állapot: ${job.status}).`);
  await new Promise((r) => setTimeout(r, 1000));
}

function loadEmail(path) {
  if (!existsSync(path)) fail(`A fájl nem található: ${path}`);
  const text = readFileSync(path, 'utf8').replace(/\r\n/g, '\n');
  if (!path.toLowerCase().endsWith('.eml')) {
    const indexPath = join(dirname(path), 'index.json');
    const entry = existsSync(indexPath)
      ? JSON.parse(readFileSync(indexPath, 'utf8')).emails.find((e) => e.file === basename(path))
      : undefined;
    return { body: text, subject: entry?.subject, from: entry?.from, date: entry?.receivedAt };
  }
  // Minimal parser for the plain-text sample .eml files (no MIME decoding).
  const split = text.indexOf('\n\n');
  const headerText = (split < 0 ? text : text.slice(0, split)).replace(/\n[ \t]+/g, ' ');
  const headers = {};
  for (const line of headerText.split('\n')) {
    const i = line.indexOf(':');
    if (i > 0) headers[line.slice(0, i).trim().toLowerCase()] = line.slice(i + 1).trim();
  }
  return {
    messageId: headers['message-id'],
    date: headers.date,
    from: headers.from,
    to: headers.to,
    subject: headers.subject,
    body: split < 0 ? '' : text.slice(split + 2),
  };
}

function parseArgs(argv) {
  const result = {};
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith('--')) continue;
    const key = argv[i].slice(2);
    const next = argv[i + 1];
    if (next === undefined || next.startsWith('--')) result[key] = true;
    else { result[key] = next; i++; }
  }
  return result;
}

function fail(message) {
  console.error(message);
  process.exit(1);
}
