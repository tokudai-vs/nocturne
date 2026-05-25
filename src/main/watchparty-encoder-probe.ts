import { spawn } from 'child_process';
import type { Encoder, EncoderResult } from '../shared/watchparty-types';
import { watchPartyBinaryManager } from './watchparty-binary-manager';

export type { Encoder, EncoderResult } from '../shared/watchparty-types';

const PROBE_ORDER: Encoder[] = ['h264_nvenc', 'h264_qsv', 'h264_amf', 'libx264'];
const PROBE_TIMEOUT_MS = 5_000;

// -nostdin so a stray stdin close can't kill the probe; -hide_banner +
// -loglevel error keeps stderr scannable for the post-exit pattern check.
const COMMON_FLAGS = ['-y', '-nostdin', '-hide_banner', '-loglevel', 'error'];

// Catches the flaky-QSV "exit 0 with mid-stream error" case where the muxer
// flushes a few good frames before the encoder dies and ffmpeg still returns
// 0. A pure exit-code check lets those false positives through.
const ERROR_PATTERN = /(?:^|\s)(?:error|failed|cannot|unsupported|mfx_err_)/i;

class WatchPartyEncoderProbe {
  private cached: EncoderResult | null = null;
  private inflight: Promise<EncoderResult> | null = null;

  isProbed(): boolean {
    return this.cached !== null;
  }

  getCached(): EncoderResult | null {
    return this.cached;
  }

  // Unconditionally clears the cached result. If a probe is currently
  // in-flight, it WILL re-populate cached when it resolves — callers that
  // want a guaranteed fresh result should clearCache() then await probe().
  clearCache(): void {
    this.cached = null;
  }

  async probe(): Promise<EncoderResult> {
    if (this.cached) return this.cached;
    if (this.inflight) return this.inflight;
    this.inflight = this._doProbe().finally(() => {
      this.inflight = null;
    });
    return this.inflight;
  }

  // ── Internal pipeline ─────────────────────────────────────

  private async _doProbe(): Promise<EncoderResult> {
    if (!watchPartyBinaryManager.isReady()) {
      throw new Error('WatchParty binaries not ready — call ensureBinaries() first');
    }
    const ffmpegPath = watchPartyBinaryManager.getFfmpegPath();

    const available: Encoder[] = [];
    // Sequential: a hung GPU driver from one encoder must not race another
    // for the same device handle.
    for (const enc of PROBE_ORDER) {
      const ok = await this.spawnProbe(ffmpegPath, enc);
      if (ok) available.push(enc);
    }

    if (!available.includes('libx264')) {
      throw new Error(
        'libx264 probe failed — the ffmpeg build is broken (CPU fallback unavailable)',
      );
    }

    const preferred = available[0];
    const result: EncoderResult = { preferred, available, probedAt: Date.now() };
    this.cached = result;
    return result;
  }

  private spawnProbe(ffmpegPath: string, encoder: Encoder): Promise<boolean> {
    return new Promise((resolve) => {
      const proc = spawn(ffmpegPath, this.probeArgs(encoder), {
        stdio: ['ignore', 'ignore', 'pipe'],
        windowsHide: true,
      });
      let stderr = '';
      let settled = false;
      const timer = setTimeout(() => {
        if (settled) return;
        settled = true;
        try {
          proc.kill('SIGKILL');
        } catch {
          /* already exited */
        }
        resolve(false);
      }, PROBE_TIMEOUT_MS);
      proc.stderr?.on('data', (d: Buffer) => {
        stderr += d.toString();
      });
      proc.on('error', () => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(false);
      });
      proc.on('exit', (code) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(code === 0 && !ERROR_PATTERN.test(stderr));
      });
    });
  }

  // Per-encoder probe args. Differences from the trivial 1-frame probe are
  // load-bearing for QSV — see plan notes:
  // - 64x64 (some QSV drivers reject smaller widths)
  // - 2s of multi-GOP frame submission (1-frame probes false-positive)
  // - testsrc2 + -bf 2 -g 30 on QSV (forces B-frames + GOP rollover, which
  //   is where the flaky-driver path actually breaks)
  private probeArgs(enc: Encoder): string[] {
    switch (enc) {
      case 'h264_nvenc':
        return [
          ...COMMON_FLAGS,
          '-f', 'lavfi', '-i', 'color=c=black:s=64x64:r=30:d=2',
          '-t', '2', '-c:v', 'h264_nvenc', '-b:v', '500k',
          '-f', 'null', '-',
        ];
      case 'h264_qsv':
        return [
          ...COMMON_FLAGS,
          '-init_hw_device', 'qsv=hw', '-filter_hw_device', 'hw',
          '-f', 'lavfi', '-i', 'testsrc2=s=64x64:r=30:d=2',
          '-vf', 'hwupload=extra_hw_frames=64,format=qsv',
          '-t', '2', '-c:v', 'h264_qsv', '-bf', '2', '-g', '30', '-b:v', '500k',
          '-f', 'null', '-',
        ];
      case 'h264_amf':
        return [
          ...COMMON_FLAGS,
          '-f', 'lavfi', '-i', 'color=c=black:s=64x64:r=30:d=2',
          '-t', '2', '-c:v', 'h264_amf', '-b:v', '500k',
          '-f', 'null', '-',
        ];
      case 'libx264':
        return [
          ...COMMON_FLAGS,
          '-f', 'lavfi', '-i', 'color=c=black:s=64x64:r=30:d=2',
          '-t', '2', '-c:v', 'libx264', '-preset', 'ultrafast', '-b:v', '500k',
          '-f', 'null', '-',
        ];
    }
  }
}

export const watchPartyEncoderProbe = new WatchPartyEncoderProbe();
