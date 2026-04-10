import type { CSSProperties } from 'react';

interface SkeletonProps {
  width?: string | number;
  height?: string | number;
  borderRadius?: string | number;
  style?: CSSProperties;
  className?: string;
}

export default function Skeleton({ width, height, borderRadius = 4, style, className }: SkeletonProps) {
  return (
    <div
      className={`shimmer ${className ?? ''}`}
      style={{
        width,
        height,
        borderRadius,
        ...style,
      }}
    />
  );
}
