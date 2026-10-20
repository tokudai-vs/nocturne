const { execSync } = require('child_process');
const path = require('path');
const fs = require('fs');

const root = path.join(__dirname, '..');
const makensis = '/home/tokudai/.cache/electron-builder/nsis/nsis-3.0.4.1-nsis-3.0.4.1/linux/makensis';

console.log('[installer] Publishing self-contained release binary...');
execSync(
  'export PATH="/home/tokudai/.dotnet:$PATH" && dotnet publish src-native/Nocturne.App/Nocturne.App.csproj -r win-x64 -c Release -o bin/native-dist/ --self-contained true -p:PublishTrimmed=false',
  { cwd: root, stdio: 'inherit', shell: '/bin/bash' }
);

console.log('[installer] Mirroring mpv into native-dist...');
const mpvSrc = path.join(root, 'resources', 'mpv');
const mpvDest = path.join(root, 'bin', 'native-dist', 'resources', 'mpv');
if (fs.existsSync(mpvSrc)) {
  fs.mkdirSync(mpvDest, { recursive: true });
  fs.cpSync(mpvSrc, mpvDest, { recursive: true });
}

console.log('[installer] Ensuring dist/ directory exists...');
const distDir = path.join(root, 'dist');
fs.mkdirSync(distDir, { recursive: true });

console.log('[installer] Compiling NSIS setup installer...');
const nsisDir = '/home/tokudai/.cache/electron-builder/nsis/nsis-3.0.4.1-nsis-3.0.4.1';
execSync(`${makensis} scripts/installer.nsi`, {
  cwd: root,
  stdio: 'inherit',
  env: { ...process.env, NSISDIR: nsisDir }
});

console.log('\n[installer] SUCCESS! Installer generated at dist/Nocturne-v4.1.0-Setup.exe');
