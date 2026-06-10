import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { AlertTriangle, Popcorn } from 'lucide-react';
import { useSettingsStore } from '../../stores/settings-store';
import { useToastStore } from '../../stores/toast-store';
import LoadingSpinner from './LoadingSpinner';
import Select from './Select';
import type { Encoder, EncoderResult, WatchPartySource } from '../../../shared/watchparty-types';
import { selectWatchPartySource } from '../../../shared/watchparty-types';
import styles from './WatchPartyPreFlightModal.module.css';

type State = 'probing' | 'ready' | 'error';
type Quality = '2160p' | '1080p' | '720p';
type MaxGuests = number | 'unlimited';
type StartFrom = 'beginning' | 'resume';

function formatHMS(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const pad = (n: number) => String(n).padStart(2, '0');
  if (h > 0) return `${h}:${pad(m)}:${pad(sec)}`;
  return `${m}:${pad(sec)}`;
}

const ENCODER_LABELS: Record<Encoder, string> = {
  h264_nvenc: 'NVIDIA (NVENC)',
  h264_qsv: 'Intel Quick Sync (QSV)',
  h264_amf: 'AMD (AMF)',
  libx264: 'CPU (libx264)',
};

// Mirrors the transcoder's bitrate ladder (bitrateForHeight) — keep in
// lockstep so the estimate matches what ffmpeg actually pushes.
const MBPS_PER_GUEST: Record<Quality, number> = {
  '2160p': 20,
  '1080p': 5,
  '720p': 2.5,
};

const LOCKED_CAP = 10;
const DEFAULT_GUESTS = 4;

interface Props {
  source: WatchPartySource;
  onClose: () => void;
}

export default function WatchPartyPreFlightModal({ source, onClose }: Props) {
  const navigate = useNavigate();
  const addToast = useToastStore((s) => s.addToast);
  const unlocked = useSettingsStore((s) => s.settings?.watchPartyMaxGuestsUnlocked ?? false);
  const prefer4kSource = useSettingsStore((s) => s.settings?.watchPartyPrefer4kSource ?? false);
  const allow4kOutput = useSettingsStore((s) => s.settings?.watchPartyAllow4kOutput ?? false);
  const allowCpuEncoder = useSettingsStore((s) => s.settings?.watchPartyAllowCpuEncoder ?? false);
  const [state, setState] = useState<State>('probing');
  const [encoderResult, setEncoderResult] = useState<EncoderResult | null>(null);
  const [quality, setQuality] = useState<Quality>('1080p');
  const [maxGuests, setMaxGuests] = useState<MaxGuests>(DEFAULT_GUESTS);
  const [legalAccepted, setLegalAccepted] = useState(false);
  const [retryKey, setRetryKey] = useState(0);
  const [starting, setStarting] = useState(false);
  // Start-from + history controls. The radio only renders when there's a
  // saved resume position; the toggle is always visible.
  const hasResume = (source.resumeSec ?? 0) > 0;
  const [startFrom, setStartFrom] = useState<StartFrom>('beginning');
  const [trackHistory, setTrackHistory] = useState(true);

  useEffect(() => {
    console.log('[wp:preflight] mounted', {
      title: source.title,
      versionCount: source.versions.length,
      durationSec: source.durationSec,
      resumeSec: source.resumeSec,
      hasResume,
    });
    // hasResume is derived from source.resumeSec; logging it once on mount
    // is enough — the source prop is stable for the modal's lifetime.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Mirrors the pick the session manager will make: the 4K-source Danger
  // Zone toggle flips the preference, and a 2160p ceiling implies it.
  // Keeping the "Sharing: …" line honest about what actually streams.
  const selectedVersion = useMemo(
    () =>
      selectWatchPartySource(source.versions, {
        prefer4kSource: prefer4kSource || quality === '2160p',
      }),
    [source.versions, prefer4kSource, quality],
  );
  const has4kVersion = useMemo(
    () => source.versions.some((v) => v.widthPx >= 3840),
    [source.versions],
  );

  const guestOptions = useMemo(() => {
    const numeric = Array.from({ length: LOCKED_CAP }, (_, i) => {
      const n = i + 1;
      return { value: String(n), label: String(n) };
    });
    return unlocked
      ? [...numeric, { value: 'unlimited', label: 'Unlimited' }]
      : numeric;
  }, [unlocked]);

  const cancelledRef = useRef(false);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    cancelledRef.current = false;
    setState('probing');
    setEncoderResult(null);

    void window.api.watchparty
      .probeEncoder()
      .then((res) => {
        if (cancelledRef.current) return;
        if (res.success && res.data) {
          setEncoderResult(res.data);
          setState('ready');
        } else {
          setState('error');
        }
      })
      .catch(() => {
        if (cancelledRef.current) return;
        setState('error');
      });

    return () => {
      cancelledRef.current = true;
    };
  }, [retryKey]);

  const handleCancel = useCallback(() => {
    cancelledRef.current = true;
    onClose();
  }, [onClose]);

  const handleRetry = useCallback(() => {
    setRetryKey((k) => k + 1);
  }, []);

  const handleStart = useCallback(async () => {
    if (starting) return;
    setStarting(true);
    const startOffsetSec =
      startFrom === 'resume' && hasResume ? Math.floor(source.resumeSec ?? 0) : 0;
    const res = await window.api.watchparty.startSession({
      source,
      durationSec: source.durationSec ?? 0,
      maxGuests,
      qualityHeight: quality === '720p' ? 720 : quality === '2160p' ? 2160 : 1080,
      startOffsetSec,
      trackHistory,
    });
    if (res.success && res.data) {
      onClose();
      // The host page subscribes to onState; it'll bind to the current
      // INITIALIZING → WAITING flow automatically when it mounts.
      navigate('/watch-party');
    } else {
      setStarting(false);
      addToast(`Start failed: ${res.error ?? 'unknown'}`, 'error');
    }
  }, [source, maxGuests, quality, startFrom, hasResume, trackHistory, starting, addToast, navigate, onClose]);

  const isCpu = encoderResult?.preferred === 'libx264';
  // No hardware encoder + no Danger Zone override = blocked. The session
  // manager enforces this server-side too; this is the friendly half.
  const cpuBlocked = isCpu && !allowCpuEncoder;
  const canStart = state === 'ready' && !cpuBlocked && legalAccepted && !starting;

  return (
    <div className={styles.backdrop}>
      <div className={styles.modal}>
        <div className={styles.iconWrap}>
          <Popcorn size={32} className={styles.icon} />
        </div>
        <h2 className={styles.title}>Watch Party</h2>

        {state === 'probing' && (
          <>
            <div className={styles.probingState}>
              <LoadingSpinner size={32} />
              <p className={styles.probingCaption}>Detecting hardware encoder…</p>
            </div>
            <div className={styles.actions}>
              <button className={styles.cancelBtn} onClick={handleCancel}>Cancel</button>
            </div>
          </>
        )}

        {state === 'error' && (
          <>
            <p className={styles.errorMsg}>Could not detect encoder.</p>
            <div className={styles.actions}>
              <button className={styles.cancelBtn} onClick={handleCancel}>Cancel</button>
              <button className={styles.startBtn} onClick={handleRetry}>Retry</button>
            </div>
          </>
        )}

        {state === 'ready' && encoderResult && cpuBlocked && (
          <>
            <div className={styles.blockedBox}>
              <h3 className={styles.blockedHeading}>
                <AlertTriangle size={16} /> Hardware encoder required
              </h3>
              <p className={styles.blockedBody}>
                Watch Party transcodes the whole file ahead of playback and needs a hardware
                encoder (NVIDIA NVENC, Intel Quick Sync, or AMD AMF) to stay ahead of it.
                This machine only offers software encoding (libx264), which usually cannot
                keep pace — sessions would stall and fall out of sync for every guest.
              </p>
              <p className={styles.blockedBody}>
                To run a session anyway — for testing — enable “Watch Party on CPU-only
                systems” under Settings → Danger Zone.
              </p>
            </div>
            <div className={styles.actions}>
              <button className={styles.cancelBtn} onClick={handleCancel}>Close</button>
              <button
                className={styles.startBtn}
                onClick={() => {
                  onClose();
                  navigate('/settings');
                }}
              >
                Open Settings
              </button>
            </div>
          </>
        )}

        {state === 'ready' && encoderResult && !cpuBlocked && (
          <>
            <div className={styles.section}>
              <p className={styles.encoderLine}>
                Sharing: {source.title} {'·'} {selectedVersion.qualityLabel}
              </p>
            </div>

            <div className={styles.section}>
              <p className={styles.encoderLine}>
                {isCpu ? (
                  <>
                    <AlertTriangle size={14} className={styles.warningIcon} />
                    Software encoding (CPU)
                  </>
                ) : (
                  <>Hardware encoder: {ENCODER_LABELS[encoderResult.preferred]}</>
                )}
              </p>
            </div>

            {hasResume && (
              <div className={styles.section}>
                <div className={styles.fieldLabel}>Start from</div>
                <div className={styles.radioGroup}>
                  <label className={styles.radioOption}>
                    <input
                      type="radio"
                      name="watchparty-startfrom"
                      value="beginning"
                      checked={startFrom === 'beginning'}
                      onChange={() => setStartFrom('beginning')}
                    />
                    <span>From the beginning</span>
                  </label>
                  <label className={styles.radioOption}>
                    <input
                      type="radio"
                      name="watchparty-startfrom"
                      value="resume"
                      checked={startFrom === 'resume'}
                      onChange={() => setStartFrom('resume')}
                    />
                    <span>Resume from {formatHMS(source.resumeSec ?? 0)}</span>
                  </label>
                </div>
              </div>
            )}

            <div className={styles.section}>
              <div className={styles.fieldLabel}>Quality</div>
              <div className={styles.radioGroup}>
                {allow4kOutput && (
                  <label className={styles.radioOption}>
                    <input
                      type="radio"
                      name="watchparty-quality"
                      value="2160p"
                      checked={quality === '2160p'}
                      onChange={() => setQuality('2160p')}
                    />
                    <span>4K (2160p)</span>
                    <span className={styles.qualityNote}>
                      {has4kVersion
                        ? '~20 Mbps upload per guest'
                        : 'No 4K version of this item — output will match the source'}
                    </span>
                  </label>
                )}
                <label className={styles.radioOption}>
                  <input
                    type="radio"
                    name="watchparty-quality"
                    value="1080p"
                    checked={quality === '1080p'}
                    onChange={() => setQuality('1080p')}
                  />
                  <span>1080p (recommended)</span>
                  {isCpu && (
                    <span className={styles.qualityNote}>May cause stutter on this machine</span>
                  )}
                </label>
                <label className={styles.radioOption}>
                  <input
                    type="radio"
                    name="watchparty-quality"
                    value="720p"
                    checked={quality === '720p'}
                    onChange={() => setQuality('720p')}
                  />
                  <span>720p</span>
                </label>
              </div>
            </div>

            <div className={styles.section}>
              <div className={styles.fieldLabel}>Maximum guests</div>
              <Select
                value={maxGuests === 'unlimited' ? 'unlimited' : String(maxGuests)}
                options={guestOptions}
                onChange={(v) =>
                  setMaxGuests(v === 'unlimited' ? 'unlimited' : Number(v))
                }
              />
              {(() => {
                const rate = MBPS_PER_GUEST[quality];
                const isUnlimited = maxGuests === 'unlimited';
                const total = isUnlimited ? rate : rate * (maxGuests as number);
                // Unlimited is the unlocked Danger-Zone path; force accent+bold
                // regardless of the per-guest number to reinforce ongoing
                // responsibility instead of showing a calm muted gray.
                const valueClass = isUnlimited
                  ? styles.bandwidthValueBold
                  : total >= 50
                    ? styles.bandwidthValueBold
                    : total < 25
                      ? styles.bandwidthValueMuted
                      : styles.bandwidthValueAccent;
                return isUnlimited ? (
                  <p className={styles.guestNote}>
                    Bandwidth depends on guests connected (~
                    <span className={valueClass}>{rate} Mbps</span> each)
                  </p>
                ) : (
                  <p className={styles.guestNote}>
                    Estimated upload: <span className={valueClass}>{total} Mbps</span>
                  </p>
                );
              })()}
            </div>

            <div className={styles.section}>
              <label className={styles.toggleRow}>
                <input
                  type="checkbox"
                  checked={trackHistory}
                  onChange={(e) => setTrackHistory(e.target.checked)}
                />
                <span className={styles.toggleLabel}>
                  Add to my watch history
                  <span className={styles.toggleHint}>
                    Reports progress to Emby and scrobbles to Trakt as if you watched it solo.
                  </span>
                </span>
              </label>
            </div>

            <div className={styles.section}>
              <div className={styles.legalBox}>
                <div className={styles.legalHeading}>Before You Continue</div>
                <p className={styles.legalBody}>
                  Watch Party streams content from your computer to invited guests over an
                  encrypted tunnel. You are responsible for ensuring you have the right to share
                  any content you stream. Sharing copyrighted content without authorization may
                  violate copyright law in your jurisdiction.
                </p>
                <p className={styles.legalBody}>
                  By starting a session, you confirm that all viewers are people you have
                  personally invited and that you are not publicly broadcasting. Nocturne does
                  not log or record your content.
                </p>
                <label className={styles.legalCheckRow}>
                  <input
                    type="checkbox"
                    checked={legalAccepted}
                    onChange={(e) => setLegalAccepted(e.target.checked)}
                  />
                  <span>I understand and accept responsibility for how I use this feature</span>
                </label>
              </div>
            </div>

            <div className={styles.actions}>
              <button className={styles.cancelBtn} onClick={handleCancel}>Cancel</button>
              <button
                className={styles.startBtn}
                onClick={handleStart}
                disabled={!canStart}
              >
                {starting ? 'Starting…' : 'Start Session'}
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
