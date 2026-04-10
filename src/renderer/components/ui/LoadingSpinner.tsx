import styles from './LoadingSpinner.module.css';

interface Props {
  size?: number;
}

export default function LoadingSpinner({ size = 40 }: Props) {
  return (
    <div className={styles.wrapper}>
      <div className={styles.ring} style={{ width: size, height: size }} />
    </div>
  );
}
