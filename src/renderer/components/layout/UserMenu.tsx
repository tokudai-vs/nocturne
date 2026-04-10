import { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { RefreshCw, LogOut } from 'lucide-react';
import { useAuthStore } from '../../stores/auth-store';
import styles from './UserMenu.module.css';

interface Props {
  onClose: () => void;
}

export default function UserMenu({ onClose }: Props) {
  const navigate = useNavigate();
  const { user, serverInfo, logout } = useAuthStore();
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

  const handleSwitchUser = () => {
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
          {serverInfo?.ServerName ?? 'Emby Server'}{' '}
          {serverInfo?.Version ? `v${serverInfo.Version}` : ''}
        </div>
      </div>
      <div className={styles.divider} />
      <button className={styles.item} onClick={handleSwitchUser}>
        <span className={styles.itemIcon}><RefreshCw size={16} /></span>
        Switch User
      </button>
      <div className={styles.divider} />
      <button className={styles.item} onClick={handleLogout}>
        <span className={styles.itemIcon}><LogOut size={16} /></span>
        Logout
      </button>
    </div>
  );
}
