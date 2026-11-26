import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createAgent } from '../src/core/agent.js';
import { wireAgent } from '../src/core/wiring.js';
import { createFakeExe } from './fake-exe.js';
import { createFakeMessenger } from './fake-messenger.js';

export const REPO = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const repoPath = (...parts) => join(REPO, ...parts);
export const readJson = (...parts) => JSON.parse(readFileSync(repoPath(...parts), 'utf8'));

export const INBOX = 'account1://INBOX';
export const ORDERS = 'account1://INBOX/Rendelesek';
export const ARCHIVE = 'account1://Archives';

/** Fake Thunderbird + fake exe + agent, with an inbox, one extra folder and storage.local options pointing at the fake. */
export async function setup({ messengerOptions = {}, exeOptions = {}, options = {}, now } = {}) {
  const exe = createFakeExe(exeOptions);
  const fake = createFakeMessenger(messengerOptions);
  fake.addFolder({ id: INBOX, name: 'Inbox', path: '/INBOX', specialUse: ['inbox'] });
  fake.addFolder({ id: ORDERS, name: 'Rendelesek', path: '/INBOX/Rendelesek' });
  fake.addFolder({ id: ARCHIVE, name: 'Archives', path: '/Archives', specialUse: ['archives'] });
  await fake.messenger.storage.local.set({ options: { serverUrl: 'http://127.0.0.1:47811', token: exe.token, ...options } });
  const logs = [];
  const log = { info: (...a) => logs.push(['info', a.join(' ')]), warn: (...a) => logs.push(['warn', a.join(' ')]), log: () => {} };
  const agent = createAgent({ messenger: fake.messenger, fetch: exe.fetch, log, now });
  wireAgent(fake.messenger, agent);
  return { exe, fake, agent, logs };
}

export const DEMO_EML = repoPath('samples', 'emails', 'demo_eloreendeles_01.eml');
export const NON_MATCHING_EML = repoPath('samples', 'emails', 'demo_nem_illeszkedo.eml');
