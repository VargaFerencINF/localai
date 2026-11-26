// Fake of exactly the subset of the Thunderbird MailExtension API that the add-on uses. Records every call so tests
// can assert on them. Behaviour follows the documented MV3 API (Thunderbird 128+): MessageList pagination with
// continueList, messages.tags.*, folders.query/get, storage.managed rejecting when no policy is set, etc.
import { readFileSync } from 'node:fs';

function createEvent(name, calls) {
  const listeners = [];
  return {
    addListener: (fn, ...extra) => {
      calls.push({ name: `${name}.addListener`, args: extra });
      listeners.push(fn);
    },
    removeListener: (fn) => listeners.splice(listeners.indexOf(fn), 1),
    hasListener: (fn) => listeners.includes(fn),
    async fire(...args) {
      const results = [];
      for (const fn of listeners) results.push(await fn(...args));
      return results;
    },
    get listenerCount() {
      return listeners.length;
    },
  };
}

const clone = (value) => (value === undefined ? undefined : structuredClone(value));

/** Minimal parser for the plain-text sample .eml files (headers + 8bit body, no MIME decoding). */
export function parseEml(text) {
  const normalized = text.replace(/\r\n/g, '\n');
  const split = normalized.indexOf('\n\n');
  const headerText = (split < 0 ? normalized : normalized.slice(0, split)).replace(/\n[ \t]+/g, ' ');
  const headers = {};
  for (const line of headerText.split('\n')) {
    const i = line.indexOf(':');
    if (i > 0) headers[line.slice(0, i).trim().toLowerCase()] = line.slice(i + 1).trim();
  }
  return { headers, body: split < 0 ? '' : normalized.slice(split + 2) };
}

export function createFakeMessenger({ managed = null, pageSize = 100, withConvertToPlainText = true, withInlineTextParts = true } = {}) {
  const calls = [];
  const record = (name, ...args) => calls.push({ name, args: clone(args) });
  const local = {};
  const messages = new Map();
  const folders = [];
  const accounts = [{ id: 'account1', name: 'eloreendeles@ertekesites.example', type: 'imap' }];
  const tags = [];
  const alarms = new Map();
  const menus = new Map();
  const lists = new Map();
  let nextMessageId = 1;
  let nextListId = 1;

  const events = {
    newMail: createEvent('messages.onNewMailReceived', calls),
    alarm: createEvent('alarms.onAlarm', calls),
    menuClicked: createEvent('menus.onClicked', calls),
    startup: createEvent('runtime.onStartup', calls),
    installed: createEvent('runtime.onInstalled', calls),
    runtimeMessage: createEvent('runtime.onMessage', calls),
    storageChanged: createEvent('storage.onChanged', calls),
  };

  function header(id) {
    const entry = messages.get(id);
    if (!entry) throw new Error(`Message not found: ${id}`);
    const result = clone(entry.header);
    result.date = new Date(entry.header.date);
    result.folder = clone(folderById(entry.folderId));
    return result;
  }

  function folderById(id) {
    return folders.find((f) => f.id === id);
  }

  /** Splits headers into pages like Thunderbird's MessageList ({ id, messages }). */
  function toList(ids) {
    const pages = [];
    for (let i = 0; i < ids.length; i += pageSize) pages.push(ids.slice(i, i + pageSize));
    if (!pages.length) pages.push([]);
    const listId = pages.length > 1 ? `list-${nextListId++}` : null;
    if (listId) lists.set(listId, pages.slice(1));
    return { id: listId, messages: pages[0].map(header) };
  }

  const messenger = {
    storage: {
      local: {
        async get(keys) {
          record('storage.local.get', keys);
          if (keys === null || keys === undefined) return clone(local);
          const list = Array.isArray(keys) ? keys : typeof keys === 'string' ? [keys] : Object.keys(keys);
          return Object.fromEntries(list.filter((k) => k in local).map((k) => [k, clone(local[k])]));
        },
        async set(items) {
          record('storage.local.set', Object.keys(items));
          for (const [k, v] of Object.entries(items)) local[k] = clone(v);
        },
        async remove(keys) {
          record('storage.local.remove', keys);
          for (const k of Array.isArray(keys) ? keys : [keys]) delete local[k];
        },
      },
      managed: {
        async get(keys) {
          record('storage.managed.get', keys);
          if (!managed) throw new Error('Managed storage manifest not found');
          const list = Array.isArray(keys) ? keys : [keys];
          return Object.fromEntries(list.filter((k) => k in managed).map((k) => [k, managed[k]]));
        },
      },
      onChanged: events.storageChanged,
    },
    messages: {
      onNewMailReceived: events.newMail,
      async get(id) {
        record('messages.get', id);
        return header(id);
      },
      async getFull(id) {
        record('messages.getFull', id);
        const entry = messages.get(id);
        const parts = [];
        if (entry.text !== null) parts.push({ contentType: 'text/plain', body: entry.text, partName: '1.1' });
        if (entry.html !== null) parts.push({ contentType: 'text/html', body: entry.html, partName: '1.2' });
        return {
          contentType: 'multipart/mixed',
          partName: '',
          headers: clone(entry.rawHeaders),
          parts: [
            { contentType: 'multipart/alternative', partName: '1', parts },
            ...entry.attachments.map((name, i) => ({ contentType: 'application/octet-stream', name, partName: `${i + 2}` })),
          ],
        };
      },
      async listInlineTextParts(id) {
        record('messages.listInlineTextParts', id);
        const entry = messages.get(id);
        const parts = [];
        if (entry.text !== null) parts.push({ contentType: 'text/plain', content: entry.text });
        if (entry.html !== null) parts.push({ contentType: 'text/html', content: entry.html });
        return parts;
      },
      async listAttachments(id) {
        record('messages.listAttachments', id);
        return messages.get(id).attachments.map((name, i) => ({ name, contentType: 'application/octet-stream', size: 100, partName: `${i + 2}` }));
      },
      async query(queryInfo = {}) {
        record('messages.query', queryInfo);
        const ids = [...messages.entries()]
          .filter(([, e]) => !queryInfo.folderId || [].concat(queryInfo.folderId).includes(e.folderId))
          .filter(([, e]) => !queryInfo.fromDate || new Date(e.header.date) >= queryInfo.fromDate)
          .filter(([, e]) => !queryInfo.toDate || new Date(e.header.date) <= queryInfo.toDate)
          .map(([id]) => id);
        return toList(ids);
      },
      async continueList(listId) {
        record('messages.continueList', listId);
        const pages = lists.get(listId);
        const page = pages.shift();
        if (!pages.length) lists.delete(listId);
        return { id: pages.length ? listId : null, messages: page.map(header) };
      },
      async update(id, properties) {
        record('messages.update', id, properties);
        const entry = messages.get(id);
        if (properties.tags) {
          for (const key of properties.tags) if (!tags.some((t) => t.key === key)) throw new Error(`Unknown tag: ${key}`);
          entry.header.tags = [...properties.tags];
        }
      },
      tags: {
        async list() {
          record('messages.tags.list');
          return clone(tags);
        },
        async create(key, tag, color) {
          record('messages.tags.create', key, tag, color);
          if (tags.some((t) => t.key === key)) throw new Error(`Tag exists: ${key}`);
          tags.push({ key, tag, color, ordinal: '' });
          return key;
        },
        async update(key, properties) {
          record('messages.tags.update', key, properties);
          Object.assign(tags.find((t) => t.key === key), properties);
        },
      },
    },
    messengerUtilities: withConvertToPlainText
      ? {
          async convertToPlainText(body) {
            record('messengerUtilities.convertToPlainText', body);
            return body.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
          },
        }
      : undefined,
    folders: {
      async query(queryInfo = {}) {
        record('folders.query', queryInfo);
        return clone(folders.filter((f) => !queryInfo.specialUse || queryInfo.specialUse.every((u) => (f.specialUse ?? []).includes(u))));
      },
      async get(id) {
        record('folders.get', id);
        const folder = folderById(id);
        if (!folder) throw new Error(`Folder not found: ${id}`);
        return clone(folder);
      },
    },
    accounts: {
      async list() {
        record('accounts.list');
        return clone(accounts);
      },
    },
    alarms: {
      create(name, info) {
        record('alarms.create', name, info);
        alarms.set(name, { name, ...info });
      },
      async clear(name) {
        record('alarms.clear', name);
        return alarms.delete(name);
      },
      async getAll() {
        return [...alarms.values()];
      },
      onAlarm: events.alarm,
    },
    menus: {
      create(properties) {
        record('menus.create', properties);
        if (properties.onclick) throw new Error('Event pages cannot use onclick');
        if (menus.has(properties.id)) throw new Error(`Duplicate menu id: ${properties.id}`);
        menus.set(properties.id, clone(properties));
        return properties.id;
      },
      async removeAll() {
        record('menus.removeAll');
        menus.clear();
      },
      onClicked: events.menuClicked,
    },
    runtime: {
      onStartup: events.startup,
      onInstalled: events.installed,
      onMessage: events.runtimeMessage,
      async sendMessage(message) {
        const results = await events.runtimeMessage.fire(message);
        return results.find((r) => r !== undefined);
      },
    },
    permissions: {
      async contains() {
        return true;
      },
      async request() {
        return true;
      },
    },
  };
  if (!withInlineTextParts) delete messenger.messages.listInlineTextParts;

  function addFolder(folder) {
    const full = { accountId: 'account1', isRoot: false, isVirtual: false, isUnified: false, isTag: false, specialUse: [], ...folder };
    folders.push(full);
    return full;
  }

  function addMessage({ folderId, subject, author = 'Kovács Péter <kovacs.peter@ertekesites.example>', recipients = ['eloreendeles@ertekesites.example'],
    date = new Date(), headerMessageId, text = 'szöveg', html = null, attachments = [], tags: messageTags = [], rawHeaders = {} }) {
    const id = nextMessageId++;
    messages.set(id, {
      folderId,
      header: { id, subject, author, recipients, date: new Date(date).toISOString(), headerMessageId: headerMessageId ?? `msg-${id}@test.example`, tags: [...messageTags], read: false },
      text,
      html,
      attachments,
      rawHeaders,
    });
    return id;
  }

  function addEml(path, folderId, overrides = {}) {
    const { headers, body } = parseEml(readFileSync(path, 'utf8'));
    return addMessage({
      folderId,
      subject: headers.subject,
      author: headers.from,
      recipients: (headers.to ?? '').split(',').map((s) => s.trim()).filter(Boolean),
      date: new Date(headers.date),
      headerMessageId: (headers['message-id'] ?? '').replace(/^<|>$/g, ''),
      text: body,
      rawHeaders: Object.fromEntries(Object.entries(headers).map(([k, v]) => [k, [v]])),
      ...overrides,
    });
  }

  return {
    messenger,
    calls,
    events,
    local,
    tags,
    alarms,
    menus,
    addFolder,
    addMessage,
    addEml,
    callsOf: (name) => calls.filter((c) => c.name === name),
    tagsOf: (id) => [...messages.get(id).header.tags],
    tagNamesOf: (id) => messages.get(id).header.tags.map((key) => tags.find((t) => t.key === key)?.tag ?? key),
    fireNewMail: (folderId, ids) => events.newMail.fire(clone(folderById(folderId)), toList(ids)),
    fireAlarm: (name) => events.alarm.fire({ name, scheduledTime: Date.now() }),
    clickMenu: (menuItemId, ids) => events.menuClicked.fire({ menuItemId, selectedMessages: toList(ids) }, { id: 1 }),
  };
}
