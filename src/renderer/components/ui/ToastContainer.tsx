import { CheckCircle, XCircle, Info } from 'lucide-react';
import { useToastStore } from '../../stores/toast-store';
import styles from './ToastContainer.module.css';

const iconMap = {
  success: { Icon: CheckCircle, cls: styles.iconSuccess },
  error: { Icon: XCircle, cls: styles.iconError },
  info: { Icon: Info, cls: styles.iconInfo },
};

export default function ToastContainer() {
  const { toasts, removeToast } = useToastStore();

  if (toasts.length === 0) return null;

  return (
    <div className={styles.container}>
      {toasts.map((t) => {
        const { Icon, cls } = iconMap[t.type];
        return (
          <div key={t.id} className={`${styles.toast} ${styles[t.type]}`}>
            <Icon size={16} className={`${styles.icon} ${cls}`} />
            <span className={styles.message}>{t.message}</span>
            <button className={styles.dismiss} onClick={() => removeToast(t.id)}>&times;</button>
          </div>
        );
      })}
    </div>
  );
}
