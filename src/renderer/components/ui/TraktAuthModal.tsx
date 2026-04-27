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
  const cancelled = useRef(false);

  const cleanup = useCallback(() => {
    if (pollTimer.current) clearTimeout(pollTimer.current);
    if (expireTimer.current) clearInterval(expireTimer.current);
    pollTimer.current = null;
    expireTimer.current = null;
  }, []);

  const beginPolling = useCallback(
    (d: TraktDeviceCode) => {
      let intervalSec = d.interval;
      const tick = async () => {
        if (cancelled.current) return;
        const res = await window.api.trakt.authPoll(d.device_code);
        if (cancelled.current) return;
        if (!res.success) {
          // Network/config error — keep retrying at the suggested interval.
          pollTimer.current = setTimeout(tick, intervalSec * 1000);
          return;
        }
        const state = res.data;
        if (state === 'success') {
          setStatus('success');
          cleanup();
          onSuccess();
          setTimeout(onClose, 800);
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
    [cleanup, onClose, onSuccess],
  );

  const startFlow = useCallback(async () => {
    cleanup();
    cancelled.current = false;
    setStatus('loading');
    setError(null);
    const res = await window.api.trakt.authStart();
    if (cancelled.current) return;
    if (!res.success || !res.data) {
      setStatus('error');
      setError(res.error ?? 'Failed to start authorization');
      return;
    }
    const d = res.data;
    setDevice(d);
    setSecondsLeft(d.expires_in);
    setStatus('waiting');
    expireTimer.current = setInterval(() => {
      setSecondsLeft((s) => {
        if (s <= 1) {
          setStatus('expired');
          cleanup();
          return 0;
        }
        return s - 1;
      });
    }, 1000);
    beginPolling(d);
  }, [beginPolling, cleanup]);

  useEffect(() => {
    startFlow();
    return () => {
      cancelled.current = true;
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
