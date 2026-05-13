import { useMemo, useState } from 'react';
import { buildImageUrl } from '../../utils/image-url';
import styles from './HeroBackdrop.module.css';

// Session-level cache of backdrop URLs known to fail. Shared with MediaCard
// conceptually but kept independent here — distinct image type, different
// URL space, no benefit from a unified set.
const KNOWN_BAD_BACKDROP_URLS = new Set<string>();

interface Props {
  itemId: string;
  tag?: string;
  /** Dedup-sibling backdrop URLs cycled when the primary fails. */
  backdropFallbacks?: string[];
  height?: string;
  children?: React.ReactNode;
}

export default function HeroBackdrop({ itemId, tag, backdropFallbacks, height = '55vh', children }: Props) {
  const chain = useMemo(() => {
    const primary = buildImageUrl(itemId, 'Backdrop', { maxWidth: 1920, tag });
    const all = (primary ? [primary, ...(backdropFallbacks ?? [])] : (backdropFallbacks ?? []))
      .filter((u) => u && !KNOWN_BAD_BACKDROP_URLS.has(u));
    return all;
  }, [itemId, tag, backdropFallbacks]);

  const [attempt, setAttempt] = useState(0);
  const [loaded, setLoaded] = useState(false);
  const [exhausted, setExhausted] = useState(false);

  const src = !exhausted && attempt < chain.length ? chain[attempt] : '';

  const onImgError = () => {
    const failed = chain[attempt];
    if (failed) KNOWN_BAD_BACKDROP_URLS.add(failed);
    if (attempt + 1 < chain.length) {
      // eslint-disable-next-line no-console
      console.log(`[image-fallback] hero ${itemId} backdrop failed, advancing to fallback ${attempt}/${chain.length - 1}`);
      setAttempt(attempt + 1);
    } else {
      // eslint-disable-next-line no-console
      console.log(`[image-fallback] hero ${itemId} all ${chain.length} backdrop URL(s) exhausted`);
      setExhausted(true);
    }
  };

  return (
    <div className={styles.hero} style={{ height }}>
      {src && (
        <img
          src={src}
          alt=""
          className={`${styles.img} ${loaded ? styles.imgLoaded : ''}`}
          onLoad={() => setLoaded(true)}
          onError={onImgError}
          key={src}
        />
      )}
      <div className={styles.overlay} />
      <div className={styles.content}>
        {children}
      </div>
    </div>
  );
}
