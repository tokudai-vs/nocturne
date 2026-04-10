import { useState } from 'react';
import { buildImageUrl } from '../../utils/image-url';
import styles from './PersonCard.module.css';

interface Props {
  id: string;
  name: string;
  role?: string;
  imageTag?: string;
}

export default function PersonCard({ id, name, role, imageTag }: Props) {
  const [imgError, setImgError] = useState(false);
  const src = imageTag && !imgError ? buildImageUrl(id, 'Primary', { maxWidth: 160, tag: imageTag }) : '';

  return (
    <div className={styles.card}>
      <div className={styles.avatar}>
        {src ? (
          <img src={src} alt={name} className={styles.img} onError={() => setImgError(true)} loading="lazy" />
        ) : (
          <span className={styles.initials}>{name.charAt(0)}</span>
        )}
      </div>
      <div className={styles.name}>{name}</div>
      {role && <div className={styles.role}>{role}</div>}
    </div>
  );
}
