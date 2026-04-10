#!/usr/bin/env node
/**
 * Copy mpv from system installation into resources/mpv/ for bundling.
 * Usage: node scripts/download-mpv.js
 *
 * On Windows, looks for mpv in common locations:
 *   1. System PATH
 *   2. C:\Program Files\mpv\mpv.exe
 *   3. C:\mpv\mpv.exe
 *   4. %LOCALAPPDATA%\Programs\mpv\mpv.exe
 */

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const DEST = path.join(__dirname, '..', 'resources', 'mpv');

const SEARCH_PATHS = [
  'C:\\Program Files\\mpv\\mpv.exe',
  'C:\\Program Files (x86)\\mpv\\mpv.exe',
  'C:\\mpv\\mpv.exe',
  path.join(process.env.LOCALAPPDATA || '', 'Programs', 'mpv', 'mpv.exe'),
  path.join(process.env.USERPROFILE || '', 'scoop', 'apps', 'mpv', 'current', 'mpv.exe'),
];

function findMpv() {
  // Try `where mpv` on Windows
  try {
    const result = execSync('where mpv', { encoding: 'utf-8' }).trim().split('\n')[0].trim();
    if (fs.existsSync(result)) return path.dirname(result);
  } catch { /* not in PATH */ }

  // Check common locations
  for (const p of SEARCH_PATHS) {
    if (fs.existsSync(p)) return path.dirname(p);
  }

  return null;
}

function copyDir(src, dest) {
  fs.mkdirSync(dest, { recursive: true });
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    const srcPath = path.join(src, entry.name);
    const destPath = path.join(dest, entry.name);
    if (entry.isDirectory()) {
      copyDir(srcPath, destPath);
    } else {
      fs.copyFileSync(srcPath, destPath);
    }
  }
}

function main() {
  const mpvDir = findMpv();
  if (!mpvDir) {
    console.error('ERROR: mpv not found. Install mpv and ensure it is in your PATH.');
    console.error('Download from: https://mpv.io/installation/');
    process.exit(1);
  }

  console.log(`Found mpv at: ${mpvDir}`);

  // Create destination
  fs.mkdirSync(DEST, { recursive: true });

  // Copy mpv.exe and required DLLs
  const mpvExe = path.join(mpvDir, 'mpv.exe');
  if (!fs.existsSync(mpvExe)) {
    console.error(`ERROR: mpv.exe not found in ${mpvDir}`);
    process.exit(1);
  }

  fs.copyFileSync(mpvExe, path.join(DEST, 'mpv.exe'));
  console.log('Copied mpv.exe');

  // Copy all DLLs alongside mpv.exe (needed for portable builds)
  let dllCount = 0;
  for (const file of fs.readdirSync(mpvDir)) {
    if (file.toLowerCase().endsWith('.dll')) {
      fs.copyFileSync(path.join(mpvDir, file), path.join(DEST, file));
      dllCount++;
    }
  }
  console.log(`Copied ${dllCount} DLL files`);

  // Copy mpv.conf if present
  const conf = path.join(mpvDir, 'mpv.conf');
  if (fs.existsSync(conf)) {
    fs.copyFileSync(conf, path.join(DEST, 'mpv.conf'));
    console.log('Copied mpv.conf');
  }

  console.log(`\nmpv files copied to: ${DEST}`);
  console.log('Ready for electron-builder packaging.');
}

main();
