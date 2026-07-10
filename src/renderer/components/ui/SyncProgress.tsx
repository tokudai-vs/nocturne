import { useEffect, useState, useRef } from 'react';
import { AlertTriangle } from 'lucide-react';
import { useSyncStore } from '../../stores/sync-store';
import { useToastStore } from '../../stores/toast-store';
import type { SyncProgress as SyncProgressData, SyncStatus } from '../../api/types';
import styles from './SyncProgress.module.css';

const SIZE = 40;
const RADIUS = 17;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

// Which servers have already toasted this app session. Module-level so the
// "Couldn't sync X" toast fires at most once per server per launch, no matter
// how many times the component remounts.
const toastedServers = new Set<string>();

export default function SyncProgress() {
  const { running, progress, completed, serverErrors, setProgress, setComplete, setPartial, setError, setServerError } =
    useSyncStore();
  const setStatus = useSyncStore((s) => s.setStatus);
  const addToast = useToastStore((s) => s.addToast);
  const [visible, setVisible] = useState(false);
  const [fadeOut, setFadeOut] = useState(false);
  const fadeTimer = useRef<ReturnType<typeof setTimeout>>(undefined);

  // Subscribe to sync IPC events + bootstrap current state on mount.
  useEffect(() => {
    // One-shot bootstrap: if a sync was already in progress before this
    // component mounted (e.g., the auto-start kicked off in ProtectedRoute
    // before AppShell rendered), the renderer's `running` flag would stay
    // false until the next progress event. Pull the live status once so
    // banner / spinner reflect reality on cold start.
    void window.api.sync.getStatus().then((res) => {
      if (res.success && res.data) setStatus(res.data as SyncStatus);
    });

    const unsubProgress = window.api.sync.onProgress((data) => {
      setProgress(data as SyncProgressData);
    });
    const unsubComplete = window.api.sync.onComplete(() => {
      setComplete();
      // Re-pull status so serverHealth-derived serverErrors update after every
      // sync (a now-healthy server drops out; a still-failed one persists).
      void window.api.sync.getStatus().then((res) => {
        if (res.success && res.data) setStatus(res.data as SyncStatus);
      });
    });
    const unsubError = window.api.sync.onError((err) => {
      setError(err.message);
    });
    // Partial = full sync finished with >=1 failed server. It never emits
    // 'complete', so without this the ring would stick at its last percent
    // and the failure chip below could never render.
    const unsubPartial = window.api.sync.onPartial(() => {
      setPartial();
      void window.api.sync.getStatus().then((res) => {
        if (res.success && res.data) setStatus(res.data as SyncStatus);
      });
    });
    const unsubServerError = window.api.sync.onServerError((data) => {
      setServerError(data);
      if (!toastedServers.has(data.serverId)) {
        toastedServers.add(data.serverId);
        addToast(`Couldn't sync ${data.serverName} — showing cached data`, 'error');
      }
    });

    return () => {
      unsubProgress();
      unsubComplete();
      unsubError();
      unsubPartial();
      unsubServerError();
    };
  }, [setProgress, setComplete, setPartial, setError, setServerError, setStatus, addToast]);

  // Show/hide logic
  useEffect(() => {
    if (running) {
      setVisible(true);
      setFadeOut(false);
      if (fadeTimer.current) clearTimeout(fadeTimer.current);
    } else if (completed) {
      // Show checkmark briefly then fade out
      setVisible(true);
      setFadeOut(false);
      fadeTimer.current = setTimeout(() => {
        setFadeOut(true);
        fadeTimer.current = setTimeout(() => {
          setVisible(false);
          setFadeOut(false);
        }, 300);
      }, 2000);
    } else {
      setVisible(false);
    }

    return () => {
      if (fadeTimer.current) clearTimeout(fadeTimer.current);
    };
  }, [running, completed]);

  // When the progress ring is hidden, fall back to a persistent amber warning
  // chip while any server is in a failed state. The ring always wins while
  // visible (running / just-completed); the chip has no fade — it stays until
  // serverErrors clears (i.e. the next successful sync of that server).
  if (!visible) {
    const failedNames = Object.values(serverErrors).map((e) => e.serverName);
    if (failedNames.length > 0 && !running) {
      return (
        <div className={`${styles.container} ${styles.visible}`}>
          <div className={styles.tooltip}>
            Couldn't sync: {failedNames.join(', ')} — data may be stale
          </div>
          <div className={styles.warnChip}>
            <AlertTriangle size={20} />
          </div>
        </div>
      );
    }
    return null;
  }

  const percent = progress?.percent ?? 0;
  const offset = CIRCUMFERENCE * (1 - percent / 100);
  const tooltipText = progress?.detail || (completed ? 'Sync complete' : 'Syncing...');

  return (
    <div className={`${styles.container} ${visible ? styles.visible : ''} ${fadeOut ? styles.fadeOut : ''}`}>
      <div className={styles.tooltip}>{tooltipText}</div>
      <svg className={styles.ring} width={SIZE} height={SIZE}>
        <circle className={styles.trackCircle} cx={SIZE / 2} cy={SIZE / 2} r={RADIUS} />
        {completed ? (
          <>
            <circle
              className={styles.completeCircle}
              cx={SIZE / 2}
              cy={SIZE / 2}
              r={RADIUS}
              strokeDasharray={CIRCUMFERENCE}
              strokeDashoffset={0}
              transform={`rotate(-90 ${SIZE / 2} ${SIZE / 2})`}
            />
            <polyline
              className={styles.checkmark}
              points="14,20 18,24 26,16"
            />
          </>
        ) : (
          <>
            <circle
              className={styles.progressCircle}
              cx={SIZE / 2}
              cy={SIZE / 2}
              r={RADIUS}
              strokeDasharray={CIRCUMFERENCE}
              strokeDashoffset={offset}
              transform={`rotate(-90 ${SIZE / 2} ${SIZE / 2})`}
            />
            <text
              className={styles.label}
              x={SIZE / 2}
              y={SIZE / 2}
              textAnchor="middle"
              dominantBaseline="central"
            >
              {percent}%
            </text>
          </>
        )}
      </svg>
    </div>
  );
}
