import { useCallback, useEffect, useRef, useState } from 'react';
import { Copy, ExternalLink } from 'lucide-react';
import type { TraktDeviceCode } from '../../api/types';
import styles from './TraktAuthModal.module.css';

type Status = 'loading' | 'waiting' | 'success' | 'expired' | 'denied' | 'error';

interface Props {
  onClose: () => void;
  onSuccess: () => void;
}

export default function TraktAuthModal({ onClose, onSuccess }: Props) {
  const [status, setStatus] = useState<Status>('loading');
  const [device, setDevice] = useState<TraktDeviceCode | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [secondsLeft, setSecondsLeft] = useState(0);
  const [copied, setCopied] = useState(false);
  const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const expireTimer = useRef<ReturnType<typeof setInterval> | null>(null);

  // Always-current refs for the callback props. SettingsPage passes inline
  // arrow functions whose identity changes on every parent re-render. If we
  // closed over `onClose` / `onSuccess` directly inside `useCallback`, the
  // entire effect chain (beginPolling → startFlow → useEffect) would re-fire
  // on every parent re-render and request a fresh device code each time —
  // which is the bug this guards against.
  const onCloseRef = useRef(onClose);
  const onSuccessRef = useRef(onSuccess);
  onCloseRef.current = onClose;
  onSuccessRef.current = onSuccess;

  // Generation counter used to invalidate in-flight async work. Each call to
  // startFlow bumps it and captures the new value into a local `myEpoch`; all
  // awaits compare-and-bail when the latest epoch no longer matches. The
  // mount cleanup also bumps it so:
  //   - React StrictMode's double-mount in dev doesn't race two authStart
  //     calls into setting modal state (only the latest wins);
  //   - clicking Try Again throws away any pending poll from the prior flow;
  //   - unmounting mid-flight makes the in-flight authStart resolve into a
  //     no-op instead of touching state on an unmounted component.
  const epoch = useRef(0);

  const cleanup = useCallback(() => {
    if (pollTimer.current) clearTimeout(pollTimer.current);
    if (expireTimer.current) clearInterval(expireTimer.current);
    pollTimer.current = null;
    expireTimer.current = null;
  }, []);

  const beginPolling = useCallback(
    (d: TraktDeviceCode, myEpoch: number) => {
      let intervalSec = d.interval;
      const tick = async () => {
        if (myEpoch !== epoch.current) return;
        const res = await window.api.trakt.authPoll(d.device_code);
        if (myEpoch !== epoch.current) return;
        if (!res.success) {
          // Network/config error — keep retrying at the suggested interval.
          pollTimer.current = setTimeout(tick, intervalSec * 1000);
          return;
        }
        const state = res.data;
        if (state === 'success') {
          setStatus('success');
          cleanup();
          onSuccessRef.current();
          setTimeout(() => onCloseRef.current(), 800);
          return;
        }
        if (state === 'expired') { setStatus('expired'); cleanup(); return; }
        if (state === 'denied') { setStatus('denied'); cleanup(); return; }
        if (state === 'slow_down') {
          intervalSec = Math.min(intervalSec * 2, 30);
        }
        pollTimer.current = setTimeout(tick, intervalSec * 1000);
      };
      pollTimer.current = setTimeout(tick, intervalSec * 1000);
    },
    [cleanup],
  );

  const startFlow = useCallback(async () => {
    cleanup();
    const myEpoch = ++epoch.current;
    setStatus('loading');
    setError(null);
    const res = await window.api.trakt.authStart();
    if (myEpoch !== epoch.current) return;
    if (!res.success || !res.data) {
      setStatus('error');
      setError(res.error ?? 'Failed to start authorization');
      return;
    }
    const d = res.data;
    setDevice(d);
    setSecondsLeft(d.expires_in);
    setStatus('waiting');
    // Capture the interval handle into a local so a stale tick (after a new
    // flow has bumped the epoch and replaced expireTimer.current) clears the
    // RIGHT interval rather than the new flow's active one.
    const interval = setInterval(() => {
      if (myEpoch !== epoch.current) {
        clearInterval(interval);
        return;
      }
      setSecondsLeft((s) => {
        if (s <= 1) {
          setStatus('expired');
          cleanup();
          return 0;
        }
        return s - 1;
      });
    }, 1000);
    expireTimer.current = interval;
    beginPolling(d, myEpoch);
  }, [beginPolling, cleanup]);

  useEffect(() => {
    void startFlow();
    return () => {
      // Bumping the epoch invalidates any in-flight authStart/authPoll and
      // any pending tick callbacks; cleanup() clears the active timers.
      ++epoch.current;
      cleanup();
    };
  }, [startFlow, cleanup]);

  const handleCopy = useCallback(() => {
    if (!device) return;
    navigator.clipboard.writeText(device.user_code).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  }, [device]);

  const handleOpenBrowser = useCallback(() => {
    if (!device) return;
    void window.api.trakt.openVerification(device.verification_url);
  }, [device]);

  const fmtTime = (s: number) => {
    const m = Math.floor(s / 60);
    const r = s % 60;
    return `${m}:${r.toString().padStart(2, '0')}`;
  };

  return (
    <div className={styles.backdrop} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <h2 className={styles.title}>Connect Trakt</h2>

        {status === 'loading' && (
          <div className={styles.loading}>Starting authorization…</div>
        )}

        {status === 'waiting' && device && (
          <>
            <ol className={styles.steps}>
              <li>
                Visit <strong>{device.verification_url}</strong> in your browser.
              </li>
              <li>
                Enter this code:
                <div className={styles.codeRow}>
                  <code className={styles.code}>{device.user_code}</code>
                  <button className={styles.iconBtn} onClick={handleCopy} title="Copy code">
                    <Copy size={14} />
                  </button>
                </div>
                {copied && <div className={styles.copied}>Copied!</div>}
              </li>
            </ol>
            <button className={styles.openBtn} onClick={handleOpenBrowser}>
              <ExternalLink size={14} /> Open in browser
            </button>
            <div className={styles.statusRow}>
              <span className={styles.dot} />
              Waiting for authorization… (expires in {fmtTime(secondsLeft)})
            </div>
          </>
        )}

        {status === 'success' && (
          <div className={styles.success}>Connected!</div>
        )}

        {status === 'expired' && (
          <>
            <div className={styles.error}>
              The code expired before authorization completed.
            </div>
            <button className={styles.retryBtn} onClick={startFlow}>Try again</button>
          </>
        )}

        {status === 'denied' && (
          <>
            <div className={styles.error}>Authorization was denied.</div>
            <button className={styles.retryBtn} onClick={startFlow}>Try again</button>
          </>
        )}

        {status === 'error' && (
          <>
            <div className={styles.error}>{error}</div>
            <button className={styles.retryBtn} onClick={startFlow}>Try again</button>
          </>
        )}

        <button className={styles.cancelBtn} onClick={onClose}>Cancel</button>
      </div>
    </div>
  );
}
