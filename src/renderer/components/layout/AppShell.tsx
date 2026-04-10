import { useEffect } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { useUiStore } from '../../stores/ui-store';
import TopBar from './TopBar';
import SidebarOverlay from './SidebarOverlay';
import styles from './AppShell.module.css';

/** Pages with hero/backdrop get transparent TopBar; others get solid */
const TRANSPARENT_PATHS = ['/', '/detail'];

function isTransparentPage(pathname: string): boolean {
  if (pathname === '/') return true;
  if (pathname.startsWith('/detail/')) return true;
  return false;
}

export default function AppShell() {
  const location = useLocation();
  const { setTopBarSolid } = useUiStore();
  const transparent = isTransparentPage(location.pathname);

  useEffect(() => {
    setTopBarSolid(!transparent);
  }, [transparent, setTopBarSolid]);

  return (
    <div className={styles.shell}>
      <TopBar />
      <main className={`${styles.content} content-scroll ${!transparent ? styles.contentPadded : ''}`}>
        <Outlet />
      </main>
      <SidebarOverlay />
    </div>
  );
}
