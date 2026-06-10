import { useCallback, useState } from 'react';
import { useSettingsStore } from '../../stores/settings-store';
import type { NocturneSettings } from '../../api/types';
import styles from './WatchPartyUnlockModal.module.css';

// The three v3.5 Danger Zone toggles, sharing the guest-limit unlock's
// visual language (and its CSS module): TL;DR up top, risk sections with
// bold headings, an explicit acknowledgment paragraph, checkbox-gated
// confirm. The prose carries the safety weight — keep it specific to the
// risk, not boilerplate.

interface DangerModalProps {
  title: string;
  tldr: string;
  intro: string;
  sections: Array<{ heading: string; body: string }>;
  confirmLabel: string;
  settingKey: keyof NocturneSettings;
  onClose: () => void;
}

function DangerModal({ title, tldr, intro, sections, confirmLabel, settingKey, onClose }: DangerModalProps) {
  const updateSetting = useSettingsStore((s) => s.updateSetting);
  const [acknowledged, setAcknowledged] = useState(false);

  const handleConfirm = useCallback(() => {
    void updateSetting(settingKey, true);
    onClose();
  }, [updateSetting, settingKey, onClose]);

  return (
    <div className={styles.backdrop}>
      <div className={styles.modal}>
        <h2 className={styles.title}>{title}</h2>

        <div className={styles.tldrBox}>
          <span className={styles.tldrLabel}>TL;DR.</span> {tldr}
        </div>

        <p className={styles.intro}>{intro}</p>

        {sections.map((s) => (
          <p key={s.heading} className={styles.section}>
            <span className={styles.sectionHeading}>{s.heading}</span> {s.body}
          </p>
        ))}

        <p className={styles.ackParagraph}>
          By checking the box below, you acknowledge that you have read the above, understand
          the risks, and accept sole responsibility for your use of this feature.
        </p>

        <label className={styles.checkRow}>
          <input
            type="checkbox"
            checked={acknowledged}
            onChange={(e) => setAcknowledged(e.target.checked)}
          />
          <span>I have read and understand the above, and accept sole responsibility.</span>
        </label>

        <div className={styles.actions}>
          <button className={styles.cancelBtn} onClick={onClose}>Cancel</button>
          <button
            className={styles.confirmBtn}
            onClick={handleConfirm}
            disabled={!acknowledged}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

export function WatchParty4kSourceModal({ onClose }: { onClose: () => void }) {
  return (
    <DangerModal
      onClose={onClose}
      settingKey="watchPartyPrefer4kSource"
      title="Transcode From the 4K Source"
      confirmLabel="Use 4K Source"
      tldr="Transcoding from a 4K master is several times heavier than from 1080p, for a quality gain most viewers cannot point to. On borderline hardware it can stall the session for everyone."
      intro="By default, Watch Party feeds the 1080p version of an item to the transcoder — the lightest path that matches the output. This toggle makes it prefer the 4K version as the transcode input even when the output stays 1080p, trading host-side load for a marginally better downscale master."
      sections={[
        {
          heading: 'What this does.',
          body: 'When a 4K version of an item exists in your library, ffmpeg reads it instead of the 1080p version. Downscaling from a higher-resolution master can look slightly sharper than re-encoding the 1080p file, because you start from finer detail instead of compounding two generations of 1080p compression. Output resolution and guest bandwidth do not change.',
        },
        {
          heading: 'Transcode load.',
          body: 'Decoding 4K — commonly HEVC, often HDR — is roughly four times the pixel throughput of 1080p before encoding even starts. A modern GPU absorbs it, but the head-start buffer builds more slowly, and on borderline hardware the transcode can fall behind playback mid-session. When that happens the stream stalls for every guest at once.',
        },
        {
          heading: 'Bandwidth from your server.',
          body: 'The host downloads the source from Emby in real time while transcoding. 4K files commonly stream at 40–80 Mbps versus 10–20 Mbps for 1080p. If your connection to the Emby server cannot sustain the higher rate, the transcoder starves no matter how fast your GPU is.',
        },
        {
          heading: 'Quality reality check.',
          body: 'The improvement is marginal and mostly visible in fine texture during slow scenes. If you would not bet on telling the difference in a blind A/B at 1080p, leave this off and keep the headroom.',
        },
      ]}
    />
  );
}

export function WatchParty4kOutputModal({ onClose }: { onClose: () => void }) {
  return (
    <DangerModal
      onClose={onClose}
      settingKey="watchPartyAllow4kOutput"
      title="Enable 4K (2160p) Output"
      confirmLabel="Enable 4K Output"
      tldr="Each 4K guest pulls roughly 20–25 Mbps of sustained upload from your machine, with no fanout. Two guests saturate most residential uplinks; oversubscribe and everyone buffers."
      intro="This adds a 4K (2160p) ceiling to the pre-flight quality options. The ceiling is still capped by the source: output is 4K only when a 4K version of the item exists. Nocturne never upscales — with only a 1080p source, the output stays 1080p regardless of this setting."
      sections={[
        {
          heading: 'Bandwidth.',
          body: 'A 4K HLS stream runs about 20 Mbps per guest — four times the 1080p figure — and every guest is a separate proxy connection back to your machine, not a CDN. Three 4K guests means 60+ Mbps of sustained upload for the length of a movie. Most residential connections cannot do this, and the failure mode is everyone buffering at once.',
        },
        {
          heading: 'Transcode load.',
          body: 'Encoding 2160p is roughly four times the work of 1080p. Recent NVENC-class hardware sustains it faster than realtime; older GPUs and every software path will not. If the transcode falls behind playback, the session stalls and there is no recovery short of ending it.',
        },
        {
          heading: 'Guest requirements.',
          body: 'Each guest needs ~25 Mbps of stable downstream and a machine that can decode 4K H.264 in a browser tab without dropping frames. A single underpowered guest will spend the session buffering while everyone else watches.',
        },
        {
          heading: 'When it is worth it.',
          body: 'A small number of guests, a hardware encoder, and an upload link you have actually measured. If any of those is a guess, 1080p will look better in practice than a starving 4K stream.',
        },
      ]}
    />
  );
}

export function WatchPartyCpuEncoderModal({ onClose }: { onClose: () => void }) {
  return (
    <DangerModal
      onClose={onClose}
      settingKey="watchPartyAllowCpuEncoder"
      title="Enable Watch Party on CPU-Only Systems"
      confirmLabel="Enable Anyway"
      tldr="Software encoding usually cannot outrun playback. Sessions hosted on CPU-only machines are likely to stall, desync, and buffer for every guest. Intended for testing only."
      intro="Watch Party transcodes the whole file ahead of playback and depends on the encoder running faster than realtime so the buffer keeps growing. This machine has no hardware encoder (NVIDIA NVENC, Intel Quick Sync, or AMD AMF), so Watch Party is blocked by default. This toggle removes that block."
      sections={[
        {
          heading: 'Why this is blocked by default.',
          body: 'Hardware encoders transcode 1080p at three to five times realtime, so the buffer outruns playback within minutes. Software x264 on a typical desktop CPU manages roughly one to two times realtime — and less under any other load. When the margin between transcoded content and playback position reaches zero, the stream stalls in lockstep with the encoder.',
        },
        {
          heading: 'What failure looks like.',
          body: 'Playback catches up with the transcoded edge. The host and every guest stutter together, the drift corrector amplifies the chaos with seeks into unbuffered range, and the session degrades until someone gives up. There is no recovery short of ending the session.',
        },
        {
          heading: 'If you proceed anyway.',
          body: 'Keep it to 720p, short content, and one or two guests. Close everything else CPU-heavy. Treat any full-length movie as an experiment that will probably fail — this override exists for testing the pipeline, not for hosting movie night.',
        },
      ]}
    />
  );
}
