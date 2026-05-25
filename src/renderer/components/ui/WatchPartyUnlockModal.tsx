import { useCallback, useState } from 'react';
import { useSettingsStore } from '../../stores/settings-store';
import styles from './WatchPartyUnlockModal.module.css';

interface Props {
  onClose: () => void;
}

export default function WatchPartyUnlockModal({ onClose }: Props) {
  const updateSetting = useSettingsStore((s) => s.updateSetting);
  const [acknowledged, setAcknowledged] = useState(false);

  const handleConfirm = useCallback(() => {
    void updateSetting('watchPartyMaxGuestsUnlocked', true);
    onClose();
  }, [updateSetting, onClose]);

  return (
    <div className={styles.backdrop}>
      <div className={styles.modal}>
        <h2 className={styles.title}>Remove Watch Party Guest Limit</h2>

        <div className={styles.tldrBox}>
          <span className={styles.tldrLabel}>TL;DR.</span> Increasing guest count past 10 may
          expose you to copyright liability, service termination, and degraded streaming
          quality. Use at your own risk.
        </div>

        <p className={styles.intro}>
          Watch Party limits sessions to 10 guests by default to keep usage within
          personal-scale norms. Removing this limit allows sessions of any size and materially
          changes your legal and operational exposure.
        </p>

        <p className={styles.section}>
          <span className={styles.sectionHeading}>Copyright liability.</span> In most
          jurisdictions, streaming personal-library content to a small group of
          personally-invited friends generally falls within private performance or comparable
          doctrines. Streaming the same content to larger audiences — particularly content for
          which you do not hold distribution rights — may constitute public performance or
          unauthorized broadcast, which carries materially higher legal risk including
          statutory damages and civil liability. This risk applies regardless of whether you
          charge guests or advertise the session. Scale alone can change the legal character
          of an otherwise-permissible private viewing. Nocturne provides no legal review, no
          copyright clearance, and no indemnification.
        </p>

        <p className={styles.section}>
          <span className={styles.sectionHeading}>Service termination.</span> Watch Party uses
          Cloudflare Quick Tunnels (trycloudflare.com), intended for personal, small-scale
          workloads under Cloudflare&apos;s terms of service. Larger sessions may trigger
          fair-use enforcement, terminating your session without notice.
        </p>

        <p className={styles.section}>
          <span className={styles.sectionHeading}>Bandwidth.</span> Each 1080p guest consumes
          approximately 5 Mbps of upload bandwidth with no fanout — each guest is a separate
          proxy connection back to your machine. Ten guests requires approximately 50 Mbps
          sustained upload. Twenty guests requires gigabit-class upload. Most residential
          connections cannot sustain this.
        </p>

        <p className={styles.section}>
          <span className={styles.sectionHeading}>Quality.</span> Sync drift and buffering
          compound with guest count. Past ten concurrent guests, expect meaningfully degraded
          playback for everyone watching.
        </p>

        <p className={styles.ackParagraph}>
          By checking the box below, you acknowledge that you have read the above, understand
          the legal, technical, and quality risks, and accept sole responsibility for your use
          of this feature.
        </p>

        <label className={styles.checkRow}>
          <input
            type="checkbox"
            checked={acknowledged}
            onChange={(e) => setAcknowledged(e.target.checked)}
          />
          <span>I have read and understand the above, and accept sole responsibility.</span>
        </label>

        <div className={styles.actions}>
          <button className={styles.cancelBtn} onClick={onClose}>Cancel</button>
          <button
            className={styles.confirmBtn}
            onClick={handleConfirm}
            disabled={!acknowledged}
          >
            Remove Limit
          </button>
        </div>
      </div>
    </div>
  );
}
