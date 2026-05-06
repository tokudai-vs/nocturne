import { useEffect, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { Home, Film, Tv, Music, BookOpen, Archive, Folder, Star, Settings } from 'lucide-react';
import { useLibraryStore } from '../../stores/library-store';
import { useSettingsStore } from '../../stores/settings-store';
import { useSyncStore } from '../../stores/sync-store';
import { useUiStore } from '../../stores/ui-store';
import type { VirtualLibrary } from '../../api/types';
import styles from './SidebarOverlay.module.css';

const ICON_MAP: Record<string, React.ReactNode> = {
  Film: <Film size={18} />,
  Tv: <Tv size={18} />,
  Music: <Music size={18} />,
  BookOpen: <BookOpen size={18} />,
  Archive: <Archive size={18} />,
  Folder: <Folder size={18} />,
  Star: <Star size={18} />,
};

function iconForVlib(vlib: VirtualLibrary): React.ReactNode {
  return ICON_MAP[vlib.icon] ?? <Folder size={18} />;
}

function iconForCollection(name: string): React.ReactNode {
  const key = name.toLowerCase().replace(/\s/g, '');
  if (key.includes('movie')) return <Film size={18} />;
  if (key.includes('tv') || key.includes('show') || key.includes('series')) return <Tv size={18} />;
  if (key.includes('music')) return <Music size={18} />;
  if (key.includes('book')) return <BookOpen size={18} />;
  if (key.includes('boxset') || key.includes('collection')) return <Archive size={18} />;
  return <Folder size={18} />;
}

export default function SidebarOverlay() {
  const { sidebarOpen, closeSidebar } = useUiStore();
  const { virtualLibraries, vlibsLoaded, fetchVirtualLibraries, views, fetchViews } = useLibraryStore();
  const settings = useSettingsStore((s) => s.settings);
  const { completed: syncCompleted } = useSyncStore();
  const isCombined = settings?.libraryMode === 'combined';
  const [closing, setClosing] = useState(false);

  useEffect(() => {
    if (sidebarOpen) {
      fetchVirtualLibraries();
      if (!isCombined && views.length === 0) fetchViews();
    }
  }, [sidebarOpen, fetchVirtualLibraries, views.length, fetchViews, isCombined]);

  // Refresh virtual libraries when sync completes
  useEffect(() => {
    if (syncCompleted) {
      fetchVirtualLibraries();
    }
  }, [syncCompleted, fetchVirtualLibraries]);

  // Refresh virtual libraries on Trakt watchlist changes so the sidebar's
  // Trakt Watchlist row count tracks add/remove + initial post-auth refresh
  // without waiting for the next 1h timer. Always-on listener (cheap) so it
  // works regardless of whether the sidebar is currently open.
  useEffect(() => {
    const off = window.api.trakt.onWatchlistUpdated(() => {
      fetchVirtualLibraries();
    });
    return off;
  }, [fetchVirtualLibraries]);

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

  // Use virtual libraries if available, else fall back to raw views
  const hasVlibs = vlibsLoaded && virtualLibraries.length > 0;
  const mappedVlibs = virtualLibraries.filter((v) => v.isVirtual);
  const unmappedVlibs = virtualLibraries.filter((v) => !v.isVirtual);

  return (
    <div
      className={`${styles.overlay} ${closing ? styles.overlayOut : ''}`}
      onClick={(e) => {
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

          {hasVlibs ? (
            <>
              {mappedVlibs.map((vlib) => (
                <NavLink
                  key={vlib.id}
                  to={`/library/${vlib.id}`}
                  className={({ isActive }) => `${styles.navItem} ${isActive ? styles.active : ''}`}
                  onClick={handleNav}
                >
                  <span className={styles.navIcon}>{iconForVlib(vlib)}</span>
                  {vlib.name}
                </NavLink>
              ))}
              {unmappedVlibs.length > 0 && (
                <>
                  <div className={styles.divider} />
                  {unmappedVlibs.map((vlib) => (
                    <NavLink
                      key={vlib.id}
                      to={`/library/${vlib.id}`}
                      className={({ isActive }) => `${styles.navItem} ${isActive ? styles.active : ''}`}
                      onClick={handleNav}
                    >
                      <span className={styles.navIcon}>{iconForVlib(vlib)}</span>
                      {vlib.name}
                    </NavLink>
                  ))}
                </>
              )}
            </>
          ) : !isCombined ? (
            // Fallback: raw Emby views (separate mode only)
            views.map((v) => (
              <NavLink
                key={v.Id}
                to={`/library/${v.Id}`}
                className={({ isActive }) => `${styles.navItem} ${isActive ? styles.active : ''}`}
                onClick={handleNav}
              >
                <span className={styles.navIcon}>{iconForCollection(v.Name)}</span>
                {v.Name}
              </NavLink>
            ))
          ) : null}
        </div>
        <div className={styles.divider} />
        <NavLink
          to="/settings"
          className={({ isActive }) => `${styles.navItem} ${isActive ? styles.active : ''}`}
          onClick={handleNav}
        >
          <span className={styles.navIcon}><Settings size={18} /></span>
          Settings
        </NavLink>
      </nav>
    </div>
  );
}
