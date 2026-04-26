import { app } from 'electron';
import { join } from 'path';
import { existsSync, mkdirSync, unlinkSync, statSync, createWriteStream } from 'fs';
import axios from 'axios';
import {
  getImageCacheEntry,
  setImageCacheEntry,
  getImageCacheTotalSize,
  getOldestImageCacheEntries,
  deleteImageCacheEntry,
} from './database';
import { createHash } from 'crypto';

const MAX_CACHE_SIZE = 500 * 1024 * 1024; // 500MB
let cacheDir: string;

export function initImageCache(): void {
  cacheDir = join(app.getPath('userData'), 'image-cache');
  if (!existsSync(cacheDir)) {
    mkdirSync(cacheDir, { recursive: true });
  }
}

// The Emby access token shows up as `&api_key=...` on every image URL the
// renderer builds. We strip it here so the SQLite cache key (and the on-disk
// filename hash, which is derived from the key) never contains the secret,
// and so reader/writer always agree regardless of whether the caller included
// the token.
function sanitizeCacheKey(url: string): string {
  return url.replace(/([?&])api_key=[^&]*&?/g, (_m, sep: string) => (sep === '?' ? '?' : '&'))
    .replace(/[?&]$/, '');
}

function urlToFilename(url: string): string {
  const hash = createHash('sha256').update(url).digest('hex').slice(0, 24);
  // Extract extension from URL if possible
  const extMatch = url.match(/\.(\w{3,4})(?:\?|$)/);
  const ext = extMatch ? `.${extMatch[1]}` : '.jpg';
  return `${hash}${ext}`;
}

export function getCachedUrl(url: string): string | null {
  const key = sanitizeCacheKey(url);
  const entry = getImageCacheEntry(key);
  if (!entry) return null;

  // Verify file still exists on disk
  if (!existsSync(entry.local_path)) {
    deleteImageCacheEntry(key);
    return null;
  }

  return `file://${entry.local_path}`;
}

async function downloadImage(url: string, destPath: string): Promise<number> {
  const response = await axios.get(url, {
    responseType: 'stream',
    timeout: 30000,
  });

  return new Promise((resolve, reject) => {
    const writer = createWriteStream(destPath);
    response.data.pipe(writer);
    writer.on('finish', () => {
      try {
        const stats = statSync(destPath);
        resolve(stats.size);
      } catch {
        resolve(0);
      }
    });
    writer.on('error', reject);
  });
}

async function evictIfNeeded(incomingSize: number): Promise<void> {
  let totalSize = getImageCacheTotalSize();

  while (totalSize + incomingSize > MAX_CACHE_SIZE) {
    const oldest = getOldestImageCacheEntries(20);
    if (oldest.length === 0) break;

    for (const entry of oldest) {
      try {
        if (existsSync(entry.local_path)) {
          unlinkSync(entry.local_path);
        }
      } catch {
        // File already gone
      }
      deleteImageCacheEntry(entry.url);
      totalSize -= entry.size_bytes || 0;

      if (totalSize + incomingSize <= MAX_CACHE_SIZE) break;
    }
  }
}

export async function cacheImage(url: string): Promise<string | null> {
  // Storage key strips the api_key so it never lands on disk; the original
  // `url` is still used for the HTTP GET since the token is required to fetch.
  const key = sanitizeCacheKey(url);
  const existing = getCachedUrl(key);
  if (existing) return existing;

  const filename = urlToFilename(key);
  const destPath = join(cacheDir, filename);

  try {
    const sizeBytes = await downloadImage(url, destPath);
    await evictIfNeeded(sizeBytes);
    setImageCacheEntry(key, destPath, sizeBytes);
    return `file://${destPath}`;
  } catch {
    // Cleanup on failure
    try {
      if (existsSync(destPath)) unlinkSync(destPath);
    } catch { /* ignore */ }
    return null;
  }
}

export async function precacheImages(urls: string[]): Promise<void> {
  for (const url of urls) {
    await cacheImage(url);
    // Small delay between downloads
    await new Promise((r) => setTimeout(r, 50));
  }
}
