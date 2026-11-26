#!/usr/bin/env node
// Sets the product version everywhere it is stored: VERSION (exe assembly version via Directory.Build.props,
// installer via build.ps1) and addon/manifest.json. Usage: node scripts/set-version.mjs 1.0.0
import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const version = process.argv[2];
if (!/^\d+\.\d+\.\d+$/.test(version ?? '')) {
  console.error('Használat: node scripts/set-version.mjs <fő.al.javítás>, pl. 1.0.0');
  process.exit(1);
}
const root = resolve(import.meta.dirname, '..');
writeFileSync(join(root, 'VERSION'), `${version}\n`);
const manifestPath = join(root, 'addon', 'manifest.json');
const manifest = readFileSync(manifestPath, 'utf8');
writeFileSync(manifestPath, manifest.replace(/"version":\s*"[^"]*"/, `"version": "${version}"`));
console.log(`Verzió: ${version} (VERSION, addon/manifest.json)`);
