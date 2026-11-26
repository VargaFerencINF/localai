import assert from 'node:assert/strict';
import test from 'node:test';
import { ALARM_MATCHERS, ALARM_POLL, MENU_ROOT, MENU_RULE_PREFIX, OUTBOX_LIMIT } from '../src/core/agent.js';
import { TAGS } from '../src/core/tags.js';
import { ARCHIVE, DEMO_EML, INBOX, NON_MATCHING_EML, ORDERS, setup } from './helpers.js';

test('startup creates the four tags with distinct colors, alarms, menus, and downloads matchers', async () => {
  const { fake, agent, exe } = await setup();
  await agent.onStartup();

  assert.deepEqual(fake.tags.map((t) => t.tag), ['AI: sorban', 'AI: kész', 'AI: ellenőrzendő', 'AI: hiba']);
  assert.equal(new Set(fake.tags.map((t) => t.color)).size, 4);
  assert.deepEqual(fake.alarms.get(ALARM_POLL), { name: ALARM_POLL, periodInMinutes: 1 });
  assert.deepEqual(fake.alarms.get(ALARM_MATCHERS), { name: ALARM_MATCHERS, periodInMinutes: 15 });
  assert.equal(fake.menus.get(MENU_ROOT).title, 'Küldés AI feldolgozásra');
  assert.deepEqual(fake.menus.get(MENU_ROOT).contexts, ['message_list']);
  const child = fake.menus.get(`${MENU_RULE_PREFIX}eloreendeles`);
  assert.equal(child.parentId, MENU_ROOT);
  assert.equal(child.title, 'Előrendelés feldolgozás');
  assert.equal(fake.local.matchers.rulesVersion, 'v1');
  assert.ok(exe.state.requests.every((r) => r.headers['x-efmailagent-token'] === exe.token));

  // A second startup must not create the tags again.
  await agent.onStartup();
  assert.equal(fake.callsOf('messages.tags.create').length, 4);
});

test('an existing user tag with the same label is reused', async () => {
  const { fake, agent } = await setup();
  await fake.messenger.messages.tags.create('$sajat', 'AI: kész', '#000000');
  await agent.ensureTags();
  assert.equal(fake.local.tagKeys.kesz, '$sajat');
  assert.equal(fake.tags.filter((t) => t.tag === 'AI: kész').length, 1);
});

test('demo email: submitted, tagged "AI: sorban", then "AI: ellenőrzendő" after polling', async () => {
  const { fake, agent, exe } = await setup();
  await agent.onStartup();
  const id = fake.addEml(DEMO_EML, INBOX);

  await fake.fireNewMail(INBOX, [id]);

  assert.equal(exe.submissions().length, 1);
  assert.deepEqual(fake.tagNamesOf(id), ['AI: sorban']);
  assert.equal(Object.keys(fake.local.openJobs).length, 1);
  assert.equal(fake.local.history[0].status, 'queued');

  await fake.fireAlarm(ALARM_POLL);
  assert.deepEqual(fake.tagNamesOf(id), ['AI: sorban'], 'still queued on the exe side');

  exe.advance('needs_review');
  await fake.fireAlarm(ALARM_POLL);
  assert.deepEqual(fake.tagNamesOf(id), ['AI: ellenőrzendő']);
  assert.deepEqual(fake.local.openJobs, {});
  assert.equal(fake.local.history[0].status, 'needs_review');
});

test('done and failed jobs get "AI: kész" and "AI: hiba"; foreign tags stay', async () => {
  const { fake, agent, exe } = await setup();
  await agent.onStartup();
  await fake.messenger.messages.tags.create('$label1', 'Fontos', '#ff0000');
  const ok = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés 1', tags: ['$label1'] });
  const bad = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés 2' });
  await fake.fireNewMail(INBOX, [ok, bad]);

  exe.state.jobs.get('job-1').status = 'done';
  exe.state.jobs.get('job-2').status = 'failed';
  await fake.fireAlarm(ALARM_POLL);

  assert.deepEqual(fake.tagNamesOf(ok), ['Fontos', 'AI: kész']);
  assert.deepEqual(fake.tagNamesOf(bad), ['AI: hiba']);
});

test('non-matching subject is not submitted', async () => {
  const { fake, agent, exe } = await setup();
  await agent.onStartup();
  const id = fake.addEml(NON_MATCHING_EML, INBOX);

  await fake.fireNewMail(INBOX, [id]);

  assert.equal(exe.submissions().length, 0);
  assert.deepEqual(fake.tagsOf(id), []);
});

test('only the inbox and folders chosen in the options are watched', async () => {
  const { fake, agent, exe } = await setup({ options: { watchedFolderIds: [ORDERS] } });
  await agent.onStartup();
  const inArchive = fake.addMessage({ folderId: ARCHIVE, subject: 'Előrendelés archív' });
  const inOrders = fake.addMessage({ folderId: ORDERS, subject: 'Előrendelés mappában' });

  await fake.fireNewMail(ARCHIVE, [inArchive]);
  await fake.fireNewMail(ORDERS, [inOrders]);

  assert.deepEqual(exe.submissions().map((s) => s.body.subject), ['Előrendelés mappában']);
});

test('disabled add-on submits nothing', async () => {
  const { fake, agent, exe } = await setup({ options: { enabled: false } });
  await agent.refreshMatchers();
  const id = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés' });
  await fake.fireNewMail(INBOX, [id]);
  assert.equal(exe.submissions().length, 0);
});

test('paginated MessageList from onNewMailReceived is fully processed', async () => {
  const { fake, agent, exe } = await setup({ messengerOptions: { pageSize: 2 } });
  await agent.refreshMatchers();
  const ids = [1, 2, 3, 4, 5].map((n) => fake.addMessage({ folderId: INBOX, subject: `Előrendelés ${n}` }));
  await fake.fireNewMail(INBOX, ids);
  assert.equal(exe.submissions().length, 5);
  assert.ok(fake.callsOf('messages.continueList').length >= 2);
});

test('exe unreachable: payload goes to the outbox, no tag; flushed with backoff when it is back', async () => {
  let clock = Date.parse('2026-11-26T08:00:00Z');
  const { fake, agent, exe } = await setup({ now: () => clock });
  await agent.onStartup();
  const id = fake.addEml(DEMO_EML, INBOX);

  exe.state.down = true;
  await fake.fireNewMail(INBOX, [id]);
  assert.deepEqual(fake.tagsOf(id), [], 'never tag before 200/202');
  assert.equal(fake.local.outbox.length, 1);
  assert.equal(fake.local.outbox[0].nextAttemptAt, clock + 60_000);

  await fake.fireAlarm(ALARM_POLL); // not due yet
  assert.equal(fake.local.outbox[0].attempts, 1);

  clock += 61_000;
  await fake.fireAlarm(ALARM_POLL); // due, still down -> attempt 2, next in 2 minutes
  assert.equal(fake.local.outbox[0].attempts, 2);
  assert.equal(fake.local.outbox[0].nextAttemptAt, clock + 120_000);

  exe.state.down = false;
  clock += 121_000;
  await fake.fireAlarm(ALARM_POLL);
  assert.deepEqual(fake.local.outbox, []);
  assert.deepEqual(fake.tagNamesOf(id), ['AI: sorban']);
  assert.equal(exe.submissions().filter((s) => s.body).length >= 1, true);
});

test('401 (wrong token) keeps the message in the outbox instead of dropping it', async () => {
  const { fake, agent, exe } = await setup();
  await agent.onStartup();
  await fake.messenger.storage.local.set({ options: { token: 'rossz', serverUrl: 'http://127.0.0.1:47811' } });
  const id = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés' });
  await fake.fireNewMail(INBOX, [id]);
  assert.equal(fake.local.outbox.length, 1);
  assert.equal(exe.state.jobs.size, 0);
});

test('outbox is capped at 500 items with a warning', async () => {
  const { fake, agent, exe, logs } = await setup();
  await agent.onStartup();
  await fake.messenger.storage.local.set({
    outbox: Array.from({ length: OUTBOX_LIMIT }, (_, i) => ({ tbMessageId: 1000 + i, payload: { messageId: `<o${i}@x>`, subject: 's' }, attempts: 1, nextAttemptAt: Date.now() + 3_600_000 })),
  });
  exe.state.down = true;
  const id = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés' });

  await fake.fireNewMail(INBOX, [id]);

  assert.equal(fake.local.outbox.length, OUTBOX_LIMIT);
  assert.ok(logs.some(([level, text]) => level === 'warn' && text.includes('megtelt')));
});

test('422 from the exe (no rule matches) leaves the message untagged and out of the outbox', async () => {
  const { fake, agent, exe } = await setup();
  await agent.onStartup();
  exe.state.submitOverride = () => ({ status: 422, body: { error: 'nincs' } });
  const id = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés' });
  await fake.fireNewMail(INBOX, [id]);
  assert.deepEqual(fake.tagsOf(id), []);
  assert.equal(fake.local.outbox ?? undefined, undefined);
});

test('duplicate answer (200) with a finished job sets the final tag directly', async () => {
  const { fake, agent, exe } = await setup();
  await agent.onStartup();
  exe.state.submitOverride = () => ({ status: 200, body: { jobId: 'job-9', ruleId: 'eloreendeles', status: 'done', duplicate: true } });
  const id = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés' });
  await fake.fireNewMail(INBOX, [id]);
  assert.deepEqual(fake.tagNamesOf(id), ['AI: kész']);
  assert.deepEqual(fake.local.openJobs ?? {}, {});
});

test('matchers are requested with If-None-Match and a changed rulesVersion triggers a refresh', async () => {
  const { fake, agent, exe } = await setup();
  await agent.onStartup();

  await fake.fireAlarm(ALARM_MATCHERS);
  const conditional = exe.state.requests.filter((r) => r.path === '/api/matchers').at(-1);
  assert.equal(conditional.headers['if-none-match'], '"v1"');

  exe.state.rulesVersion = 'v2';
  exe.state.matchers = [...exe.state.matchers, { ruleId: 'arajanlat', name: 'Árajánlat', subjectContains: ['árajánlat'], subjectRegex: null, priority: 20 }];
  await fake.fireAlarm(ALARM_POLL); // health shows a new rulesVersion
  assert.equal(fake.local.matchers.rulesVersion, 'v2');
  assert.ok(fake.menus.has(`${MENU_RULE_PREFIX}arajanlat`));
});

test('mail arriving before matchers were ever loaded is processed once they arrive', async () => {
  const { fake, agent, exe } = await setup();
  exe.state.down = true;
  await agent.onStartup();
  const id = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés korán' });
  await fake.fireNewMail(INBOX, [id]);
  assert.deepEqual(fake.local.pendingMatch, [id]);

  exe.state.down = false;
  await fake.fireAlarm(ALARM_MATCHERS);
  assert.equal(exe.submissions().filter((s) => s.body?.subject === 'Előrendelés korán').length, 1);
  assert.deepEqual(fake.tagNamesOf(id), ['AI: sorban']);
});

test('backfill submits untagged matching messages of the last N days only', async () => {
  const now = Date.parse('2026-11-26T08:00:00Z');
  const { fake, agent, exe } = await setup({ now: () => now, options: { backfillDays: 3, watchedFolderIds: [ORDERS] }, messengerOptions: { pageSize: 1 } });
  await agent.ensureTags();
  const recent = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés friss', date: new Date(now - 86_400_000) });
  fake.addMessage({ folderId: INBOX, subject: 'Előrendelés régi', date: new Date(now - 5 * 86_400_000) });
  const tagged = fake.addMessage({ folderId: INBOX, subject: 'Előrendelés kész', date: new Date(now - 3_600_000), tags: [TAGS.kesz.key] });
  fake.addMessage({ folderId: INBOX, subject: 'Hírlevél', date: new Date(now - 3_600_000) });
  const inOrders = fake.addMessage({ folderId: ORDERS, subject: 'Pre-order Q2', date: new Date(now - 3_600_000) });
  fake.addMessage({ folderId: ARCHIVE, subject: 'Előrendelés archív', date: new Date(now - 3_600_000) });

  await agent.onStartup();

  assert.deepEqual(exe.submissions().map((s) => s.body.subject).sort(), ['Előrendelés friss', 'Pre-order Q2']);
  assert.deepEqual(fake.tagNamesOf(recent), ['AI: sorban']);
  assert.deepEqual(fake.tagNamesOf(inOrders), ['AI: sorban']);
  assert.deepEqual(fake.tagNamesOf(tagged), ['AI: kész']);
  const query = fake.callsOf('messages.query')[0].args[0];
  assert.equal(new Date(query.fromDate).getTime(), now - 3 * 86_400_000);
});

test('context menu submits the selected messages manually with the chosen rule', async () => {
  const { fake, agent, exe } = await setup();
  await agent.onStartup();
  const a = fake.addMessage({ folderId: ARCHIVE, subject: 'Teljesen más tárgy' });
  const b = fake.addMessage({ folderId: ARCHIVE, subject: 'Még egy' });

  await fake.clickMenu(`${MENU_RULE_PREFIX}eloreendeles`, [a, b]);

  assert.deepEqual(exe.submissions().map((s) => [s.body.source, s.body.ruleId]), [['manual', 'eloreendeles'], ['manual', 'eloreendeles']]);
  assert.deepEqual(fake.tagNamesOf(a), ['AI: sorban']);
  await fake.clickMenu(MENU_ROOT, [a]);
  assert.equal(exe.submissions().length, 2, 'the parent item does nothing');
});

test('managed storage (policy) overrides the token saved in the options', async () => {
  const { fake, agent, exe } = await setup({ messengerOptions: { managed: { serverUrl: 'http://127.0.0.1:47811/', token: 'teszt-token' } }, options: { token: 'regi' } });
  await agent.onStartup();
  assert.ok(exe.state.requests.length > 0);
  assert.ok(exe.state.requests.every((r) => r.headers['x-efmailagent-token'] === 'teszt-token'));
});

test('options page requests are answered by the background', async () => {
  const { fake, agent } = await setup();
  await agent.onStartup();
  const status = await fake.messenger.runtime.sendMessage({ type: 'status' });
  assert.equal(status.outbox, 0);
  assert.equal(status.matchers.rulesVersion, 'v1');
});

test('new mail listener monitors all folders, so filtered mail moved out of the inbox is still seen', async () => {
  const { fake } = await setup();
  const [registration] = fake.callsOf('messages.onNewMailReceived.addListener');
  assert.deepEqual(registration.args, [true]);
});
