#!/usr/bin/env node
/**
 * Generate app icons from build/icon.svg using sharp.
 * Produces build/icon.ico (256x256) and build/icon.png (512x512).
 * Usage: npm run generate-icons
 * Requires: npm install -D sharp
 */

const sharp = require('sharp');
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

  // Generate PNG at multiple sizes
  const sizes = [16, 32, 48, 64, 128, 256, 512];
  const pngBuffers = [];

  for (const size of sizes) {
    const buf = await sharp(svgBuffer).resize(size, size).png().toBuffer();
    pngBuffers.push({ size, buf });
    console.log(`Generated ${size}x${size} PNG`);
  }

  // Write 512px PNG
  const png512 = pngBuffers.find((p) => p.size === 512);
  if (png512) {
    fs.writeFileSync(path.join(BUILD_DIR, 'icon.png'), png512.buf);
    console.log('Wrote build/icon.png');
  }

  // Write 256px PNG for ICO base
  const png256 = pngBuffers.find((p) => p.size === 256);
  if (png256) {
    // Create a basic ICO file from the 256px PNG
    const icoBuffer = createIco([
      pngBuffers.find((p) => p.size === 16).buf,
      pngBuffers.find((p) => p.size === 32).buf,
      pngBuffers.find((p) => p.size === 48).buf,
      pngBuffers.find((p) => p.size === 64).buf,
      pngBuffers.find((p) => p.size === 128).buf,
      pngBuffers.find((p) => p.size === 256).buf,
    ]);
    fs.writeFileSync(path.join(BUILD_DIR, 'icon.ico'), icoBuffer);
    console.log('Wrote build/icon.ico');
  }

  console.log('\nIcon generation complete.');
}

/**
 * Create a minimal ICO file from PNG buffers.
 * ICO format: header (6 bytes) + entries (16 bytes each) + image data.
 */
function createIco(pngBuffers) {
  const numImages = pngBuffers.length;
  const headerSize = 6;
  const entrySize = 16;
  const dirSize = headerSize + entrySize * numImages;

  // Calculate total size
  let totalSize = dirSize;
  for (const buf of pngBuffers) {
    totalSize += buf.length;
  }

  const ico = Buffer.alloc(totalSize);
  let offset = 0;

  // ICO header
  ico.writeUInt16LE(0, offset);          // reserved
  ico.writeUInt16LE(1, offset + 2);      // type: 1 = ICO
  ico.writeUInt16LE(numImages, offset + 4); // count
  offset += headerSize;

  // Directory entries
  let dataOffset = dirSize;
  const sizes = [16, 32, 48, 64, 128, 256];
  for (let i = 0; i < numImages; i++) {
    const size = sizes[i] || 0;
    ico.writeUInt8(size >= 256 ? 0 : size, offset);     // width (0 = 256)
    ico.writeUInt8(size >= 256 ? 0 : size, offset + 1); // height
    ico.writeUInt8(0, offset + 2);                        // color palette
    ico.writeUInt8(0, offset + 3);                        // reserved
    ico.writeUInt16LE(1, offset + 4);                     // color planes
    ico.writeUInt16LE(32, offset + 6);                    // bits per pixel
    ico.writeUInt32LE(pngBuffers[i].length, offset + 8);  // data size
    ico.writeUInt32LE(dataOffset, offset + 12);           // data offset
    dataOffset += pngBuffers[i].length;
    offset += entrySize;
  }

  // Image data
  for (const buf of pngBuffers) {
    buf.copy(ico, offset);
    offset += buf.length;
  }

  return ico;
}

main().catch((err) => {
  console.error('Error:', err.message);
  process.exit(1);
});
