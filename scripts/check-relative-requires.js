#!/usr/bin/env node
/**
 * Static guard: ban `require('./foo')` (or any relative-path require) in
 * src/main/**, src/preload/**, src/renderer/**, src/shared/**.
 *
 * Background — two v3 ship-stopper regressions (image-fallback +
 * analytics) shipped because electron-vite does not statically trace
 * dynamic require() calls. The referenced files were excluded from
 * out/main/index.js; the IPC handlers that depended on them threw
 * "Cannot find module" at runtime and the renderer silently rendered
 * empty.
 *
 * This script is wired into the `prebuild` hook so it runs before every
 * build, and into `lint` so it runs in CI. Regex-based — fast, no
 * @typescript-eslint dependency, good enough.
 *
 * To intentionally allow a single instance (real circular-import workaround
 * etc.), append a line comment ending in
 *   // eslint-allow-relative-require: <reason>
 * and the scanner will skip that line.
 */

'use strict';

const { readdirSync, readFileSync, statSync } = require('node:fs');
const { join, relative } = require('node:path');

const ROOT = join(__dirname, '..');
const SCAN_DIRS = ['src/main', 'src/preload', 'src/renderer', 'src/shared'];
const EXTS = new Set(['.ts', '.tsx', '.js', '.mjs', '.cjs']);

const REL_REQUIRE = /\brequire\s*\(\s*['"](\.{1,2}\/[^'"]+)['"]\s*\)/g;
const ALLOW_MARKER = 'eslint-allow-relative-require:';

/** Strip a single-line `//`-comment tail (outside string literals) so a
 *  require() call mentioned inside a comment doesn't false-positive. */
function stripLineComment(line) {
  let inSingle = false;
  let inDouble = false;
  let inBacktick = false;
  for (let i = 0; i < line.length - 1; i++) {
    const c = line[i];
    if (c === '\\') { i++; continue; }
    if (!inDouble && !inBacktick && c === '\'') inSingle = !inSingle;
    else if (!inSingle && !inBacktick && c === '"') inDouble = !inDouble;
    else if (!inSingle && !inDouble && c === '`') inBacktick = !inBacktick;
    else if (!inSingle && !inDouble && !inBacktick && c === '/' && line[i + 1] === '/') {
      return line.slice(0, i);
    }
  }
  return line;
}

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const s = statSync(full);
    if (s.isDirectory()) {
      yield* walk(full);
    } else if (s.isFile()) {
      const dot = entry.lastIndexOf('.');
      if (dot >= 0 && EXTS.has(entry.slice(dot))) yield full;
    }
  }
}

const violations = [];
for (const dir of SCAN_DIRS) {
  const abs = join(ROOT, dir);
  try {
    for (const file of walk(abs)) {
      const src = readFileSync(file, 'utf8');
      const lines = src.split('\n');
      let inBlockComment = false;
      lines.forEach((rawLine, i) => {
        // Handle multi-line `/* … */` comments: strip the comment-only span.
        let line = rawLine;
        if (inBlockComment) {
          const end = line.indexOf('*/');
          if (end === -1) return; // entire line inside block comment
          line = line.slice(end + 2);
          inBlockComment = false;
        }
        // Strip any trailing block comment that starts on this line.
        for (;;) {
          const start = line.indexOf('/*');
          if (start === -1) break;
          const rest = line.slice(start + 2);
          const end = rest.indexOf('*/');
          if (end === -1) {
            line = line.slice(0, start);
            inBlockComment = true;
            break;
          }
          line = line.slice(0, start) + rest.slice(end + 2);
        }
        line = stripLineComment(line);

        REL_REQUIRE.lastIndex = 0;
        let m;
        while ((m = REL_REQUIRE.exec(line))) {
          if (rawLine.includes(ALLOW_MARKER)) continue;
          violations.push({
            file: relative(ROOT, file),
            line: i + 1,
            specifier: m[1],
            snippet: rawLine.trim(),
          });
        }
      });
    }
  } catch (err) {
    if (err.code !== 'ENOENT') throw err;
  }
}

if (violations.length === 0) {
  console.log('[check-relative-requires] OK — no dynamic relative require()s in src/.');
  process.exit(0);
}

console.error('[check-relative-requires] FAIL — found dynamic relative require() call(s):');
console.error('');
for (const v of violations) {
  console.error(`  ${v.file}:${v.line}  require('${v.specifier}')`);
  console.error(`    ${v.snippet}`);
}
console.error('');
console.error('Use a static `import` instead. electron-vite does not bundle dynamic require()');
console.error('targets; the module gets dropped from out/main/index.js and fails at runtime.');
console.error('');
console.error('If a real circular-dependency forces dynamic resolution, append a line comment:');
console.error("  // eslint-allow-relative-require: <one-sentence reason>");
process.exit(1);
