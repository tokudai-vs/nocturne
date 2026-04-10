import { useState, type ReactNode } from 'react';
import { buildImageUrl } from '../../utils/image-url';
import type { ImageType } from '../../api/types';

interface Props {
  itemId: string;
  imageType: ImageType;
  width?: number;
  height?: number;
  tag?: string;
  className?: string;
  fallback?: ReactNode;
  alt?: string;
}

const DefaultFallback = () => (
  <div
    style={{
      width: '100%',
      height: '100%',
      background: 'var(--bg-tertiary)',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      color: 'var(--text-muted)',
      fontSize: 24,
    }}
  >
    &#9654;
  </div>
);

export default function EmbyImage({ itemId, imageType, width, height, tag, className, fallback, alt = '' }: Props) {
  const [error, setError] = useState(false);
  const src = buildImageUrl(itemId, imageType, { maxWidth: width, maxHeight: height, tag });

  if (error || !src) {
    return <>{fallback ?? <DefaultFallback />}</>;
  }

  return (
    <img
      src={src}
      alt={alt}
      className={className}
      loading="lazy"
      onError={() => setError(true)}
    />
  );
}
