import { WebSocketServer, WebSocket, type RawData } from 'ws';
import type { IncomingMessage } from 'http';
import type { Duplex } from 'stream';
import { randomUUID } from 'crypto';
import { watchPartyLogger } from './watchparty-logger';

// Wire protocol — host → guest. Mirrors docs/watch-party-session-architecture
// section 6. Discrete events (play/pause/seek) are applied immediately on
// receipt; heartbeats drive the deadzone-ladder drift corrector.
export type ServerToClientMessage =
  | {
      type: 'session_info';
      title: string;
      durationSec: number | null;
      sessionStartedAt: number;
      /** Movie-time offset where the transcode starts (t=0 of the playlist). */
      startOffsetSec: number;
    }
  | {
      type: 'state';
      state: 'WAITING' | 'LIVE' | 'ENDED';
      position: number;
      playing: boolean;
      serverTime: number;
    }
  | { type: 'play'; position: number; serverTime: number }
  | { type: 'pause'; position: number; serverTime: number }
  | { type: 'seek'; position: number; serverTime: number }
  | { type: 'heartbeat'; position: number; serverTime: number }
  | { type: 'session_end' };

export type ClientToServerMessage =
  | { type: 'join'; clientId?: string }
  | { type: 'ack'; clientId: string; drift: number }
  // Guest-side diagnostics. Packaged builds can't see the guest browser's
  // console, so hls.js fatals and starvation get posted back here and the
  // session manager routes them into session.log. Guest throttles stall
  // reports; the server additionally clamps the details string.
  | {
      type: 'client_error';
      kind: 'hls_fatal' | 'stall';
      details?: string;
      readyState?: number;
    };

interface Client {
  id: string;
  ws: WebSocket;
}

type ConnectionListener = (info: { client: Client; req: IncomingMessage }) => void;
type DisconnectionListener = (info: { client: Client }) => void;
type CountListener = (count: number) => void;
type MessageListener = (info: { client: Client; msg: ClientToServerMessage }) => void;

export class WatchPartySyncServer {
  private wss: WebSocketServer;
  private clients = new Map<WebSocket, Client>();
  private connectionCbs: ConnectionListener[] = [];
  private disconnectionCbs: DisconnectionListener[] = [];
  private countCbs: CountListener[] = [];
  private messageCbs: MessageListener[] = [];

  constructor() {
    // noServer: true — we attach to the HTTP server's upgrade event below,
    // sharing the single port the cloudflared tunnel forwards. One origin
    // for HLS + WebSocket means one tunnel total.
    this.wss = new WebSocketServer({ noServer: true });
  }

  /** Route an 'upgrade' event from a shared http.Server. */
  handleUpgrade(req: IncomingMessage, socket: Duplex, head: Buffer): void {
    this.wss.handleUpgrade(req, socket, head, (ws) => {
      const client: Client = { id: randomUUID(), ws };
      this.clients.set(ws, client);

      ws.on('message', (data: RawData) => {
        let parsed: ClientToServerMessage | null = null;
        try {
          const text = typeof data === 'string' ? data : data.toString('utf8');
          parsed = JSON.parse(text) as ClientToServerMessage;
        } catch {
          return; // ignore malformed
        }
        if (!parsed || typeof parsed !== 'object' || typeof parsed.type !== 'string') return;
        for (const cb of this.messageCbs) {
          try {
            cb({ client, msg: parsed });
          } catch (err) {
            watchPartyLogger.error('sync', `message listener threw: ${err instanceof Error ? err.message : String(err)}`);
          }
        }
      });

      ws.on('close', () => this.handleClose(ws));
      ws.on('error', (err) => {
        watchPartyLogger.warn('sync', `client socket error: ${err.message}`);
        this.handleClose(ws);
      });

      for (const cb of this.connectionCbs) {
        try {
          cb({ client, req });
        } catch (err) {
          watchPartyLogger.error('sync', `connection listener threw: ${err instanceof Error ? err.message : String(err)}`);
        }
      }
      this.emitCount();
    });
  }

  /** Send to one specific client; silently drops if the socket is dead. */
  sendTo(client: Client, msg: ServerToClientMessage): void {
    if (client.ws.readyState !== WebSocket.OPEN) return;
    try {
      client.ws.send(JSON.stringify(msg));
    } catch (err) {
      watchPartyLogger.warn('sync', `sendTo failed: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  /** Send to every connected guest. */
  broadcast(msg: ServerToClientMessage): void {
    const payload = JSON.stringify(msg);
    for (const client of this.clients.values()) {
      if (client.ws.readyState !== WebSocket.OPEN) continue;
      try {
        client.ws.send(payload);
      } catch (err) {
        watchPartyLogger.warn('sync', `broadcast failed: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
  }

  getClientCount(): number {
    return this.clients.size;
  }

  onConnection(cb: ConnectionListener): void {
    this.connectionCbs.push(cb);
  }

  onDisconnection(cb: DisconnectionListener): void {
    this.disconnectionCbs.push(cb);
  }

  onCountChanged(cb: CountListener): void {
    this.countCbs.push(cb);
  }

  onMessage(cb: MessageListener): void {
    this.messageCbs.push(cb);
  }

  /** Close all client sockets and the WSS itself. Idempotent. */
  async close(): Promise<void> {
    // Snapshot first — closing a ws fires the 'close' listener which
    // mutates this.clients.
    const clients = Array.from(this.clients.values());
    for (const c of clients) {
      try {
        c.ws.close(1001, 'session_end');
      } catch {
        /* ignore */
      }
    }
    this.clients.clear();
    return new Promise((resolve) => {
      this.wss.close(() => resolve());
    });
  }

  private handleClose(ws: WebSocket): void {
    const client = this.clients.get(ws);
    if (!client) return;
    this.clients.delete(ws);
    for (const cb of this.disconnectionCbs) {
      try {
        cb({ client });
      } catch (err) {
        watchPartyLogger.error('sync', `disconnection listener threw: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
    this.emitCount();
  }

  private emitCount(): void {
    const n = this.clients.size;
    for (const cb of this.countCbs) {
      try {
        cb(n);
      } catch (err) {
        watchPartyLogger.error('sync', `count listener threw: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
  }
}
