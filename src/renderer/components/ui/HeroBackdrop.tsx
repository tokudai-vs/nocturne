import { useState } from 'react';
import { buildImageUrl } from '../../utils/image-url';
import styles from './HeroBackdrop.module.css';

interface Props {
  itemId: string;
  tag?: string;
  height?: string;
  children?: React.ReactNode;
}

export default function HeroBackdrop({ itemId, tag, height = '55vh', children }: Props) {
  const [loaded, setLoaded] = useState(false);
  const src = buildImageUrl(itemId, 'Backdrop', { maxWidth: 1920, tag });

  return (
    <div className={styles.hero} style={{ height }}>
      {src && (
        <img
          src={src}
          alt=""
          className={`${styles.img} ${loaded ? styles.imgLoaded : ''}`}
          onLoad={() => setLoaded(true)}
        />
      )}
      <div className={styles.overlay} />
      <div className={styles.content}>
        {children}
      </div>
    </div>
  );
}
