import { app, safeStorage } from 'electron';
import { readFileSync, writeFileSync, existsSync, unlinkSync } from 'fs';
import { join } from 'path';
import type { TraktTokens } from './trakt-types';

const FILE_NAME = 'trakt-credentials.bin';

let cached: TraktTokens | null = null;
let loaded = false;

function filePath(): string {
  return join(app.getPath('userData'), FILE_NAME);
}

export function isEncryptionAvailable(): boolean {
  try {
    return safeStorage.isEncryptionAvailable();
  } catch {
    return false;
  }
}

export function loadTokens(): TraktTokens | null {
  if (loaded) return cached;
  loaded = true;
  const path = filePath();
  if (!existsSync(path)) return null;
  if (!isEncryptionAvailable()) {
    console.warn('[trakt-store] safeStorage unavailable — refusing to read credentials');
    return null;
  }
  try {
    const blob = readFileSync(path);
    const decrypted = safeStorage.decryptString(blob);
    cached = JSON.parse(decrypted) as TraktTokens;
    return cached;
  } catch (err) {
    console.warn('[trakt-store] failed to decrypt credentials:', err);
    return null;
  }
}

export function saveTokens(tokens: TraktTokens): void {
  if (!isEncryptionAvailable()) {
    throw new Error('safeStorage encryption unavailable on this platform');
  }
  const blob = safeStorage.encryptString(JSON.stringify(tokens));
  writeFileSync(filePath(), blob);
  cached = tokens;
  loaded = true;
}

export function clearTokens(): void {
  const path = filePath();
  if (existsSync(path)) {
    try { unlinkSync(path); } catch (err) {
      console.warn('[trakt-store] failed to remove credentials file:', err);
    }
  }
  cached = null;
  loaded = true;
}

export function hasTokens(): boolean {
  return loadTokens() !== null;
}
