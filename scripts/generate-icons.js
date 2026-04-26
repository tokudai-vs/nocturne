#!/usr/bin/env node
/**
 * Generate app icons from build/icon.svg using sharp + png-to-ico.
 * Produces build/icon.ico (multi-size) and build/icon.png (512x512).
 * Usage: npm run generate-icons
 * Requires: npm install -D sharp png-to-ico
 */

const sharp = require('sharp');
const { default: pngToIco } = require('png-to-ico');
const path = require('path');
const fs = require('fs');

const SVG_PATH = path.join(__dirname, '..', 'build', 'icon.svg');
const BUILD_DIR = path.join(__dirname, '..', 'build');

async function main() {
  if (!fs.existsSync(SVG_PATH)) {
    console.error('ERROR: build/icon.svg not found');
    process.exit(1);
  }

  const svgBuffer = fs.readFileSync(SVG_PATH);

  // Generate PNGs at multiple sizes for ICO
  const icoSizes = [16, 32, 48, 64, 128, 256];
  const tempPaths = [];

  for (const size of icoSizes) {
    const pngPath = path.join(BUILD_DIR, `icon-${size}.png`);
    await sharp(svgBuffer).resize(size, size).png().toFile(pngPath);
    tempPaths.push(pngPath);
    console.log(`Generated ${size}x${size} PNG`);
  }

  // Main icon.png (512x512 for high-DPI / Linux / BrowserWindow)
  await sharp(svgBuffer).resize(512, 512).png().toFile(path.join(BUILD_DIR, 'icon.png'));
  console.log('Generated icon.png (512x512)');

  // Generate proper .ico from PNGs using png-to-ico
  const icoBuffer = await pngToIco(tempPaths.map((p) => fs.readFileSync(p)));
  fs.writeFileSync(path.join(BUILD_DIR, 'icon.ico'), icoBuffer);
  console.log('Generated icon.ico (multi-size)');

  // Cleanup temp PNGs
  for (const p of tempPaths) {
    fs.unlinkSync(p);
  }

  const icoSize = fs.statSync(path.join(BUILD_DIR, 'icon.ico')).size;
  console.log(`\nIcon generation complete. icon.ico = ${(icoSize / 1024).toFixed(1)} KB`);
}

main().catch((err) => {
  console.error('Error:', err.message);
  process.exit(1);
});
