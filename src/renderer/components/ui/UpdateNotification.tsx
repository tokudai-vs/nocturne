import { useState, useEffect } from 'react';
import type { UpdateStatus } from '../../api/types';
import styles from './UpdateNotification.module.css';

export default function UpdateNotification() {
  const [status, setStatus] = useState<UpdateStatus | null>(null);
  const [dismissed, setDismissed] = useState(false);

  useEffect(() => {
    // Get initial status
    window.api.updater.getStatus().then((res) => {
      if (res.success && res.data) setStatus(res.data as UpdateStatus);
    });

    // Listen for status changes
    const unsub = window.api.updater.onStatus((s) => {
      setStatus(s);
      setDismissed(false);
    });
    return unsub;
  }, []);

  if (dismissed || !status) return null;
  if (status.state === 'idle' || status.state === 'checking' || status.state === 'error') return null;

  return (
    <div className={styles.bar}>
      {status.state === 'available' && (
        <>
          <span className={styles.message}>
            Update <span className={styles.version}>v{status.info?.version}</span> is available
          </span>
          <button className={styles.primaryBtn} onClick={() => window.api.updater.download()}>
            Download
          </button>
          <button className={styles.dismissBtn} onClick={() => setDismissed(true)}>
            Later
          </button>
        </>
      )}

      {status.state === 'downloading' && (
        <>
          <span className={styles.message}>Downloading update... {status.progress}%</span>
          <div className={styles.progressBar}>
            <div className={styles.progressFill} style={{ width: `${status.progress}%` }} />
          </div>
        </>
      )}

      {status.state === 'downloaded' && (
        <>
          <span className={styles.message}>
            Update <span className={styles.version}>v{status.info?.version}</span> ready to install
          </span>
          <button className={styles.primaryBtn} onClick={() => window.api.updater.install()}>
            Restart Now
          </button>
          <button className={styles.dismissBtn} onClick={() => setDismissed(true)}>
            Later
          </button>
        </>
      )}
    </div>
  );
}
