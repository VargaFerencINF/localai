// Orchestration of the add-on: new mail, matchers, submissions, outbox, job polling, tags, backfill, context menu.
// Everything Thunderbird-specific comes in through the injected `messenger` object, so the same code runs against
// the real API (src/background.js) and against test/fake-messenger.js in Node. The MV3 background is an event page
// that can be unloaded at any time, so all state lives in storage.local, never in variables.

import { createApi, ApiError } from './api.js';
import { backoffMs } from './backoff.js';
import { loadConfig } from './config.js';
import { htmlToText } from './htmlToText.js';
import { compileMatcher, findBestMatch } from './normalize.js';
import { buildPayload, canonicalMessageId, collectInlineTextParts, pickBodies } from './payload.js';
import { TAGS, hasAiTag, isTerminal, replaceAiTag, tagForStatus } from './tags.js';

export const ALARM_POLL = 'efmailagent-poll';
export const ALARM_MATCHERS = 'efmailagent-matchers';
export const MENU_ROOT = 'efmailagent-submit';
export const MENU_RULE_PREFIX = 'efmailagent-rule:';
export const OUTBOX_LIMIT = 500;
export const HISTORY_LIMIT = 20;
export const PENDING_LIMIT = 500;

const DAY_MS = 24 * 60 * 60 * 1000;

export function createAgent({ messenger, fetch, log = console, now = () => Date.now() }) {
  // Serializes storage read-modify-write sequences within this page instance.
  let chain = Promise.resolve();
  const exclusive = (fn) => {
    const run = chain.then(fn, fn);
    chain = run.catch(() => {});
    return run;
  };

  const info = (...args) => log.info?.('[EfMailAgent]', ...args) ?? log.log?.('[EfMailAgent]', ...args);
  const warn = (...args) => log.warn?.('[EfMailAgent]', ...args);

  async function getState(key, fallback) {
    const result = await messenger.storage.local.get(key);
    return result[key] ?? fallback;
  }

  async function setState(key, value) {
    await messenger.storage.local.set({ [key]: value });
  }

  async function api() {
    return createApi({ fetch, config: await loadConfig(messenger) });
  }

  async function* iterateList(list) {
    let page = list;
    while (page) {
      for (const message of page.messages ?? []) yield message;
      page = page.id ? await messenger.messages.continueList(page.id) : null;
    }
  }

  // ---------- tags ----------

  /** Creates missing AI tags (distinct colors) and returns { name: key }. Reuses a user tag with the same label. */
  async function ensureTags() {
    const existing = await messenger.messages.tags.list();
    const keys = {};
    for (const [name, def] of Object.entries(TAGS)) {
      const found = existing.find((t) => t.key === def.key) ?? existing.find((t) => t.tag === def.tag);
      if (found) {
        keys[name] = found.key;
      } else {
        keys[name] = (await messenger.messages.tags.create(def.key, def.tag, def.color)) ?? def.key;
      }
    }
    await setState('tagKeys', keys);
    return keys;
  }

  async function tagKeys() {
    return (await getState('tagKeys', null)) ?? (await ensureTags());
  }

  async function setStatusTag(tbMessageId, status) {
    const keys = await tagKeys();
    const header = await messenger.messages.get(tbMessageId);
    if (!header) return;
    const tags = replaceAiTag(header.tags, Object.values(keys), keys[tagForStatus(status)]);
    await messenger.messages.update(tbMessageId, { tags });
  }

  // ---------- matchers & menus ----------

  async function refreshMatchers({ force = false } = {}) {
    const cached = await getState('matchers', null);
    const client = await api();
    const result = await client.matchers(force ? null : cached?.etag);
    if (result.notModified) return { changed: false, matchers: cached };
    const stored = { etag: result.etag, rulesVersion: result.rulesVersion, matchers: result.matchers ?? [], fetchedAt: now() };
    await setState('matchers', stored);
    if (cached?.rulesVersion !== stored.rulesVersion)
      info(`Szabályok frissítve (verzió ${stored.rulesVersion}, ${stored.matchers.length} szabály).`);
    await rebuildMenus(stored.matchers);
    await processPendingMatch();
    return { changed: true, matchers: stored };
  }

  async function rebuildMenus(matchers) {
    await messenger.menus.removeAll();
    messenger.menus.create({ id: MENU_ROOT, title: 'Küldés AI feldolgozásra', contexts: ['message_list'] });
    if (!matchers?.length) {
      messenger.menus.create({ id: `${MENU_RULE_PREFIX}`, parentId: MENU_ROOT, title: '(nincs elérhető szabály)', enabled: false, contexts: ['message_list'] });
      return;
    }
    for (const matcher of matchers) {
      messenger.menus.create({ id: `${MENU_RULE_PREFIX}${matcher.ruleId}`, parentId: MENU_ROOT, title: matcher.name || matcher.ruleId, contexts: ['message_list'] });
    }
  }

  // ---------- submission ----------

  async function extractBodies(tbMessageId) {
    let parts;
    if (typeof messenger.messages.listInlineTextParts === 'function') {
      parts = await messenger.messages.listInlineTextParts(tbMessageId);
    } else {
      parts = collectInlineTextParts(await messenger.messages.getFull(tbMessageId));
    }
    const { text, html } = pickBodies(parts);
    if (text !== null && text.trim()) return { bodyText: text, bodyHtml: null };
    if (!html) return { bodyText: text ?? '', bodyHtml: null };
    let converted;
    if (typeof messenger.messengerUtilities?.convertToPlainText === 'function') {
      converted = await messenger.messengerUtilities.convertToPlainText(html);
    } else {
      converted = htmlToText(html);
    }
    return { bodyText: converted, bodyHtml: html };
  }

  async function attachmentNames(tbMessageId) {
    try {
      const attachments = await messenger.messages.listAttachments(tbMessageId);
      return attachments.map((a) => a.name).filter(Boolean);
    } catch {
      return [];
    }
  }

  async function createPayload(header, folder, source, ruleId) {
    const bodies = await extractBodies(header.id);
    return buildPayload({ header, folder: folder ?? header.folder, ...bodies, attachmentNames: await attachmentNames(header.id), source, ruleId });
  }

  async function addHistory(entry) {
    const history = await getState('history', []);
    const index = history.findIndex((h) => h.jobId && h.jobId === entry.jobId);
    if (index >= 0) history.splice(index, 1);
    history.unshift(entry);
    await setState('history', history.slice(0, HISTORY_LIMIT));
  }

  async function updateHistoryStatus(jobId, status) {
    const history = await getState('history', []);
    const entry = history.find((h) => h.jobId === jobId);
    if (entry) {
      entry.status = status;
      await setState('history', history);
    }
  }

  /** After 200/202: tag the message (only now), remember open jobs for polling. */
  async function accepted(tbMessageId, payload, response) {
    await setStatusTag(tbMessageId, response.status);
    if (!isTerminal(response.status)) {
      const open = await getState('openJobs', {});
      open[response.jobId] = { tbMessageId, messageId: payload.messageId, subject: payload.subject, since: now() };
      await setState('openJobs', open);
    }
    await addHistory({ at: now(), jobId: response.jobId, ruleId: response.ruleId, status: response.status, subject: payload.subject, duplicate: Boolean(response.duplicate) });
    info(`Beküldve: ${payload.subject} -> feladat ${response.jobId} (${response.status}${response.duplicate ? ', már ismert' : ''}).`);
  }

  async function enqueueOutbox(tbMessageId, payload, error) {
    const outbox = await getState('outbox', []);
    if (outbox.some((e) => e.payload.messageId === payload.messageId)) return 'queued';
    if (outbox.length >= OUTBOX_LIMIT) {
      warn(`A kimenő sor megtelt (${OUTBOX_LIMIT} levél), ez a levél most nem kerül be: ${payload.subject}. A visszamenőleges keresés később pótolja.`);
      return 'outbox-full';
    }
    outbox.push({ tbMessageId, payload, attempts: 1, addedAt: now(), nextAttemptAt: now() + backoffMs(1), lastError: error.message });
    await setState('outbox', outbox);
    warn(`Az EfMailAgent nem érhető el, a levél a kimenő sorba került: ${payload.subject} (${error.message})`);
    return 'queued';
  }

  /** Submits one payload. Returns 'accepted' | 'no-match' | 'rejected' | 'queued' | 'outbox-full'. */
  async function submitPayload(tbMessageId, payload) {
    const client = await api();
    try {
      const response = await client.submit(payload);
      await accepted(tbMessageId, payload, response);
      return 'accepted';
    } catch (error) {
      if (error instanceof ApiError && error.kind === 'rejected') {
        if (error.status === 422) {
          info(`Nem illeszkedik egyik szabályra sem (az exe döntése): ${payload.subject}`);
          return 'no-match';
        }
        warn(`Az EfMailAgent elutasította a levelet (HTTP ${error.status}): ${payload.subject}: ${error.message}`);
        return 'rejected';
      }
      return enqueueOutbox(tbMessageId, payload, error);
    }
  }

  async function submitMessage(header, folder, { source, ruleId } = {}) {
    const payload = await createPayload(header, folder, source ?? 'auto', ruleId);
    if (!payload.messageId) {
      warn(`A levélnek nincs Message-ID fejléce, nem küldhető be: ${header.subject}`);
      return 'rejected';
    }
    return submitPayload(header.id, payload);
  }

  // ---------- matching ----------

  async function compiledMatchers() {
    const cached = await getState('matchers', null);
    return cached ? cached.matchers.map(compileMatcher) : null;
  }

  async function isWatched(folder) {
    if (!folder) return false;
    if ((folder.specialUse ?? []).includes('inbox')) return true;
    const config = await loadConfig(messenger);
    return config.watchedFolderIds.includes(folder.id);
  }

  /** New mail arrived before matchers were ever downloaded: remember it until they are. */
  async function rememberPending(header) {
    const pending = await getState('pendingMatch', []);
    if (pending.length >= PENDING_LIMIT || pending.includes(header.id)) return;
    pending.push(header.id);
    await setState('pendingMatch', pending);
  }

  async function processPendingMatch() {
    const pending = await getState('pendingMatch', []);
    if (!pending.length) return;
    await setState('pendingMatch', []);
    const matchers = await compiledMatchers();
    for (const id of pending) {
      const header = await messenger.messages.get(id).catch(() => null);
      if (header && findBestMatch(header.subject, matchers)) await submitMessage(header, header.folder, { source: 'auto' });
    }
  }

  async function handleCandidate(header, folder) {
    const matchers = await compiledMatchers();
    if (!matchers) {
      await rememberPending(header);
      return 'pending';
    }
    if (!findBestMatch(header.subject, matchers)) return 'no-match';
    return submitMessage(header, folder, { source: 'auto' });
  }

  // ---------- public handlers ----------

  async function onNewMail(folder, messageList) {
    return exclusive(async () => {
      const config = await loadConfig(messenger);
      if (!config.enabled || !(await isWatched(folder))) return [];
      const results = [];
      for await (const header of iterateList(messageList)) results.push(await handleCandidate(header, folder));
      return results;
    });
  }

  async function flushOutbox() {
    const outbox = await getState('outbox', []);
    if (!outbox.length) return;
    const remaining = [];
    let client = await api();
    let unreachable = false;
    for (const entry of outbox) {
      if (unreachable || entry.nextAttemptAt > now()) {
        remaining.push(entry);
        continue;
      }
      try {
        const response = await client.submit(entry.payload);
        await accepted(entry.tbMessageId, entry.payload, response);
      } catch (error) {
        if (error instanceof ApiError && error.kind === 'rejected') {
          warn(`Kimenő sor: az EfMailAgent nem fogadta el (HTTP ${error.status}): ${entry.payload.subject}`);
          continue; // 400/422: a permanent answer, retrying would not help
        }
        entry.attempts += 1;
        entry.nextAttemptAt = now() + backoffMs(entry.attempts);
        entry.lastError = error.message;
        remaining.push(entry);
        unreachable = error.kind === 'unreachable';
        client = await api();
      }
    }
    await setState('outbox', remaining);
  }

  async function pollJobs() {
    const open = await getState('openJobs', {});
    const ids = Object.keys(open);
    if (!ids.length) return;
    const client = await api();
    for (const id of ids) {
      let job;
      try {
        job = await client.job(id);
      } catch {
        return; // exe unreachable: try again on the next alarm
      }
      if (job === null) {
        delete open[id];
        continue;
      }
      if (isTerminal(job.status)) {
        await setStatusTag(open[id].tbMessageId, job.status).catch((e) => warn(`Címke nem állítható (${id}): ${e.message}`));
        await updateHistoryStatus(id, job.status);
        info(`Feladat ${id} befejeződött: ${job.status}.`);
        delete open[id];
      }
    }
    await setState('openJobs', open);
  }

  /** Cheap check on every poll: refresh matchers when the exe's rulesVersion changed. */
  async function checkRulesVersion() {
    const client = await api();
    const health = await client.health();
    const cached = await getState('matchers', null);
    if (!cached || cached.rulesVersion !== health.rulesVersion) await refreshMatchers();
  }

  async function onAlarm(alarm) {
    const name = typeof alarm === 'string' ? alarm : alarm?.name;
    return exclusive(async () => {
      if (name === ALARM_MATCHERS) {
        await refreshMatchers().catch((e) => warn(`Szabályok frissítése sikertelen: ${e.message}`));
      } else if (name === ALARM_POLL) {
        await checkRulesVersion().catch(() => {});
        await flushOutbox().catch((e) => warn(`Kimenő sor feldolgozása sikertelen: ${e.message}`));
        await pollJobs().catch((e) => warn(`Feladatok lekérdezése sikertelen: ${e.message}`));
      }
    });
  }

  async function watchedFolders() {
    const config = await loadConfig(messenger);
    const inboxes = (await messenger.folders.query({ specialUse: ['inbox'] })).filter((f) => !f.isUnified && !f.isVirtual);
    const extra = [];
    for (const id of config.watchedFolderIds) {
      if (inboxes.some((f) => f.id === id)) continue;
      const folder = await messenger.folders.get(id).catch(() => null);
      if (folder) extra.push(folder);
    }
    return [...inboxes, ...extra];
  }

  /** Startup scan of watched folders for the last N days: matching messages without any AI tag are submitted. */
  async function backfill() {
    const config = await loadConfig(messenger);
    if (!config.enabled || config.backfillDays <= 0) return { scanned: 0, submitted: 0 };
    const matchers = await compiledMatchers();
    if (!matchers) return { scanned: 0, submitted: 0 };
    const keys = Object.values(await tagKeys());
    const outbox = await getState('outbox', []);
    const fromDate = new Date(now() - config.backfillDays * DAY_MS);
    let scanned = 0;
    let submitted = 0;
    for (const folder of await watchedFolders()) {
      const list = await messenger.messages.query({ folderId: folder.id, fromDate });
      for await (const header of iterateList(list)) {
        scanned += 1;
        if (hasAiTag(header.tags, keys) || !findBestMatch(header.subject, matchers)) continue;
        if (outbox.some((e) => e.payload.messageId === canonicalMessageId(header.headerMessageId))) continue;
        await submitMessage(header, folder, { source: 'auto' });
        submitted += 1;
      }
    }
    if (submitted) info(`Visszamenőleges keresés: ${scanned} levél átnézve, ${submitted} beküldve.`);
    return { scanned, submitted };
  }

  async function onStartup() {
    return exclusive(async () => {
      messenger.alarms.create(ALARM_POLL, { periodInMinutes: 1 });
      messenger.alarms.create(ALARM_MATCHERS, { periodInMinutes: 15 });
      await ensureTags().catch((e) => warn(`A címkék nem hozhatók létre: ${e.message}`));
      const cached = await getState('matchers', null);
      await rebuildMenus(cached?.matchers ?? []).catch(() => {});
      try {
        await refreshMatchers();
      } catch (error) {
        warn(`A szabályok nem tölthetők le (a gyorsítótárazott szabályok maradnak): ${error.message}`);
      }
      await flushOutbox().catch(() => {});
      await backfill().catch((e) => warn(`Visszamenőleges keresés sikertelen: ${e.message}`));
    });
  }

  async function onMenuClicked(clickInfo) {
    const id = String(clickInfo?.menuItemId ?? '');
    if (!id.startsWith(MENU_RULE_PREFIX) || id === MENU_RULE_PREFIX) return [];
    const ruleId = id.slice(MENU_RULE_PREFIX.length);
    return exclusive(async () => {
      const results = [];
      for await (const header of iterateList(clickInfo.selectedMessages)) {
        results.push(await submitMessage(header, header.folder, { source: 'manual', ruleId }));
      }
      return results;
    });
  }

  /** Requests from the options page. */
  async function onRuntimeMessage(message) {
    switch (message?.type) {
      case 'refreshMatchers':
        return exclusive(() => refreshMatchers({ force: true }));
      case 'backfill':
        return exclusive(() => backfill());
      case 'status':
        return {
          outbox: (await getState('outbox', [])).length,
          openJobs: Object.keys(await getState('openJobs', {})).length,
          matchers: await getState('matchers', null),
        };
      default:
        return undefined;
    }
  }

  return { onStartup, onNewMail, onAlarm, onMenuClicked, onRuntimeMessage, backfill, flushOutbox, pollJobs, refreshMatchers, ensureTags };
}
