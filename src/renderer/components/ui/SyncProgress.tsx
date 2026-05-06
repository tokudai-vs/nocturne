import { useEffect, useState, useRef } from 'react';
import { useSyncStore } from '../../stores/sync-store';
import type { SyncProgress as SyncProgressData, SyncStatus } from '../../api/types';
import styles from './SyncProgress.module.css';

const SIZE = 40;
const RADIUS = 17;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

export default function SyncProgress() {
  const { running, progress, completed, setProgress, setComplete, setError } = useSyncStore();
  const setStatus = useSyncStore((s) => s.setStatus);
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
    });
    const unsubError = window.api.sync.onError((err) => {
      setError(err.message);
    });

    return () => {
      unsubProgress();
      unsubComplete();
      unsubError();
    };
  }, [setProgress, setComplete, setError, setStatus]);

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

  if (!visible) return null;

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
