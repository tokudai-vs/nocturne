#!/usr/bin/env node
/**
 * Mirror tracked mpv portable_config files from build/ to resources/.
 * Source of truth lives under build/mpv/portable_config/; resources/mpv/ is
 * regenerated for runtime/packaging use (it is gitignored).
 *
 * download-modernz.js already writes modernz.lua and the icon font to both
 * locations, so this script only needs to cover the files we own ourselves.
 */

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const SRC = path.join(ROOT, 'build', 'mpv', 'portable_config');
const DEST = path.join(ROOT, 'resources', 'mpv', 'portable_config');

const FILES = [
  'mpv.conf',
  'input.conf',
  'script-opts/modernz.conf',
  'scripts/nocturne_select.lua',
  'scripts/nocturne_nav.lua',
  'scripts/open-subtitles.lua',
];

let copied = 0;
let missing = 0;
for (const rel of FILES) {
  const src = path.join(SRC, rel);
  const dest = path.join(DEST, rel);
  if (!fs.existsSync(src)) {
    console.warn(`  miss: ${rel} (not found in build/)`);
    missing++;
    continue;
  }
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.copyFileSync(src, dest);
  console.log(`  copy: ${rel}`);
  copied++;
}

console.log(`\nMirrored ${copied}/${FILES.length} file(s) to resources/mpv/portable_config/.`);
if (missing > 0) {
  console.warn(`${missing} file(s) missing from build/. Run "npm run setup" if this is a fresh clone.`);
}
