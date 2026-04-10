import { useEffect, useState, useRef } from 'react';
import { useUiStore } from '../stores/ui-store';
import styles from './SplashScreen.module.css';

const MIN_DISPLAY_MS = 2500;

export default function SplashScreen() {
  const { splashVisible, dismissSplash } = useUiStore();
  const [fading, setFading] = useState(false);
  const timerDone = useRef(false);
  const dataDone = useRef(false);

  useEffect(() => {
    const timer = setTimeout(() => {
      timerDone.current = true;
      maybeExit();
    }, MIN_DISPLAY_MS);
    return () => clearTimeout(timer);
  }, []);

  // Listen for data readiness — the app fires a custom event when initial data loads
  useEffect(() => {
    const handler = () => {
      dataDone.current = true;
      maybeExit();
    };
    window.addEventListener('nocturne:ready', handler);
    return () => window.removeEventListener('nocturne:ready', handler);
  }, []);

  function maybeExit() {
    if (timerDone.current && dataDone.current) {
      setFading(true);
      setTimeout(() => dismissSplash(), 500);
    }
  }

  if (!splashVisible) return null;

  return (
    <div className={`${styles.splash} ${fading ? styles.hidden : ''}`}>
      <div className={styles.logo}>NOCTURNE</div>
      <div className={styles.dots}>
        <span className={styles.dot} />
        <span className={styles.dot} />
        <span className={styles.dot} />
      </div>
    </div>
  );
}
