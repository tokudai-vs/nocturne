import styles from './MediaCardSkeleton.module.css';

interface Props {
  orientation?: 'portrait' | 'landscape';
}

export default function MediaCardSkeleton({ orientation = 'portrait' }: Props) {
  return (
    <div className={`${styles.card} ${orientation === 'landscape' ? styles.landscape : styles.portrait}`}>
      <div className={`shimmer ${styles.image}`} />
    </div>
  );
}
