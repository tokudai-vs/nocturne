const { execSync } = require('child_process');

console.log('=== Nocturne Development Setup ===\n');

// Check Node version
const nodeVersion = process.versions.node.split('.')[0];
if (parseInt(nodeVersion) < 20) {
  console.error('ERROR: Node.js 20+ required. Current:', process.version);
  process.exit(1);
}
console.log('✓ Node.js', process.version);

// Install dependencies
console.log('\nInstalling dependencies...');
execSync('npm install', { stdio: 'inherit' });

// Download mpv
console.log('\nSetting up mpv...');
try {
  execSync('node scripts/download-mpv.js', { stdio: 'inherit' });
} catch {
  console.warn('⚠ mpv setup failed. Install mpv manually: winget install shinchiro.mpv');
}

// Download ModernZ
console.log('\nDownloading ModernZ OSC...');
try {
  execSync('node scripts/download-modernz.js', { stdio: 'inherit' });
} catch {
  console.warn('⚠ ModernZ download failed. Download manually from https://github.com/Samillion/ModernZ');
}

// Mirror our custom mpv scripts/configs from build/ → resources/
console.log('\nMirroring mpv portable_config to resources/...');
try {
  execSync('node scripts/mirror-mpv-config.js', { stdio: 'inherit' });
} catch {
  console.warn('⚠ mpv config mirror failed.');
}

// Generate icons
console.log('\nGenerating icons...');
try {
  execSync('node scripts/generate-icons.js', { stdio: 'inherit' });
} catch {
  console.warn('⚠ Icon generation failed. Install sharp: npm install sharp --save-dev');
}

console.log('\n=== Setup complete! Run "npm start" to launch. ===');
