import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Film, Tv, Music, BookOpen, Archive, Folder, Star } from 'lucide-react';
import { useSettingsStore } from '../../stores/settings-store';
import { useSyncStore } from '../../stores/sync-store';
import { useLibraryStore } from '../../stores/library-store';
import type { LibraryMapping } from '../../api/types';
import styles from './LibraryMappingWizard.module.css';

const ICON_MAP: Record<string, React.ReactNode> = {
  Film: <Film size={16} />,
  Tv: <Tv size={16} />,
  Music: <Music size={16} />,
  BookOpen: <BookOpen size={16} />,
  Archive: <Archive size={16} />,
  Folder: <Folder size={16} />,
  Star: <Star size={16} />,
};

export default function LibraryMappingWizard() {
  const navigate = useNavigate();
  const { settings, updateSetting, updateMultiple } = useSettingsStore();
  const { completed: syncCompleted } = useSyncStore();
  const { views } = useLibraryStore();

  const [show, setShow] = useState(false);
  const [suggested, setSuggested] = useState<Record<string, LibraryMapping> | null>(null);
  const [dontShowAgain, setDontShowAgain] = useState(false);

  useEffect(() => {
    // Show wizard when: sync just completed AND firstLaunchComplete is false
    if (syncCompleted && settings && !settings.firstLaunchComplete) {
      loadSuggestions();
    }
  }, [syncCompleted, settings]);

  async function loadSuggestions() {
    const res = await window.api.libraries.suggestMapping();
    if (res.success) {
      setSuggested(res.data as Record<string, LibraryMapping>);
      setShow(true);
    }
  }

  function handleAccept() {
    if (suggested) {
      // Save mappings through server manager (per-server scoped)
      window.api.servers.setMappings(suggested);
      updateSetting('firstLaunchComplete', true);
    }
    setShow(false);
  }

  function handleCustomize() {
    updateSetting('firstLaunchComplete', true);
    setShow(false);
    navigate('/settings');
  }

  function handleDismiss() {
    if (dontShowAgain) {
      updateSetting('firstLaunchComplete', true);
    }
    setShow(false);
  }

  if (!show || !suggested) return null;

  // Build display data: resolve library IDs to names
  const viewMap = new Map(views.map((v) => [v.Id, v.Name]));
  const groups = Object.entries(suggested);

  return (
    <div className={styles.overlay} onClick={handleDismiss}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <h2 className={styles.title}>Organize Your Libraries</h2>
          <p className={styles.subtitle}>
            We found {views.length} libraries on your server. Here&apos;s a suggested grouping:
          </p>
        </div>

        <div className={styles.body}>
          {groups.map(([groupId, group]) => (
            <div key={groupId} className={styles.groupPreview}>
              <div className={styles.groupPreviewHeader}>
                <span className={styles.groupPreviewIcon}>
                  {ICON_MAP[group.icon] ?? <Folder size={16} />}
                </span>
                <span>{group.name}</span>
                <span className={styles.groupPreviewCount}>
                  {group.libraryIds.length} {group.libraryIds.length === 1 ? 'library' : 'libraries'}
                </span>
              </div>
              <div className={styles.groupPreviewLibs}>
                {group.libraryIds.map((id) => viewMap.get(id) ?? id).join(', ')}
              </div>
            </div>
          ))}
        </div>

        <div className={styles.footer}>
          <label className={styles.skipLabel}>
            <input
              type="checkbox"
              checked={dontShowAgain}
              onChange={(e) => setDontShowAgain(e.target.checked)}
            />
            Don&apos;t show this again
          </label>
          <div className={styles.footerButtons}>
            <button className={styles.btnSecondary} onClick={handleCustomize}>
              Customize in Settings
            </button>
            <button className={styles.btnPrimary} onClick={handleAccept}>
              Accept & Continue
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
