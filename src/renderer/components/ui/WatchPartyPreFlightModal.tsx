import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, Popcorn } from 'lucide-react';
import { useSettingsStore } from '../../stores/settings-store';
import { useToastStore } from '../../stores/toast-store';
import LoadingSpinner from './LoadingSpinner';
import Select from './Select';
import type { Encoder, EncoderResult } from '../../shared/watchparty-types';
import styles from './WatchPartyPreFlightModal.module.css';

type State = 'probing' | 'ready' | 'error';
type Quality = '1080p' | '720p';
type MaxGuests = number | 'unlimited';

const ENCODER_LABELS: Record<Encoder, string> = {
  h264_nvenc: 'NVIDIA (NVENC)',
  h264_qsv: 'Intel Quick Sync (QSV)',
  h264_amf: 'AMD (AMF)',
  libx264: 'CPU (libx264)',
};

const MBPS_PER_GUEST: Record<Quality, number> = {
  '1080p': 5,
  '720p': 2.5,
};

const LOCKED_CAP = 10;
const DEFAULT_GUESTS = 4;

interface Props {
  onClose: () => void;
}

export default function WatchPartyPreFlightModal({ onClose }: Props) {
  const addToast = useToastStore((s) => s.addToast);
  const unlocked = useSettingsStore((s) => s.settings?.watchPartyMaxGuestsUnlocked ?? false);
  const [state, setState] = useState<State>('probing');
  const [encoderResult, setEncoderResult] = useState<EncoderResult | null>(null);
  const [quality, setQuality] = useState<Quality>('1080p');
  const [maxGuests, setMaxGuests] = useState<MaxGuests>(DEFAULT_GUESTS);
  const [legalAccepted, setLegalAccepted] = useState(false);
  const [retryKey, setRetryKey] = useState(0);

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
        if (res.success) {
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

  const handleStart = useCallback(() => {
    addToast('Session manager coming in next module', 'info');
    onClose();
  }, [addToast, onClose]);

  const isCpu = encoderResult?.preferred === 'libx264';
  const canStart = state === 'ready' && legalAccepted;

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

        {state === 'ready' && encoderResult && (
          <>
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

            <div className={styles.section}>
              <div className={styles.fieldLabel}>Quality</div>
              <div className={styles.radioGroup}>
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
                Start Session
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
