// Pinned versions, download URLs, and SHA256 hashes for the binaries the
// Watch Party feature shells out to. Bumping a version means: update the
// version constant, regenerate the URL, paste the new SHA256 from the
// upstream checksum source, and re-test ensureBinaries() against a clean
// userData/bin/ so the hash-mismatch → re-download path is exercised.

export interface WatchPartyBinaryPin {
  name: string; // human-readable for the renderer status panel
  version: string;
  url: string;
  sha256: string;
}

const FFMPEG_VERSION = '8.1.1';
const CLOUDFLARED_VERSION = '2026.5.0';

export const FFMPEG_PIN: WatchPartyBinaryPin = {
  name: 'FFmpeg (essentials build)',
  version: FFMPEG_VERSION,
  url: `https://github.com/GyanD/codexffmpeg/releases/download/${FFMPEG_VERSION}/ffmpeg-${FFMPEG_VERSION}-essentials_build.zip`,
  sha256: '6f58ce889f59c311410f7d2b18895b33c03456463486f3b1ebc93d97a0f54541',
};

export const CLOUDFLARED_PIN: WatchPartyBinaryPin = {
  name: 'cloudflared',
  version: CLOUDFLARED_VERSION,
  url: `https://github.com/cloudflare/cloudflared/releases/download/${CLOUDFLARED_VERSION}/cloudflared-windows-amd64.exe`,
  sha256: 'f141cded099c239171ad2cea6fb5da0fdaa2bd36104c3074d883f9546519eba7',
};

// Expected top-level folder name inside the ffmpeg zip. The binary manager
// looks here first, then falls back to a recursive walk if missing — defense
// against future GyanD repackages that might rename the top folder.
export const FFMPEG_EXPECTED_ZIP_TOPLEVEL = `ffmpeg-${FFMPEG_VERSION}-essentials_build`;

// Bump when the on-disk manifest format changes incompatibly. isReady()
// treats a schema mismatch as "binaries not ready" and forces re-download.
export const WATCHPARTY_MANIFEST_SCHEMA_VERSION = 1;
