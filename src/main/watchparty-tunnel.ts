import { spawn, type ChildProcess } from 'child_process';
import { watchPartyBinaryManager } from './watchparty-binary-manager';
import { watchPartyLogger } from './watchparty-logger';

// `cloudflared tunnel --url http://localhost:PORT` emits its assigned
// trycloudflare hostname to stderr in a banner that looks like:
//
//   2024-xx-xxT… INF |  https://random-words-12345.trycloudflare.com  |
//
// We scan stderr for the first match. If nothing appears within
// URL_TIMEOUT_MS the spawn is treated as failed.
const TUNNEL_URL_RE = /(https:\/\/[a-z0-9-]+\.trycloudflare\.com)/i;
const URL_TIMEOUT_MS = 30_000;
const STOP_TIMEOUT_MS = 5_000;
const STDERR_BUF_CAP_BYTES = 64 * 1024;
const STDERR_BUF_KEEP_BYTES = 32 * 1024;

export interface TunnelOptions {
  /** Local HTTP port that cloudflared should forward to. */
  localPort: number;
}

type ExitCallback = (info: { code: number | null; signal: NodeJS.Signals | null }) => void;

export class WatchPartyTunnel {
  private proc: ChildProcess | null = null;
  private stderrTail = '';
  private stopping = false;
  private exitCbs: ExitCallback[] = [];

  /** Resolves with the public https URL once cloudflared logs it. */
  start(opts: TunnelOptions): Promise<string> {
    if (this.proc) throw new Error('Tunnel already running');
    const cloudflaredPath = watchPartyBinaryManager.getCloudflaredPath();
    // --no-autoupdate disables the background self-update loop (we manage
    // the binary via the pin/manifest); --metrics binds the prometheus
    // endpoint to an ephemeral loopback port instead of cloudflared's
    // default 0.0.0.0 bind.
    const args = [
      'tunnel',
      '--url', `http://localhost:${opts.localPort}`,
      '--no-autoupdate',
      '--metrics', '127.0.0.1:0',
    ];

    return new Promise<string>((resolve, reject) => {
      const proc = spawn(cloudflaredPath, args, {
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true,
      });
      this.proc = proc;
      this.stopping = false;
      this.stderrTail = '';

      watchPartyLogger.info(
        'tunnel',
        `spawn cloudflared=${cloudflaredPath} argv=${JSON.stringify(args)}${proc.pid ? ` pid=${proc.pid}` : ''}`,
      );

      let urlResolved = false;
      const onUrl = (url: string): void => {
        if (urlResolved) return;
        urlResolved = true;
        clearTimeout(timeout);
        watchPartyLogger.info('tunnel', `URL resolved: ${url}`);
        resolve(url);
      };

      const scan = (chunk: Buffer): void => {
        const text = chunk.toString();
        this.stderrTail += text;
        if (this.stderrTail.length > STDERR_BUF_CAP_BYTES) {
          this.stderrTail = this.stderrTail.slice(-STDERR_BUF_KEEP_BYTES);
        }
        if (!urlResolved) {
          const m = text.match(TUNNEL_URL_RE) ?? this.stderrTail.match(TUNNEL_URL_RE);
          if (m) onUrl(m[1]);
        }
      };

      proc.stdout?.on('data', scan);
      proc.stderr?.on('data', scan);

      proc.on('error', (err) => {
        watchPartyLogger.error('tunnel', `cloudflared spawn error: ${err.message}`);
        if (urlResolved) return;
        urlResolved = true;
        clearTimeout(timeout);
        reject(err);
      });

      proc.on('exit', (code, signal) => {
        const wasStopping = this.stopping;
        this.proc = null;
        if (wasStopping) {
          watchPartyLogger.info('tunnel', `cloudflared exited (graceful stop) code=${code} signal=${signal ?? '-'}`);
        }
        for (const cb of this.exitCbs) {
          try {
            cb({ code, signal });
          } catch (err) {
            watchPartyLogger.error('tunnel', `exit listener threw: ${err instanceof Error ? err.message : String(err)}`);
          }
        }
        if (urlResolved) return;
        urlResolved = true;
        clearTimeout(timeout);
        if (wasStopping) {
          reject(new Error('Tunnel stopped before URL resolved'));
          return;
        }
        const tail = this.stderrTail.slice(-2000);
        watchPartyLogger.error(
          'tunnel',
          `cloudflared exited code=${code} signal=${signal ?? '-'} before URL stderr-tail:\n${tail}`,
        );
        reject(
          new Error(
            `cloudflared exited code=${code} signal=${signal ?? '-'} before URL resolved\nstderr tail:\n${tail}`,
          ),
        );
      });

      const timeout = setTimeout(() => {
        if (urlResolved) return;
        urlResolved = true;
        try {
          proc.kill('SIGKILL');
        } catch {
          /* already exited */
        }
        const tail = this.stderrTail.slice(-2000);
        watchPartyLogger.error(
          'tunnel',
          `URL did not appear within ${URL_TIMEOUT_MS}ms; killing cloudflared. stderr-tail:\n${tail}`,
        );
        reject(
          new Error(
            `cloudflared tunnel URL did not appear within ${URL_TIMEOUT_MS}ms\nstderr tail:\n${tail}`,
          ),
        );
      }, URL_TIMEOUT_MS);
    });
  }

  onExit(cb: ExitCallback): void {
    this.exitCbs.push(cb);
  }

  isRunning(): boolean {
    return this.proc !== null;
  }

  async stop(): Promise<void> {
    if (!this.proc) return;
    this.stopping = true;
    const proc = this.proc;
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        try {
          proc.kill('SIGKILL');
        } catch {
          /* already exited */
        }
      }, STOP_TIMEOUT_MS);
      proc.once('exit', () => {
        clearTimeout(timer);
        resolve();
      });
      try {
        // Windows has no SIGTERM; .kill() = TerminateProcess. cloudflared
        // doesn't flush anything that matters on a clean exit, so SIGKILL
        // straight away is fine.
        proc.kill();
      } catch {
        clearTimeout(timer);
        resolve();
      }
    });
  }
}
