import { useEffect, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { Home, Film, Tv, Music, BookOpen, Archive, Folder, Settings } from 'lucide-react';
import { useLibraryStore } from '../../stores/library-store';
import { useUiStore } from '../../stores/ui-store';
import styles from './SidebarOverlay.module.css';

const ICON_MAP: Record<string, React.ReactNode> = {
  home: <Home size={18} />,
  movies: <Film size={18} />,
  tvshows: <Tv size={18} />,
  music: <Music size={18} />,
  books: <BookOpen size={18} />,
  boxsets: <Archive size={18} />,
};

function iconForCollection(name: string): React.ReactNode {
  const key = name.toLowerCase().replace(/\s/g, '');
  return ICON_MAP[key] ?? <Folder size={18} />;
}

export default function SidebarOverlay() {
  const { sidebarOpen, closeSidebar } = useUiStore();
  const { views, fetchViews } = useLibraryStore();
  const [closing, setClosing] = useState(false);

  useEffect(() => {
    if (sidebarOpen && views.length === 0) fetchViews();
  }, [sidebarOpen, views.length, fetchViews]);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && sidebarOpen) handleClose();
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [sidebarOpen]);

  function handleClose() {
    setClosing(true);
    setTimeout(() => {
      closeSidebar();
      setClosing(false);
    }, 200);
  }

  function handleNav() {
    handleClose();
  }

  if (!sidebarOpen) return null;

  return (
    <div
      className={`${styles.overlay} ${closing ? styles.overlayOut : ''}`}
      onClick={(e) => {
        // Close if clicking the transparent area (right side)
        if ((e.target as HTMLElement).classList.contains(styles.overlay)) handleClose();
      }}
    >
      <nav className={styles.nav}>
        <div className={styles.logo}>NOCTURNE</div>
        <div className={styles.divider} />
        <div className={styles.navList}>
          <NavLink
            to="/"
            end
            className={({ isActive }) => `${styles.navItem} ${isActive ? styles.active : ''}`}
            onClick={handleNav}
          >
            <span className={styles.navIcon}><Home size={18} /></span>
            Home
          </NavLink>
          {views.map((v) => (
            <NavLink
              key={v.Id}
              to={`/library/${v.Id}`}
              className={({ isActive }) => `${styles.navItem} ${isActive ? styles.active : ''}`}
              onClick={handleNav}
            >
              <span className={styles.navIcon}>{iconForCollection(v.Name)}</span>
              {v.Name}
            </NavLink>
          ))}
        </div>
        <div className={styles.divider} />
        <button className={styles.navItem} onClick={() => { handleClose(); }}>
          <span className={styles.navIcon}><Settings size={18} /></span>
          Settings
        </button>
      </nav>
    </div>
  );
}
