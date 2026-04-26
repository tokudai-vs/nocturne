import type { ReactNode } from 'react';
import styles from './SettingsRow.module.css';

interface Props {
  label: string;
  description?: string;
  children: ReactNode;
}

export default function SettingsRow({ label, description, children }: Props) {
  return (
    <div className={styles.row}>
      <div className={styles.labelGroup}>
        <div className={styles.label}>{label}</div>
        {description && <div className={styles.description}>{description}</div>}
      </div>
      <div className={styles.control}>{children}</div>
    </div>
  );
}
