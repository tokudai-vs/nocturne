import { AlertTriangle } from 'lucide-react';
import { usePlayerStore } from '../../stores/player-store';
import styles from './MpvErrorModal.module.css';

export default function MpvErrorModal() {
  const { error, setError } = usePlayerStore();

  if (error !== 'mpv_not_found') return null;

  const handleDownload = () => {
    window.api.window.openExternal('https://mpv.io/installation/');
  };

  return (
    <div className={styles.backdrop} onClick={() => setError(null)}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.icon}><AlertTriangle size={40} /></div>
        <div className={styles.title}>mpv Not Found</div>
        <p className={styles.text}>
          The bundled mpv player could not be found. Try reinstalling the
          application, or install mpv manually and add it to your system PATH.
        </p>
        <div className={styles.actions}>
          <button className={styles.downloadBtn} onClick={handleDownload}>
            Download mpv
          </button>
          <button className={styles.closeBtn} onClick={() => setError(null)}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
