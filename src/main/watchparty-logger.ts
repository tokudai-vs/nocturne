import { app } from 'electron';
import * as fs from 'fs';
import * as path from 'path';

// Packaged Electron on Windows builds as a GUI-subsystem app; its stdout is
// detached from the launching terminal, so console.log() vanishes into the
// void. This logger writes timestamped lines to {userData}/watch-party/
// session.log AND to console so the dev path still sees them. Truncate
// at session start: the file always represents the most recent run, with
// no accumulation across runs (no rotation needed because the file caps
// at one session's worth).

type Level = 'INFO' | 'WARN' | 'ERROR';

const TUNNEL_URL_LIMIT_BYTES = 4 * 1024 * 1024;

class WatchPartyLogger {
  private logPath: string | null = null;
  private ready = false;
  private buffer: string[] = [];

  private get baseDir(): string {
    return path.join(app.getPath('userData'), 'watch-party');
  }

  /** Truncate-then-open. Called at the top of startSession. */
  startSessionLog(sessionId: string): void {
    try {
      fs.mkdirSync(this.baseDir, { recursive: true });
      this.logPath = path.join(this.baseDir, 'session.log');
      // Truncate. Subsequent writes use appendFileSync.
      fs.writeFileSync(
        this.logPath,
        `=== Watch Party session ${sessionId} started ${new Date().toISOString()} ===\n`,
      );
      this.ready = true;
      // Flush anything that landed before the log was open.
      for (const line of this.buffer) {
        this.appendRaw(line);
      }
      this.buffer = [];
    } catch (err) {
      // Even if the log file can't open, keep console logging.
      console.error('[watchparty-logger] failed to open log file:', err);
      this.ready = false;
    }
  }

  /** Called from teardown — keeps the file but writes a closing marker. */
  endSessionLog(reason: string): void {
    this.info('session', `Session log closed (reason=${reason})`);
    // Leave the file in place so the user can inspect it post-mortem.
    this.ready = false;
  }

  info(tag: string, message: string): void {
    this.write('INFO', tag, message);
  }

  warn(tag: string, message: string): void {
    this.write('WARN', tag, message);
  }

  error(tag: string, message: string): void {
    this.write('ERROR', tag, message);
  }

  /** Redact the Emby ?api_key=... so logs are safe to share. */
  redactUrl(url: string): string {
    return url.replace(/(\?|&)api_key=[^&]*/gi, '$1api_key=<redacted>');
  }

  /** Redact api_key from each element of an ffmpeg argv array. */
  redactArgs(args: string[]): string[] {
    return args.map((a) => (typeof a === 'string' && a.includes('api_key=') ? this.redactUrl(a) : a));
  }

  getLogPath(): string | null {
    return this.logPath;
  }

  // ── Internals ─────────────────────────────────────────────

  private write(level: Level, tag: string, message: string): void {
    const ts = new Date().toISOString();
    const line = `[${ts}] [${level}] [${tag}] ${message}\n`;
    // Mirror to the main-process console for dev-mode visibility.
    if (level === 'ERROR') console.error(line.trimEnd());
    else if (level === 'WARN') console.warn(line.trimEnd());
    else console.log(line.trimEnd());

    if (this.ready && this.logPath) {
      this.appendRaw(line);
    } else {
      // Buffer pre-session lines (e.g. IPC handler entry before startSession
      // calls startSessionLog). Flushed on the next startSessionLog call.
      this.buffer.push(line);
      // Hard cap on the buffer to bound memory if startSession is never
      // reached (e.g. crash before init).
      if (this.buffer.length > 1024) this.buffer.shift();
    }
  }

  private appendRaw(line: string): void {
    if (!this.logPath) return;
    try {
      // Bound runaway logs (e.g. transcoder stderr storms). Trim from the
      // top by truncate-and-rewrite if past cap. Keeps the most recent
      // half — the cause of the failure is usually nearby.
      const stat = fs.statSync(this.logPath);
      if (stat.size > TUNNEL_URL_LIMIT_BYTES) {
        const content = fs.readFileSync(this.logPath, 'utf-8');
        fs.writeFileSync(
          this.logPath,
          content.slice(-Math.floor(TUNNEL_URL_LIMIT_BYTES / 2)) + '... [truncated] ...\n',
        );
      }
    } catch {
      /* stat is best-effort */
    }
    try {
      fs.appendFileSync(this.logPath, line);
    } catch (err) {
      console.error('[watchparty-logger] appendFileSync failed:', err);
      this.ready = false;
    }
  }
}

export const watchPartyLogger = new WatchPartyLogger();
