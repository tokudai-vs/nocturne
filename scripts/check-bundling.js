#!/usr/bin/env node
/**
 * Postbuild guard: every src/main/*.ts module that exports something
 * non-trivial MUST appear in the bundled out/main/index.js, otherwise it
 * was silently dropped by electron-vite's static-analysis pass (the
 * v3 image-fallback + analytics regressions both shipped this way).
 *
 * The check is approximate but cheap: for each src/main/*.ts, we parse out
 * `export function foo`, `export const foo`, `export class Foo`, and
 * `export { ... }` names, then grep for at least one of them in
 * out/main/index.js. Files that only export types (interface/type) are
 * skipped — those compile away.
 *
 * To intentionally exclude a file from this check, add its repo-relative
 * path to BUNDLE_CHECK_SKIP below with a comment explaining why.
 */

'use strict';

const { readdirSync, readFileSync, existsSync, statSync } = require('node:fs');
const { join, relative } = require('node:path');

const ROOT = join(__dirname, '..');
const MAIN_DIR = join(ROOT, 'src', 'main');
const BUNDLE_PATH = join(ROOT, 'out', 'main', 'index.js');

const BUNDLE_CHECK_SKIP = new Set([
  // Type-only or test-only modules go here. Empty by default.
]);

if (!existsSync(BUNDLE_PATH)) {
  console.error('[check-bundling] FAIL — out/main/index.js not found. Did `electron-vite build` run?');
  process.exit(1);
}

const bundleSource = readFileSync(BUNDLE_PATH, 'utf8');
const bundleSize = Buffer.byteLength(bundleSource, 'utf8');

function* listTsFiles(dir) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const s = statSync(full);
    if (s.isDirectory()) {
      yield* listTsFiles(full);
    } else if (s.isFile() && entry.endsWith('.ts') && !entry.endsWith('.d.ts')) {
      yield full;
    }
  }
}

const EXPORT_FN = /^\s*export\s+(?:async\s+)?function\s+([A-Za-z_$][\w$]*)/gm;
const EXPORT_CONST = /^\s*export\s+(?:const|let|var)\s+([A-Za-z_$][\w$]*)/gm;
const EXPORT_CLASS = /^\s*export\s+(?:abstract\s+)?class\s+([A-Za-z_$][\w$]*)/gm;
// `export { a, b as c }` — capture each item, ignore the alias.
const EXPORT_LIST = /^\s*export\s*\{\s*([^}]+)\s*\}/gm;

function collectExports(source) {
  const names = new Set();
  for (const m of source.matchAll(EXPORT_FN)) names.add(m[1]);
  for (const m of source.matchAll(EXPORT_CONST)) names.add(m[1]);
  for (const m of source.matchAll(EXPORT_CLASS)) names.add(m[1]);
  for (const m of source.matchAll(EXPORT_LIST)) {
    for (const part of m[1].split(',')) {
      const head = part.trim().split(/\s+/)[0];
      // Skip pure type re-exports — they have no runtime presence.
      if (!head || head === 'type') continue;
      names.add(head);
    }
  }
  return names;
}

function escapeRegExp(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

const failures = [];
const checked = [];

for (const file of listTsFiles(MAIN_DIR)) {
  const rel = relative(ROOT, file).replace(/\\/g, '/');
  if (BUNDLE_CHECK_SKIP.has(rel)) continue;

  const source = readFileSync(file, 'utf8');
  const exports = collectExports(source);
  if (exports.size === 0) continue; // type-only or no exports

  // Hit if ANY exported symbol appears in the bundle. A miss on ALL of them
  // means the module is unreferenced or was silently dropped.
  let hit = false;
  for (const name of exports) {
    const re = new RegExp(`\\b${escapeRegExp(name)}\\b`);
    if (re.test(bundleSource)) { hit = true; break; }
  }

  checked.push({ rel, exportCount: exports.size, hit });
  if (!hit) failures.push({ rel, exports: Array.from(exports) });
}

if (failures.length === 0) {
  console.log(`[check-bundling] OK — ${checked.length} src/main/*.ts modules accounted for in out/main/index.js (${(bundleSize / 1024).toFixed(0)} KB).`);
  process.exit(0);
}

console.error(`[check-bundling] FAIL — ${failures.length} src/main module(s) NOT present in out/main/index.js:`);
console.error('');
for (const f of failures) {
  console.error(`  ${f.rel}`);
  console.error(`    Exports checked: ${f.exports.slice(0, 5).join(', ')}${f.exports.length > 5 ? ', …' : ''}`);
  console.error('    Likely cause: no static `import` from anywhere reachable by main entry.');
  console.error("    Fix: add `import { … } from './" + f.rel.replace(/^src\/main\//, '').replace(/\.ts$/, '') + "'` to the consuming file.");
  console.error('');
}
console.error('See AGENTS.md "Bundling traps" for the long form of this failure mode.');
process.exit(1);
