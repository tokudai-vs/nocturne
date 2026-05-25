import { useCallback, useEffect, useRef, useState } from 'react';
import { Popcorn } from 'lucide-react';
import { useToastStore } from '../../stores/toast-store';
import styles from './WatchPartySetupModal.module.css';

const WHIMSY_LINES = [
  'Setting up the cinema...',
  'Hanging the screen...',
  'Rolling out the red carpet...',
  'Dimming the lights...',
  'Stocking the concession stand...',
  'Cueing the projector...',
  'Saving you the aisle seat...',
];

type Mode = 'downloading' | 'unzipping' | 'done' | 'error';

interface Props {
  onClose: () => void;
}

export default function WatchPartySetupModal({ onClose }: Props) {
  const addToast = useToastStore((s) => s.addToast);
  const [whimsyIdx, setWhimsyIdx] = useState(0);
  const [ffmpegPct, setFfmpegPct] = useState(0);
  const [cloudflaredPct, setCloudflaredPct] = useState(0);
  const [unzipBarPct, setUnzipBarPct] = useState(0);
  const [mode, setMode] = useState<Mode>('downloading');
  const [retryKey, setRetryKey] = useState(0);

  const cancelledRef = useRef(false);
  const unzipTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const modeRef = useRef<Mode>(mode);
  modeRef.current = mode;
  // Snapshot of bar position when unzip starts; gives the interpolation a
  // stable starting value regardless of later state updates.
  const downloadBarPctRef = useRef(0);
  downloadBarPctRef.current = (ffmpegPct * 0.67 + cloudflaredPct * 0.33) * 0.85;
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;
  // Hold the original IPC promise from the in-flight ensure. Cancel attaches
  // a fresh .then() to THIS exact promise rather than calling setupBinaries()
  // again — avoids a microtask race where the inflight rejects between
  // cancel-click and a second call landing on main, which would otherwise
  // start a new download.
  const setupPromiseRef = useRef<ReturnType<typeof window.api.watchparty.setupBinaries> | null>(
    null,
  );

  // Whimsy rotation
  useEffect(() => {
    const id = setInterval(() => {
      setWhimsyIdx((i) => (i + 1) % WHIMSY_LINES.length);
    }, 4000);
    return () => clearInterval(id);
  }, []);

  // Setup pipeline + subscriptions. Re-runs on Retry via retryKey.
  useEffect(() => {
    cancelledRef.current = false;
    setMode('downloading');
    setFfmpegPct(0);
    setCloudflaredPct(0);
    setUnzipBarPct(0);

    const offProgress = window.api.watchparty.onSetupProgress((data) => {
      if (cancelledRef.current) return;
      if (data.phase === 'ffmpeg') {
        setFfmpegPct(data.percent);
      } else if (data.phase === 'cloudflared') {
        setCloudflaredPct(data.percent);
      } else if (data.phase === 'unzip' && data.percent === -1) {
        if (modeRef.current !== 'downloading') return;
        setMode('unzipping');
        const startPct = downloadBarPctRef.current;
        const startTime = Date.now();
        if (unzipTimerRef.current) clearInterval(unzipTimerRef.current);
        unzipTimerRef.current = setInterval(() => {
          const elapsed = Date.now() - startTime;
          const t = Math.min(1, elapsed / 8000);
          setUnzipBarPct(startPct + (99 - startPct) * t);
          if (t >= 1 && unzipTimerRef.current) {
            clearInterval(unzipTimerRef.current);
            unzipTimerRef.current = null;
          }
        }, 100);
      }
    });

    const offError = window.api.watchparty.onSetupError(() => {
      if (cancelledRef.current) return;
      if (unzipTimerRef.current) {
        clearInterval(unzipTimerRef.current);
        unzipTimerRef.current = null;
      }
      setMode('error');
    });

    const p = window.api.watchparty.setupBinaries();
    setupPromiseRef.current = p;
    void p.then((res) => {
      if (cancelledRef.current) return;
      if (res.success) {
        if (unzipTimerRef.current) {
          clearInterval(unzipTimerRef.current);
          unzipTimerRef.current = null;
        }
        setMode('done');
        setTimeout(() => {
          if (!cancelledRef.current) onCloseRef.current();
        }, 300);
      } else if (modeRef.current !== 'error') {
        // Defensive: IPC-level failure that didn't also emit a setup-error
        // event. Prevents a hung modal.
        setMode('error');
      }
    }).catch(() => {
      // IPC bridge itself rejected (shouldn't happen — ok/fail wraps the
      // handler — but if it does, surface as error rather than leaving the
      // modal spinning forever).
      if (cancelledRef.current) return;
      if (modeRef.current !== 'error') setMode('error');
    });

    return () => {
      cancelledRef.current = true;
      offProgress();
      offError();
      if (unzipTimerRef.current) {
        clearInterval(unzipTimerRef.current);
        unzipTimerRef.current = null;
      }
    };
  }, [retryKey]);

  const handleCancel = useCallback(() => {
    cancelledRef.current = true;
    // Re-attach to the same in-flight IPC promise. ToastContainer lives in
    // AppShell, so the toast fires even after this modal unmounts.
    if (setupPromiseRef.current) {
      void setupPromiseRef.current
        .then((res) => {
          if (res.success) {
            addToast('Watch Party is ready! 🍿', 'success');
          } else {
            addToast('Watch Party setup failed', 'error');
          }
        })
        .catch(() => {
          addToast('Watch Party setup failed', 'error');
        });
    }
    onClose();
  }, [addToast, onClose]);

  const handleRetry = useCallback(() => {
    setRetryKey((k) => k + 1);
  }, []);

  const visualBarPct =
    mode === 'done'
      ? 100
      : mode === 'unzipping'
        ? unzipBarPct
        : (ffmpegPct * 0.67 + cloudflaredPct * 0.33) * 0.85;

  return (
    <div className={styles.backdrop}>
      <div className={styles.modal}>
        <div className={styles.iconWrap}>
          <Popcorn size={64} className={styles.icon} />
        </div>
        <h2 className={styles.title}>Setting up Watch Party</h2>

        {mode === 'error' ? (
          <>
            <p className={styles.error}>
              Couldn&apos;t set up Watch Party. Check your internet connection and try again.
            </p>
            <div className={styles.actions}>
              <button className={styles.cancelBtn} onClick={onClose}>Close</button>
              <button className={styles.retryBtn} onClick={handleRetry}>Retry</button>
            </div>
          </>
        ) : (
          <>
            <p className={styles.subtitle}>{WHIMSY_LINES[whimsyIdx]}</p>
            <div className={styles.cancelRow}>
              <button className={styles.cancelBtn} onClick={handleCancel}>Cancel</button>
            </div>
            <div className={styles.progressBar}>
              <div
                className={styles.progressFill}
                style={{ width: `${Math.min(100, Math.max(0, visualBarPct))}%` }}
              />
            </div>
          </>
        )}
      </div>
    </div>
  );
}
