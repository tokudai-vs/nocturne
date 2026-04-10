import styles from './RatingBadge.module.css';

interface Props {
  communityRating?: number;
  officialRating?: string;
  criticRating?: number;
}

function ratingColor(score: number): string {
  if (score >= 8) return '#4caf50';
  if (score >= 6) return '#e5a00d';
  return '#e5394b';
}

export default function RatingBadge({ communityRating, officialRating, criticRating }: Props) {
  return (
    <span className={styles.wrap}>
      {communityRating != null && communityRating > 0 && (
        <span className={styles.community} style={{ color: ratingColor(communityRating) }}>
          &#9733; {communityRating.toFixed(1)}
        </span>
      )}
      {officialRating && (
        <span className={styles.official}>{officialRating}</span>
      )}
      {criticRating != null && criticRating > 0 && (
        <span className={styles.critic}>&#127813; {criticRating}%</span>
      )}
    </span>
  );
}
