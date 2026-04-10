import { spawn, ChildProcess } from 'child_process';
import net from 'net';
import path from 'path';
import fs from 'fs';
import { app } from 'electron';

interface PlayOptions {
  startPositionTicks?: number;
  title?: string;
}

type EventCallback = (data: unknown) => void;

class MpvManager {
  private proc: ChildProcess | null = null;
  private socket: net.Socket | null = null;
  private pipeName: string;
  private reqId = 0;
  private callbacks = new Map<
    number,
    { resolve: (data: unknown) => void; reject: (err: Error) => void }
  >();
  private listeners = new Map<string, EventCallback[]>();
  private buffer = '';
  private isReady = false;

  constructor() {
    this.pipeName = '\\\\.\\pipe\\nocturne-mpv-' + process.pid;
  }

  /**
   * Start mpv in idle mode. Call once during app startup.
   * mpv stays running hidden, waiting for loadfile commands.
   */
  async startIdle(): Promise<void> {
    if (this.proc && !this.proc.killed) return;

    const { exe: mpvPath, dir: mpvDir } = this.findMpv();
    const configDir = path.join(mpvDir, 'portable_config');

    const args = [
      '--idle=yes',
      `--input-ipc-server=${this.pipeName}`,
      '--force-window=no',
      '--osc=no',
      '--osd-bar=no',
      '--border=no',
      '--really-quiet',
    ];

    if (fs.existsSync(configDir)) {
      args.push(`--config-dir=${configDir}`);
    }

    this.proc = spawn(mpvPath, args, {
      stdio: ['ignore', 'pipe', 'pipe'],
      detached: false,
      windowsHide: true,
    });

    this.proc.stderr?.on('data', (d: Buffer) => {
      const msg = d.toString().trim();
      if (msg) console.log('[mpv]', msg);
    });

    this.proc.on('exit', (code) => {
      console.log('[mpv] Process exited with code:', code);
      this.cleanup();
      this.emit('process-exit', { code });
    });

    this.proc.on('error', (err) => {
      console.error('[mpv] Process error:', err.message);
      this.emit('error', { message: err.message });
    });

    await this.waitAndConnect();
    this.isReady = true;
    console.log('[mpv] Idle mode ready, waiting for commands');
  }

  /**
   * Load and play a file. mpv must already be running in idle mode.
   * Instant — no process startup delay.
   */
  async loadFile(url: string, options: PlayOptions = {}): Promise<void> {
    if (!this.isReady || !this.socket) {
      await this.startIdle();
    }

    if (options.title) {
      await this.setProperty('force-media-title', options.title);
    }

    if (options.startPositionTicks) {
      const seconds = options.startPositionTicks / 10_000_000;
      await this.setProperty('start', seconds.toString());
    } else {
      await this.setProperty('start', 'none');
    }

    // Show window and go fullscreen before loading
    await this.setProperty('force-window', 'yes');
    await this.setProperty('fullscreen', true);

    // Load the file — triggers playback
    await this.command(['loadfile', url, 'replace']);
  }

  /**
   * Stop playback but keep mpv alive in idle mode.
   */
  async stopPlayback(): Promise<void> {
    if (!this.isReady || !this.socket) return;

    try {
      await this.command(['stop']);
      await this.setProperty('fullscreen', false);
      await this.setProperty('force-window', 'no');
    } catch (err) {
      console.error('[mpv] Error stopping playback:', err);
    }
  }

  /**
   * Fully quit the mpv process. Call on app exit.
   */
  async quit(): Promise<void> {
    try {
      if (this.socket) await this.command(['quit']);
    } catch {
      /* ignore */
    }
    this.cleanup();
  }

  private findMpv(): { exe: string; dir: string } {
    const dirs = [
      path.join(process.resourcesPath, 'mpv'),
      path.join(app.getAppPath(), 'resources', 'mpv'),
      path.join(app.getAppPath(), '..', 'resources', 'mpv'),
      path.join(app.getAppPath(), 'build', 'mpv'),
    ];
    for (const dir of dirs) {
      const exe = path.join(dir, 'mpv.exe');
      if (fs.existsSync(exe)) {
        console.log('[mpv] Found mpv at:', exe);
        return { exe, dir };
      }
    }
    console.log('[mpv] mpv not found in any known path, falling back to PATH');
    return { exe: 'mpv', dir: '' };
  }

  private async waitAndConnect(retries = 20, delay = 200): Promise<void> {
    for (let i = 0; i < retries; i++) {
      try {
        await this.connectSocket();
        return;
      } catch {
        await new Promise((r) => setTimeout(r, delay));
      }
    }
    throw new Error('Failed to connect to mpv IPC pipe');
  }

  private connectSocket(): Promise<void> {
    return new Promise((resolve, reject) => {
      const socket = net.createConnection(this.pipeName);
      let connected = false;

      socket.on('data', (data: Buffer) => {
        this.buffer += data.toString();
        const lines = this.buffer.split('\n');
        this.buffer = lines.pop() || '';
        for (const line of lines) {
          if (!line.trim()) continue;
          try {
            const msg = JSON.parse(line);
            if (msg.event === 'end-file') {
              this.emit('end-file', msg);
            } else if (msg.event === 'file-loaded') {
              this.emit('file-loaded', {});
            } else if (msg.event) {
              this.emit(msg.event, msg);
            }
            if (msg.request_id !== undefined) {
              const cb = this.callbacks.get(msg.request_id);
              if (cb) {
                if (msg.error === 'success') cb.resolve(msg.data);
                else cb.reject(new Error(msg.error));
                this.callbacks.delete(msg.request_id);
              }
            }
          } catch {
            /* ignore parse errors */
          }
        }
      });

      socket.on('connect', () => {
        connected = true;
        this.socket = socket;
        resolve();
      });

      socket.on('error', (err) => {
        if (!connected) {
          reject(err);
        } else {
          console.error('[mpv] IPC socket error:', err.message);
          this.cleanup();
          this.emit('error', { message: 'mpv IPC connection lost: ' + err.message });
        }
      });

      socket.on('close', () => {
        if (connected) {
          console.log('[mpv] IPC socket closed');
          this.isReady = false;
          this.socket = null;
        }
      });

      socket.on('end', () => {
        if (connected) {
          console.log('[mpv] IPC socket ended');
          this.isReady = false;
          this.socket = null;
        }
      });
    });
  }

  async command(cmd: unknown[]): Promise<unknown> {
    return new Promise((resolve, reject) => {
      if (!this.socket) return reject(new Error('Not connected to mpv'));
      const id = ++this.reqId;
      let settled = false;
      this.callbacks.set(id, {
        resolve: (data: unknown) => {
          if (settled) return;
          settled = true;
          resolve(data);
        },
        reject: (err: Error) => {
          if (settled) return;
          settled = true;
          reject(err);
        },
      });
      this.socket.write(JSON.stringify({ command: cmd, request_id: id }) + '\n');
      setTimeout(() => {
        if (!settled) {
          settled = true;
          this.callbacks.delete(id);
          reject(new Error('mpv command timeout'));
        }
      }, 5000);
    });
  }

  async setProperty(name: string, value: unknown): Promise<void> {
    await this.command(['set_property', name, value]);
  }

  async getProperty(name: string): Promise<unknown> {
    return await this.command(['get_property', name]);
  }

  private cleanup(): void {
    if (this.socket) {
      this.socket.destroy();
      this.socket = null;
    }
    if (this.proc && !this.proc.killed) {
      try {
        this.proc.kill();
      } catch {
        /* ignore */
      }
    }
    this.proc = null;
    for (const [, cb] of this.callbacks) {
      cb.reject(new Error('mpv connection closed'));
    }
    this.callbacks.clear();
    this.buffer = '';
    this.isReady = false;
  }

  running(): boolean {
    return this.isReady && this.proc !== null && !this.proc.killed;
  }

  on(event: string, fn: EventCallback): void {
    const list = this.listeners.get(event) || [];
    list.push(fn);
    this.listeners.set(event, list);
  }

  off(event: string, fn: EventCallback): void {
    const list = this.listeners.get(event) || [];
    this.listeners.set(event, list.filter((f) => f !== fn));
  }

  private emit(event: string, data: unknown): void {
    (this.listeners.get(event) || []).forEach((fn) => fn(data));
  }
}

export const mpvManager = new MpvManager();
