/**
 * Downloads ModernZ OSC files (lua + font) from GitHub.
 * Places them into both build/mpv/portable_config/ and resources/mpv/portable_config/
 *
 * Usage: node scripts/download-modernz.js
 */

const https = require('https');
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const destinations = [
  path.join(ROOT, 'build', 'mpv', 'portable_config'),
  path.join(ROOT, 'resources', 'mpv', 'portable_config'),
];

const remoteFiles = [
  {
    url: 'https://raw.githubusercontent.com/Samillion/ModernZ/main/modernz.lua',
    subpath: path.join('scripts', 'modernz.lua'),
  },
  {
    url: 'https://raw.githubusercontent.com/Samillion/ModernZ/main/modernz-osc-icons.ttf',
    subpath: path.join('fonts', 'modernz-osc-icons.ttf'),
  },
];

function download(url, dest) {
  return new Promise((resolve, reject) => {
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    const file = fs.createWriteStream(dest);
    const get = (u) => {
      https.get(u, { headers: { 'User-Agent': 'nocturne' } }, (res) => {
        if (res.statusCode === 301 || res.statusCode === 302) {
          file.close();
          fs.unlinkSync(dest);
          download(res.headers.location, dest).then(resolve).catch(reject);
          return;
        }
        if (res.statusCode !== 200) {
          file.close();
          fs.unlinkSync(dest);
          reject(new Error(`HTTP ${res.statusCode} for ${u}`));
          return;
        }
        res.pipe(file);
        file.on('finish', () => { file.close(); resolve(); });
      }).on('error', (e) => {
        file.close();
        try { fs.unlinkSync(dest); } catch {}
        reject(e);
      });
    };
    get(url);
  });
}

async function main() {
  for (const f of remoteFiles) {
    // Download to a temp buffer first, then write to all destinations
    console.log('Downloading', path.basename(f.subpath), '...');
    const tmpDest = path.join(destinations[0], f.subpath);
    await download(f.url, tmpDest);

    // Copy to other destinations
    for (let i = 1; i < destinations.length; i++) {
      const dest = path.join(destinations[i], f.subpath);
      fs.mkdirSync(path.dirname(dest), { recursive: true });
      fs.copyFileSync(tmpDest, dest);
    }

    console.log('  saved to', destinations.length, 'locations');
  }
  console.log('ModernZ downloaded successfully.');
}

main().catch((err) => {
  console.error('Error:', err.message);
  process.exit(1);
});
