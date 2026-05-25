import { useCallback, useState } from 'react';
import { Popcorn } from 'lucide-react';
import WatchPartyPreFlightModal from './WatchPartyPreFlightModal';
import WatchPartySetupModal from './WatchPartySetupModal';
import styles from './WatchPartyButton.module.css';

export default function WatchPartyButton() {
  const [setupOpen, setSetupOpen] = useState(false);
  const [preFlightOpen, setPreFlightOpen] = useState(false);

  // binariesReady() is intentionally NOT called on mount — it can trigger an
  // ~80MB sync re-hash on the stat-drift path. Only invoke on click.
  const handleClick = useCallback(async () => {
    try {
      const res = await window.api.watchparty.binariesReady();
      if (res.success && res.data === true) {
        setPreFlightOpen(true);
      } else {
        setSetupOpen(true);
      }
    } catch {
      // IPC rejected (bridge tear-down, main crashed, etc.) — treat as
      // not-ready so the user still gets the setup flow rather than a dead
      // click.
      setSetupOpen(true);
    }
  }, []);

  return (
    <>
      <button className={styles.button} onClick={handleClick}>
        <Popcorn size={16} /> Watch Party
      </button>
      {setupOpen && <WatchPartySetupModal onClose={() => setSetupOpen(false)} />}
      {preFlightOpen && <WatchPartyPreFlightModal onClose={() => setPreFlightOpen(false)} />}
    </>
  );
}
