// Configuration: server URL + token from storage.managed (written by the exe via policies.json "3rdparty"),
// falling back to the options saved in storage.local.

export const DEFAULT_OPTIONS = Object.freeze({
  serverUrl: 'http://127.0.0.1:47811',
  token: '',
  watchedFolderIds: [],
  backfillDays: 3,
  enabled: true,
});

export const MANAGED_KEYS = Object.freeze(['serverUrl', 'token']);

export async function readManaged(messenger) {
  try {
    const managed = (await messenger.storage.managed.get([...MANAGED_KEYS])) ?? {};
    return Object.fromEntries(MANAGED_KEYS.filter((k) => typeof managed[k] === 'string' && managed[k].trim()).map((k) => [k, managed[k].trim()]));
  } catch {
    return {}; // no managed storage configured (e.g. add-on loaded by hand)
  }
}

/** Effective configuration; `managed` lists the keys that come from the policy and are read-only in the options page. */
export async function loadConfig(messenger) {
  const { options = {} } = await messenger.storage.local.get('options');
  const managed = await readManaged(messenger);
  const config = { ...DEFAULT_OPTIONS, ...options, ...managed, managed: Object.keys(managed) };
  config.serverUrl = String(config.serverUrl || DEFAULT_OPTIONS.serverUrl).replace(/\/+$/, '');
  config.backfillDays = Math.max(0, Math.min(60, Number.parseInt(config.backfillDays, 10) || 0));
  config.watchedFolderIds = Array.isArray(config.watchedFolderIds) ? config.watchedFolderIds : [];
  config.enabled = config.enabled !== false;
  return config;
}

export async function saveOptions(messenger, options) {
  const { options: current = {} } = await messenger.storage.local.get('options');
  await messenger.storage.local.set({ options: { ...current, ...options } });
}
