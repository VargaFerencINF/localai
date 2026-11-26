import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { repoPath } from './helpers.js';

const manifest = JSON.parse(readFileSync(repoPath('addon', 'manifest.json'), 'utf8'));

test('manifest identity and version', () => {
  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.browser_specific_settings.gecko.id, 'efmailagent@energofish.hu');
  assert.equal(manifest.browser_specific_settings.gecko.strict_min_version, '128.0');
  assert.equal(manifest.version, readFileSync(repoPath('VERSION'), 'utf8').trim(), 'manifest version = VERSION');
});

test('only the permissions the add-on needs', () => {
  assert.deepEqual([...manifest.permissions].sort(), ['accountsRead', 'alarms', 'menus', 'messagesRead', 'messagesTags', 'messagesTagsList', 'messagesUpdate', 'storage']);
  assert.deepEqual(manifest.host_permissions, ['http://127.0.0.1/*']);
});

test('every file referenced by the manifest exists', () => {
  const files = [...manifest.background.scripts, manifest.options_ui.page, ...Object.values(manifest.icons)];
  for (const file of files) assert.ok(existsSync(repoPath('addon', file)), file);
  assert.equal(manifest.background.type, 'module');
});

test('UI strings are Hungarian', () => {
  const html = readFileSync(repoPath('addon', 'options', 'options.html'), 'utf8');
  for (const text of ['Kapcsolat tesztelése', 'Figyelt mappák', 'Visszamenőleges keresés', 'Utolsó beküldések']) assert.ok(html.includes(text), text);
  assert.match(html, /<html lang="hu">/);
});
