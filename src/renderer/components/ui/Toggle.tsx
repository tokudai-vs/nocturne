import styles from './Toggle.module.css';

interface Props {
  value: boolean;
  onChange: (value: boolean) => void;
}

export default function Toggle({ value, onChange }: Props) {
  return (
    <button
      className={`${styles.toggle} ${value ? styles.active : ''}`}
      onClick={() => onChange(!value)}
      type="button"
      role="switch"
      aria-checked={value}
    >
      <div className={styles.knob} />
    </button>
  );
}
