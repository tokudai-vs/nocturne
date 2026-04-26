import styles from './Slider.module.css';

interface Props {
  value: number;
  min: number;
  max: number;
  step?: number;
  label?: string;
  onChange: (value: number) => void;
}

export default function Slider({ value, min, max, step = 1, label, onChange }: Props) {
  const pct = ((value - min) / (max - min)) * 100;

  return (
    <div className={styles.wrapper}>
      <input
        type="range"
        className={styles.slider}
        min={min}
        max={max}
        step={step}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        style={{ '--fill-pct': `${pct}%` } as React.CSSProperties}
      />
      <span className={styles.value}>{label ?? value}</span>
    </div>
  );
}
