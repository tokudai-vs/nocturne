const fs = require('fs');
const path = require('path');

const src = path.join(__dirname, '..', 'resources', 'mpv');
const dest = path.join(__dirname, '..', 'bin', 'native-win', 'resources', 'mpv');

if (fs.existsSync(src)) {
  fs.mkdirSync(dest, { recursive: true });
  fs.cpSync(src, dest, { recursive: true });
  console.log('[native] Successfully mirrored resources/mpv into bin/native-win/resources/mpv');
} else {
  console.warn('[native] Warning: resources/mpv does not exist.');
}
