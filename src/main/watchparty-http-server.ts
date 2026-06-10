import * as http from 'http';
import * as fs from 'fs';
import * as path from 'path';
import type { AddressInfo } from 'net';
import { GUEST_PAGE_HTML } from './watchparty-guest-page';
import { watchPartyLogger } from './watchparty-logger';
import type { WatchPartySyncServer } from './watchparty-sync-server';

// Strict allowlist for path traversal protection: only files matching these
// names are served from the session dir. The HLS playlist + segments are
// the only things that should ever be fetched from there.
const STREAM_FILE_RE = /^(stream\.m3u8|segment_\d{5}\.ts)$/;

const CONTENT_TYPES: Record<string, string> = {
  '.m3u8': 'application/vnd.apple.mpegurl',
  '.ts': 'video/mp2t',
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
};

// Resolve the bundled hls.min.js once at process startup. require.resolve
// against the package name (not a relative path) works through the
// externalize-deps pipeline AND through electron-builder's asar packaging
// — node walks node_modules from wherever main is loaded. Falls back to
// `hls.js` (the dev/unminified entry) if the minified twin is missing,
// which only happens on hand-edited node_modules.
function resolveHlsJsPath(): string {
  const mainEntry = require.resolve('hls.js');
  const minPath = path.join(path.dirname(mainEntry), 'hls.min.js');
  const picked = fs.existsSync(minPath) ? minPath : mainEntry;
  watchPartyLogger.info('http', `hls.js resolved to ${picked}`);
  return picked;
}

export interface HttpServerHandle {
  url: string;
  port: number;
  stop: () => Promise<void>;
}

export interface StartHttpServerOptions {
  sessionDir: string;
  /** Optional sync server — when provided, /ws upgrades route to it. */
  syncServer?: WatchPartySyncServer;
}

export function startHttpServer(opts: StartHttpServerOptions): Promise<HttpServerHandle> {
  const { sessionDir, syncServer } = opts;
  const hlsJsPath = (() => {
    try {
      return resolveHlsJsPath();
    } catch (err) {
      watchPartyLogger.error('http', `hls.js resolution failed: ${err instanceof Error ? err.message : String(err)}`);
      return null;
    }
  })();

  return new Promise((resolve, reject) => {
    const server = http.createServer((req, res) => {
      // Permissive CORS — cloudflared rewrites Origin, and we want
      // command-line tools (curl, vlc) to fetch for debugging.
      res.setHeader('Access-Control-Allow-Origin', '*');
      res.setHeader('Access-Control-Allow-Methods', 'GET, HEAD, OPTIONS');
      res.setHeader('Access-Control-Allow-Headers', '*');
      if (req.method === 'OPTIONS') {
        res.statusCode = 204;
        res.end();
        return;
      }
      if (req.method !== 'GET' && req.method !== 'HEAD') {
        res.statusCode = 405;
        res.end();
        return;
      }

      const url = new URL(req.url || '/', 'http://localhost');
      const pathname = url.pathname;

      // Guest landing page — the cloudflared origin lands here.
      if (pathname === '/' || pathname === '/index.html') {
        res.statusCode = 200;
        res.setHeader('Content-Type', CONTENT_TYPES['.html']);
        res.setHeader('Cache-Control', 'no-cache');
        res.end(GUEST_PAGE_HTML);
        return;
      }

      // Bundled hls.js — shipped via the app's node_modules so no CDN.
      if (pathname === '/hls.min.js' || pathname === '/hls.js') {
        if (!hlsJsPath) {
          res.statusCode = 500;
          res.end('hls.js not bundled');
          return;
        }
        serveStaticFile(hlsJsPath, '.js', req, res);
        return;
      }

      // HLS playlist + segments — strict allowlist.
      const fileName = pathname.replace(/^\/+/, '');
      if (!STREAM_FILE_RE.test(fileName)) {
        res.statusCode = 404;
        res.end();
        return;
      }
      const filePath = path.join(sessionDir, fileName);
      const ext = path.extname(fileName);
      serveStaticFile(filePath, ext, req, res);
    });

    // Share the port with WebSocket: route /ws upgrades to the sync server.
    // Single port = single cloudflared tunnel = simpler guest origin.
    if (syncServer) {
      server.on('upgrade', (req, socket, head) => {
        const url = new URL(req.url || '/', 'http://localhost');
        if (url.pathname === '/ws') {
          syncServer.handleUpgrade(req, socket, head);
        } else {
          socket.destroy();
        }
      });
    }

    server.on('error', (err) => reject(err));
    // Port 0 = OS picks a free port. Localhost-only — public exposure goes
    // through cloudflared (separate piece).
    server.listen(0, '127.0.0.1', () => {
      const addr = server.address() as AddressInfo;
      const port = addr.port;
      const url = `http://127.0.0.1:${port}`;
      // session manager already logs at INFO; this duplicate would only
      // help if the listen succeeded but the session log wasn't open yet.
      resolve({
        url,
        port,
        stop: () =>
          new Promise<void>((resolveStop) => {
            server.close(() => resolveStop());
            // close() waits for in-flight connections to drain. hls.js keeps
            // playlist polling open — closeAllConnections forces them shut
            // so stop() returns promptly.
            (server as unknown as { closeAllConnections?: () => void }).closeAllConnections?.();
          }),
      });
    });
  });
}

function serveStaticFile(
  filePath: string,
  ext: string,
  req: http.IncomingMessage,
  res: http.ServerResponse,
): void {
  const contentType = CONTENT_TYPES[ext] || 'application/octet-stream';
  fs.stat(filePath, (err, stat) => {
    if (err || !stat.isFile()) {
      res.statusCode = 404;
      res.end();
      return;
    }
    res.statusCode = 200;
    res.setHeader('Content-Type', contentType);
    res.setHeader('Content-Length', String(stat.size));
    // Playlist mutates while transcoding (event playlist grows). hls.min.js
    // is immutable but small. no-cache for everything is fine in batch 1.
    res.setHeader('Cache-Control', 'no-cache');
    if (req.method === 'HEAD') {
      res.end();
      return;
    }
    const stream = fs.createReadStream(filePath);
    stream.on('error', () => {
      if (!res.headersSent) res.statusCode = 500;
      res.end();
    });
    stream.pipe(res);
  });
}
