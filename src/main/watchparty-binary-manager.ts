import { spawn } from 'child_process';
import crypto from 'crypto';
import fs from 'fs';
import https from 'https';
import path from 'path';
import { app } from 'electron';
import {
  CLOUDFLARED_PIN,
  FFMPEG_EXPECTED_ZIP_TOPLEVEL,
  FFMPEG_PIN,
  WATCHPARTY_MANIFEST_SCHEMA_VERSION,
} from '../shared/watchparty-pins';

export interface WatchPartyBinaryPaths {
  ffmpegPath: string;
  cloudflaredPath: string;
}

type ProgressData = { phase: 'ffmpeg' | 'cloudflared' | 'unzip'; percent: number };
type ErrorData = { phase: string; message: string };
type EventName = 'progress' | 'error';
type EventCallback = (data: unknown) => void;

interface ManifestEntry {
  mtime: number; // ms since epoch
  size: number; // bytes
  sha256: string;
}

interface Manifest {
  schemaVersion: number;
  ffmpeg: ManifestEntry;
  cloudflared: ManifestEntry;
}

class WatchPartyBinaryManager {
  private listeners = new Map<EventName, EventCallback[]>();
  private inflight: Promise<WatchPartyBinaryPaths> | null = null;
  // Skip the manifest stat + read after the first successful verification
  // this process lifetime. Invalidated only by process restart.
  private readyCache = false;

  private get binDir(): string {
    return path.join(app.getPath('userData'), 'bin');
  }
  private get tmpDir(): string {
    return path.join(this.binDir, '.tmp');
  }
  private get manifestPath(): string {
    return path.join(this.binDir, '.manifest.json');
  }
  private get ffmpegPath(): string {
    return path.join(this.binDir, 'ffmpeg.exe');
  }
  private get cloudflaredPath(): string {
    return path.join(this.binDir, 'cloudflared.exe');
  }

  isReady(): boolean {
    if (this.readyCache) return true;
    try {
      if (!fs.existsSync(this.ffmpegPath) || !fs.existsSync(this.cloudflaredPath)) {
        return false;
      }
      const manifest = this.readManifest();
      if (!manifest || manifest.schemaVersion !== WATCHPARTY_MANIFEST_SCHEMA_VERSION) {
        return false;
      }
      // Pin bump? Manifest sha won't match the new pinned sha → re-download.
      if (manifest.ffmpeg.sha256 !== FFMPEG_PIN.sha256) return false;
      if (manifest.cloudflared.sha256 !== CLOUDFLARED_PIN.sha256) return false;
      const ffStat = fs.statSync(this.ffmpegPath);
      const cfStat = fs.statSync(this.cloudflaredPath);
      const ffOk = ffStat.size === manifest.ffmpeg.size && ffStat.mtimeMs === manifest.ffmpeg.mtime;
      const cfOk =
        cfStat.size === manifest.cloudflared.size && cfStat.mtimeMs === manifest.cloudflared.mtime;
      if (!ffOk || !cfOk) {
        // Stat drift (AV scanner touch, manual copy, etc.) — confirm by hash.
        const ffHash = this.hashFileSync(this.ffmpegPath);
        const cfHash = this.hashFileSync(this.cloudflaredPath);
        if (ffHash !== FFMPEG_PIN.sha256 || cfHash !== CLOUDFLARED_PIN.sha256) {
          return false;
        }
        // Hashes still good — rewrite manifest with fresh stat so future
        // isReady() calls take the fast path again.
        this.writeManifest(ffStat, ffHash, cfStat, cfHash);
      }
      this.readyCache = true;
      return true;
    } catch (err) {
      console.warn('[watchparty-binary] isReady() failed, treating as not-ready:', err);
      return false;
    }
  }

  async ensureBinaries(): Promise<WatchPartyBinaryPaths> {
    if (this.isReady()) {
      return { ffmpegPath: this.ffmpegPath, cloudflaredPath: this.cloudflaredPath };
    }
    if (this.inflight) return this.inflight;
    this.inflight = this._doEnsure().finally(() => {
      this.inflight = null;
    });
    return this.inflight;
  }

  getFfmpegPath(): string {
    if (!this.isReady()) {
      throw new Error('WatchParty binaries not ready — call ensureBinaries() first');
    }
    return this.ffmpegPath;
  }

  getCloudflaredPath(): string {
    if (!this.isReady()) {
      throw new Error('WatchParty binaries not ready — call ensureBinaries() first');
    }
    return this.cloudflaredPath;
  }

  on(event: EventName, fn: EventCallback): void {
    const list = this.listeners.get(event) || [];
    list.push(fn);
    this.listeners.set(event, list);
  }

  off(event: EventName, fn: EventCallback): void {
    const list = this.listeners.get(event) || [];
    this.listeners.set(
      event,
      list.filter((f) => f !== fn),
    );
  }

  private emit(event: EventName, data: unknown): void {
    (this.listeners.get(event) || []).forEach((fn) => {
      try {
        fn(data);
      } catch (err) {
        console.error('[watchparty-binary] listener threw:', err);
      }
    });
  }

  // ── Internal pipeline ─────────────────────────────────────

  private async _doEnsure(): Promise<WatchPartyBinaryPaths> {
    fs.mkdirSync(this.binDir, { recursive: true });
    fs.mkdirSync(this.tmpDir, { recursive: true });
    this.cleanupTmp();

    // True parallel. If ffmpeg succeeds and cloudflared fails (or vice
    // versa), the successful binary stays on disk; the next call's hash
    // check will skip its re-download.
    await Promise.all([this.ensureFfmpeg(), this.ensureCloudflared()]);

    const ffStat = fs.statSync(this.ffmpegPath);
    const cfStat = fs.statSync(this.cloudflaredPath);
    this.writeManifest(ffStat, FFMPEG_PIN.sha256, cfStat, CLOUDFLARED_PIN.sha256);
    this.readyCache = true;

    return { ffmpegPath: this.ffmpegPath, cloudflaredPath: this.cloudflaredPath };
  }

  private async ensureCloudflared(): Promise<void> {
    if (await this.fileMatchesPin(this.cloudflaredPath, CLOUDFLARED_PIN.sha256)) return;
    const partPath = path.join(this.tmpDir, 'cloudflared.exe.part');
    try {
      await this.downloadWithProgress(CLOUDFLARED_PIN.url, partPath, 'cloudflared');
      const hash = this.hashFileSync(partPath);
      if (hash !== CLOUDFLARED_PIN.sha256) {
        throw new Error(
          `cloudflared SHA256 mismatch — expected ${CLOUDFLARED_PIN.sha256}, got ${hash}`,
        );
      }
      if (fs.existsSync(this.cloudflaredPath)) fs.unlinkSync(this.cloudflaredPath);
      fs.renameSync(partPath, this.cloudflaredPath);
    } catch (err) {
      this.safeUnlink(partPath);
      const message = err instanceof Error ? err.message : String(err);
      this.emit('error', { phase: 'cloudflared', message } satisfies ErrorData);
      throw err;
    }
  }

  private async ensureFfmpeg(): Promise<void> {
    if (await this.fileMatchesPin(this.ffmpegPath, FFMPEG_PIN.sha256)) return;
    const zipPart = path.join(this.tmpDir, 'ffmpeg.zip.part');
    const zipFinal = path.join(this.tmpDir, 'ffmpeg.zip');
    const extractDir = path.join(this.tmpDir, 'ffmpeg-extracted');
    // Tracks which sub-phase any thrown error belongs to so the renderer
    // sees the correct phase tag on `setup-error`.
    let errorPhase: 'ffmpeg' | 'unzip' = 'ffmpeg';
    try {
      await this.downloadWithProgress(FFMPEG_PIN.url, zipPart, 'ffmpeg');
      const hash = this.hashFileSync(zipPart);
      if (hash !== FFMPEG_PIN.sha256) {
        throw new Error(
          `ffmpeg zip SHA256 mismatch — expected ${FFMPEG_PIN.sha256}, got ${hash}`,
        );
      }
      if (fs.existsSync(zipFinal)) fs.unlinkSync(zipFinal);
      fs.renameSync(zipPart, zipFinal);

      // Indeterminate sentinel — renderer shows a spinner, not a fake bar.
      errorPhase = 'unzip';
      this.emit('progress', { phase: 'unzip', percent: -1 } satisfies ProgressData);
      if (fs.existsSync(extractDir)) fs.rmSync(extractDir, { recursive: true, force: true });
      fs.mkdirSync(extractDir, { recursive: true });
      await this.runPowerShellExpand(zipFinal, extractDir);

      // Expected path first; recursive walk as defensive fallback.
      const expectedFfmpeg = path.join(
        extractDir,
        FFMPEG_EXPECTED_ZIP_TOPLEVEL,
        'bin',
        'ffmpeg.exe',
      );
      let foundFfmpeg: string | null = fs.existsSync(expectedFfmpeg) ? expectedFfmpeg : null;
      if (!foundFfmpeg) {
        foundFfmpeg = this.findFileRecursive(extractDir, 'ffmpeg.exe');
      }
      if (!foundFfmpeg) {
        const topLevel = fs.readdirSync(extractDir);
        throw new Error(
          `ffmpeg.exe not found anywhere under extracted zip. Top-level entries: ${JSON.stringify(topLevel)}`,
        );
      }

      if (fs.existsSync(this.ffmpegPath)) fs.unlinkSync(this.ffmpegPath);
      try {
        fs.renameSync(foundFfmpeg, this.ffmpegPath);
      } catch {
        // rename can fail across drives; fall back to copy.
        fs.copyFileSync(foundFfmpeg, this.ffmpegPath);
      }

      fs.rmSync(extractDir, { recursive: true, force: true });
      this.safeUnlink(zipFinal);
    } catch (err) {
      this.safeUnlink(zipPart);
      this.safeUnlink(zipFinal);
      if (fs.existsSync(extractDir)) {
        try {
          fs.rmSync(extractDir, { recursive: true, force: true });
        } catch {
          /* ignore */
        }
      }
      const message = err instanceof Error ? err.message : String(err);
      this.emit('error', { phase: errorPhase, message } satisfies ErrorData);
      throw err;
    }
  }

  // ── HTTP download with progress + redirect following ──────

  private downloadWithProgress(
    url: string,
    destPath: string,
    phase: 'ffmpeg' | 'cloudflared',
  ): Promise<void> {
    return new Promise((resolve, reject) => {
      let redirectsRemaining = 5;
      const start = (currentUrl: string): void => {
        const req = https.get(currentUrl, (res) => {
          // GitHub release URLs 302 to S3 — follow.
          if (
            res.statusCode &&
            res.statusCode >= 300 &&
            res.statusCode < 400 &&
            res.headers.location
          ) {
            res.resume();
            if (redirectsRemaining-- === 0) {
              reject(new Error(`Too many redirects fetching ${phase}`));
              return;
            }
            const next = new URL(res.headers.location, currentUrl).toString();
            start(next);
            return;
          }
          if (!res.statusCode || res.statusCode < 200 || res.statusCode >= 300) {
            res.resume();
            reject(new Error(`HTTP ${res.statusCode} fetching ${phase} from ${currentUrl}`));
            return;
          }
          const total = Number(res.headers['content-length']) || 0;
          let received = 0;
          let lastEmitPercent = -1;
          let lastEmitMs = 0;
          const out = fs.createWriteStream(destPath);
          res.on('data', (chunk: Buffer) => {
            received += chunk.length;
            if (total > 0) {
              const percent = Math.floor((received / total) * 100);
              const now = Date.now();
              // Throttle: emit at most every 100ms, but always on integer change.
              if (percent !== lastEmitPercent && (now - lastEmitMs > 100 || percent === 100)) {
                lastEmitPercent = percent;
                lastEmitMs = now;
                this.emit('progress', { phase, percent } satisfies ProgressData);
              }
            }
          });
          let errored = false;
          res.pipe(out);
          // 'close' (not 'finish') so we wait for the fd to actually be
          // released before the caller renames the file — avoids EBUSY on
          // Windows. autoClose:true (default) handles the fd close itself.
          out.on('close', () => {
            if (errored) return;
            // Guarantee a final 100% in case the throttle skipped the last tick
            // or content-length was absent.
            this.emit('progress', { phase, percent: 100 } satisfies ProgressData);
            resolve();
          });
          out.on('error', (err) => {
            errored = true;
            out.destroy();
            reject(err);
          });
          res.on('error', (err) => {
            errored = true;
            out.destroy();
            reject(err);
          });
        });
        req.on('error', (err) => reject(err));
        req.setTimeout(60_000, () => {
          req.destroy(new Error(`Request timed out after 60s fetching ${phase}`));
        });
      };
      start(url);
    });
  }

  // ── Hashing ────────────────────────────────────────────────

  private hashFileSync(filePath: string): string {
    const hash = crypto.createHash('sha256');
    const buf = fs.readFileSync(filePath);
    hash.update(buf);
    return hash.digest('hex');
  }

  // Skip re-download when a previous successful download left the file in
  // place but the manifest was lost (e.g. user wiped .manifest.json).
  private async fileMatchesPin(filePath: string, expectedSha: string): Promise<boolean> {
    if (!fs.existsSync(filePath)) return false;
    try {
      return this.hashFileSync(filePath) === expectedSha;
    } catch {
      return false;
    }
  }

  // ── Manifest ───────────────────────────────────────────────

  private readManifest(): Manifest | null {
    try {
      if (!fs.existsSync(this.manifestPath)) return null;
      const raw = fs.readFileSync(this.manifestPath, 'utf-8');
      const parsed = JSON.parse(raw) as Manifest;
      if (typeof parsed !== 'object' || parsed === null) return null;
      if (!parsed.ffmpeg || !parsed.cloudflared) return null;
      return parsed;
    } catch {
      return null;
    }
  }

  private writeManifest(
    ffStat: fs.Stats,
    ffHash: string,
    cfStat: fs.Stats,
    cfHash: string,
  ): void {
    const manifest: Manifest = {
      schemaVersion: WATCHPARTY_MANIFEST_SCHEMA_VERSION,
      ffmpeg: { mtime: ffStat.mtimeMs, size: ffStat.size, sha256: ffHash },
      cloudflared: { mtime: cfStat.mtimeMs, size: cfStat.size, sha256: cfHash },
    };
    fs.writeFileSync(this.manifestPath, JSON.stringify(manifest, null, 2), 'utf-8');
  }

  // ── PowerShell extract ─────────────────────────────────────

  private runPowerShellExpand(zipPath: string, destDir: string): Promise<void> {
    return new Promise((resolve, reject) => {
      const escape = (p: string): string => p.replace(/'/g, "''");
      const args = [
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-Command',
        `Expand-Archive -LiteralPath '${escape(zipPath)}' -DestinationPath '${escape(destDir)}' -Force`,
      ];
      const proc = spawn('powershell.exe', args, { windowsHide: true });
      let stderr = '';
      proc.stderr.on('data', (d: Buffer) => {
        stderr += d.toString();
      });
      proc.on('error', (err) => reject(err));
      proc.on('exit', (code) => {
        if (code === 0) resolve();
        else reject(new Error(`Expand-Archive failed with exit code ${code}: ${stderr.trim()}`));
      });
    });
  }

  // ── Filesystem helpers ─────────────────────────────────────

  private findFileRecursive(rootDir: string, fileName: string): string | null {
    const stack: string[] = [rootDir];
    while (stack.length > 0) {
      const cur = stack.pop() as string;
      let entries: fs.Dirent[];
      try {
        entries = fs.readdirSync(cur, { withFileTypes: true });
      } catch {
        continue;
      }
      for (const ent of entries) {
        const full = path.join(cur, ent.name);
        if (ent.isFile() && ent.name === fileName) return full;
        if (ent.isDirectory()) stack.push(full);
      }
    }
    return null;
  }

  private safeUnlink(p: string): void {
    try {
      if (fs.existsSync(p)) fs.unlinkSync(p);
    } catch {
      /* ignore */
    }
  }

  // Sweep stale .part files / orphaned extracted dirs from a prior crashed
  // run. Always safe — only operates inside our own .tmp dir.
  private cleanupTmp(): void {
    try {
      if (!fs.existsSync(this.tmpDir)) return;
      for (const entry of fs.readdirSync(this.tmpDir, { withFileTypes: true })) {
        const full = path.join(this.tmpDir, entry.name);
        try {
          if (entry.isDirectory()) fs.rmSync(full, { recursive: true, force: true });
          else fs.unlinkSync(full);
        } catch {
          /* ignore */
        }
      }
    } catch {
      /* ignore */
    }
  }
}

export const watchPartyBinaryManager = new WatchPartyBinaryManager();
