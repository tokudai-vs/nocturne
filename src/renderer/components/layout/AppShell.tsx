import { useEffect, useRef } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useUiStore } from '../../stores/ui-store';
import { useSettingsStore } from '../../stores/settings-store';
import { useAuthStore } from '../../stores/auth-store';
import { useToastStore } from '../../stores/toast-store';
import { useAppStore } from '../../stores/app-store';
import TopBar from './TopBar';
import SidebarOverlay from './SidebarOverlay';
import SyncProgress from '../ui/SyncProgress';
import LibraryMappingWizard from '../ui/LibraryMappingWizard';
import UpdateNotification from '../ui/UpdateNotification';
import ToastContainer from '../ui/ToastContainer';
import ContextMenu from '../ui/ContextMenu';
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
  const navigate = useNavigate();
  const { setTopBarSolid } = useUiStore();
  const cinemaMode = useUiStore((s) => s.cinemaMode);
  const { fetchSettings } = useSettingsStore();
  const { logout } = useAuthStore();
  const { addToast } = useToastStore();
  const transparent = isTransparentPage(location.pathname);
  const expiredHandled = useRef(false);

  useEffect(() => {
    setTopBarSolid(!transparent);
  }, [transparent, setTopBarSolid]);

  useEffect(() => {
    fetchSettings();
  }, [fetchSettings]);

  useEffect(() => {
    const unsub1 = window.api.app.onVisibilityChange(({ visible }: { visible: boolean }) => {
      useAppStore.getState().setVisible(visible);
    });
    const unsub2 = window.api.app.onFocusChange(({ focused }: { focused: boolean }) => {
      useAppStore.getState().setFocused(focused);
    });
    return () => { unsub1(); unsub2(); };
  }, []);

  useEffect(() => {
    const unsub = window.api.session.onExpired(() => {
      if (expiredHandled.current) return;
      const settings = useSettingsStore.getState().settings;
      if (settings?.libraryMode === 'combined') {
        // Combined mode — don't logout, just warn
        addToast('A server session expired. Check Settings \u2192 Servers.', 'error');
        return;
      }
      expiredHandled.current = true;
      addToast('Session expired. Please sign in again.', 'error');
      logout().then(() => navigate('/login'));
    });
    return unsub;
  }, [logout, navigate, addToast]);

  // Cinema mode: zero chrome. The Watch Party host page sets this when
  // LIVE; the embedded player + overlays own the viewport. Page-padding
  // and TopBar are both dropped — the page is free to use 100vh.
  return (
    <div className={styles.shell}>
      {!cinemaMode && <TopBar />}
      <main
        className={`${styles.content} content-scroll ${!cinemaMode && !transparent ? styles.contentPadded : ''} ${cinemaMode ? styles.contentCinema : ''}`}
      >
        <Outlet />
      </main>
      <SidebarOverlay />
      <SyncProgress />
      <LibraryMappingWizard />
      <UpdateNotification />
      <ToastContainer />
      <ContextMenu />
    </div>
  );
}
