import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { RefreshCw, LogOut, Settings, Server, Layers, BarChart3 } from 'lucide-react';
import { useAuthStore } from '../../stores/auth-store';
import { useSettingsStore } from '../../stores/settings-store';
import styles from './UserMenu.module.css';

interface Props {
  onClose: () => void;
}

let cachedOnlineCount: { count: number; total: number; at: number } | null = null;

export default function UserMenu({ onClose }: Props) {
  const navigate = useNavigate();
  const { user, serverInfo, logout } = useAuthStore();
  const settings = useSettingsStore((s) => s.settings);
  const isCombined = settings?.libraryMode === 'combined';
  const serverCount = settings?.servers?.length ?? 0;
  const [onlineCount, setOnlineCount] = useState<number | null>(
    cachedOnlineCount && Date.now() - cachedOnlineCount.at < 60000 ? cachedOnlineCount.count : null,
  );
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose();
    };
    const keyHandler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('mousedown', handler);
    document.addEventListener('keydown', keyHandler);
    return () => {
      document.removeEventListener('mousedown', handler);
      document.removeEventListener('keydown', keyHandler);
    };
  }, [onClose]);

  // Check server reachability when menu opens (cache for 60s)
  useEffect(() => {
    if (!isCombined || !settings?.servers?.length) return;
    if (cachedOnlineCount && Date.now() - cachedOnlineCount.at < 60000) return;

    const servers = settings.servers;
    Promise.all(servers.map((s) => window.api.auth.checkServer(s.url))).then((results) => {
      const count = results.filter((r) => r.success && r.data === true).length;
      setOnlineCount(count);
      cachedOnlineCount = { count, total: servers.length, at: Date.now() };
    });
  }, [isCombined, settings?.servers]);

  const handleSwitchServer = () => {
    onClose();
    navigate('/login');
  };

  const handleLogout = async () => {
    onClose();
    await logout();
    navigate('/login');
  };

  return (
    <div ref={ref} className={styles.menu}>
      <div className={styles.header}>
        <div className={styles.userName}>{user?.Name}</div>
        <div className={styles.serverName}>
          {isCombined ? (
            <>
              <Layers size={12} className={styles.serverIcon} />
              {onlineCount !== null ? `${onlineCount}/${serverCount}` : serverCount} server{serverCount !== 1 ? 's' : ''}{onlineCount !== null ? ' online' : ''} &middot; Combined
            </>
          ) : (
            <>
              <Server size={12} className={styles.serverIcon} />
              {serverInfo?.ServerName ?? 'Emby Server'}{' '}
              {serverInfo?.Version ? `v${serverInfo.Version}` : ''}
            </>
          )}
        </div>
      </div>
      <div className={styles.divider} />
      {!isCombined && (
        <button className={styles.item} onClick={handleSwitchServer}>
          <span className={styles.itemIcon}><RefreshCw size={16} /></span>
          Switch Server
        </button>
      )}
      <button className={styles.item} onClick={() => { onClose(); navigate('/analytics'); }}>
        <span className={styles.itemIcon}><BarChart3 size={16} /></span>
        Analytics
      </button>
      <button className={styles.item} onClick={() => { onClose(); navigate('/settings'); }}>
        <span className={styles.itemIcon}><Settings size={16} /></span>
        Settings
      </button>
      <div className={styles.divider} />
      <button className={styles.item} onClick={handleLogout}>
        <span className={styles.itemIcon}><LogOut size={16} /></span>
        Logout
      </button>
    </div>
  );
}
