import { useState, useEffect, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuthStore } from '../stores/auth-store';
import type { PublicUser, ServerConfig } from '../api/types';
import { buildImageUrl } from '../utils/image-url';
import styles from './LoginPage.module.css';

const SERVER_URL_KEY = 'nocturne_last_server';

function friendlyError(msg: string): string {
  if (msg.includes('503'))
    return 'Server is temporarily unavailable. Please try again in a moment.';
  if (msg.includes('404'))
    return 'No Emby server found at this address. Check the URL and try again.';
  if (msg.includes('ECONNREFUSED') || msg.includes('ENOTFOUND') || msg.includes('Network'))
    return 'Could not connect to server. Make sure the server is running and the address is correct.';
  if (/timeout|ETIMEDOUT/i.test(msg))
    return 'Connection timed out. Check your network and try again.';
  return 'Could not connect to server. Please verify the address and try again.';
}

export default function LoginPage() {
  const navigate = useNavigate();
  const { serverInfo, serverUrl, isAuthenticated, isConnecting, error, connectToServer, login, switchServer } = useAuthStore();

  const [step, setStep] = useState<'server' | 'users'>(serverInfo ? 'users' : 'server');
  const [url, setUrl] = useState(() => localStorage.getItem(SERVER_URL_KEY) ?? '');
  const [publicUsers, setPublicUsers] = useState<PublicUser[]>([]);
  const [selectedUser, setSelectedUser] = useState<PublicUser | null>(null);
  const [password, setPassword] = useState('');
  const [manualMode, setManualMode] = useState(false);
  const [manualUser, setManualUser] = useState('');
  const [loginError, setLoginError] = useState('');
  const [savedServers, setSavedServers] = useState<ServerConfig[]>([]);
  const [switchingId, setSwitchingId] = useState<string | null>(null);

  useEffect(() => {
    if (isAuthenticated) navigate('/', { replace: true });
  }, [isAuthenticated, navigate]);

  useEffect(() => {
    window.api.servers.getAll().then((res) => {
      if (res.success && res.data) setSavedServers(res.data as ServerConfig[]);
    });
  }, []);

  const handleConnect = async (e: FormEvent) => {
    e.preventDefault();
    const cleaned = url.trim().replace(/\/+$/, '');
    if (!cleaned) return;
    const ok = await connectToServer(cleaned);
    if (ok) {
      localStorage.setItem(SERVER_URL_KEY, cleaned);
      setStep('users');
      const res = await window.api.auth.getPublicUsers();
      if (res.success && (res.data as PublicUser[]).length > 0) {
        setPublicUsers(res.data as PublicUser[]);
      } else {
        setManualMode(true);
      }
    }
  };

  const handleUserClick = async (user: PublicUser) => {
    setLoginError('');
    if (!user.HasPassword) {
      const ok = await login(user.Name, '');
      if (!ok) setLoginError('Login failed');
      return;
    }
    setSelectedUser(user);
    setPassword('');
  };

  const handlePasswordSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setLoginError('');
    const username = manualMode ? manualUser : selectedUser?.Name;
    if (!username) return;
    const ok = await login(username, password);
    if (!ok) setLoginError(error ?? 'Invalid password');
  };

  const handleSavedServerConnect = async (server: ServerConfig) => {
    setSwitchingId(server.id);
    setLoginError('');
    const ok = await switchServer(server.id);
    setSwitchingId(null);
    if (!ok) {
      setLoginError(`Session expired for ${server.name}. Please re-login.`);
      // Pre-fill the URL so they can reconnect easily
      setUrl(server.url);
    }
  };

  const handleRemoveServer = async (serverId: string) => {
    await window.api.servers.remove(serverId);
    setSavedServers((prev) => prev.filter((s) => s.id !== serverId));
  };

  const goBack = () => {
    setStep('server');
    setSelectedUser(null);
    setManualMode(false);
    setLoginError('');
  };

  return (
    <div className={`${styles.page} fade-in`}>
      <div className={styles.container}>
        <h1 className={styles.logo}>NOCTURNE</h1>

        {step === 'server' && (
          <>
            {savedServers.length > 0 && (
              <div className={styles.savedServers}>
                <p className={styles.subtitle}>Your Servers</p>
                <div className={styles.serverList}>
                  {savedServers.map((s) => (
                    <div key={s.id} className={styles.savedServerCard}>
                      <button
                        className={styles.savedServerInfo}
                        onClick={() => handleSavedServerConnect(s)}
                        disabled={switchingId !== null}
                      >
                        <div className={styles.savedServerName}>{s.name}</div>
                        <div className={styles.savedServerMeta}>
                          {s.username} &middot; {s.url.replace(/^https?:\/\//, '')}
                        </div>
                      </button>
                      {switchingId === s.id ? (
                        <span className={styles.switchingLabel}>Connecting...</span>
                      ) : (
                        <button
                          className={styles.removeServerBtn}
                          onClick={() => handleRemoveServer(s.id)}
                          title="Remove server"
                        >
                          &times;
                        </button>
                      )}
                    </div>
                  ))}
                </div>
                {loginError && <div className={styles.error}>{loginError}</div>}
                <div className={styles.divider}>
                  <span>or add a new server</span>
                </div>
              </div>
            )}

            <form onSubmit={handleConnect} className={styles.form}>
              <label className={styles.label}>Server Address</label>
              <input
                type="text"
                className={styles.input}
                placeholder="http://192.168.1.100:8096"
                value={url}
                onChange={(e) => setUrl(e.target.value)}
                autoFocus
              />
              {error && <div className={styles.error}>{friendlyError(error)}</div>}
              <button type="submit" className={styles.btn} disabled={isConnecting}>
                {isConnecting ? 'Connecting...' : 'Connect'}
              </button>
            </form>
          </>
        )}

        {step === 'users' && (
          <div className={styles.usersSection}>
            <div className={styles.serverBadge}>
              {serverInfo?.ServerName} <span className={styles.version}>v{serverInfo?.Version}</span>
            </div>

            {!selectedUser && !manualMode && (
              <>
                <p className={styles.subtitle}>Select User</p>
                <div className={styles.userGrid}>
                  {publicUsers.map((u) => (
                    <button key={u.Id} className={styles.userCard} onClick={() => handleUserClick(u)}>
                      <div className={styles.userAvatar}>
                        {u.PrimaryImageTag && serverUrl ? (
                          <img
                            src={buildImageUrl(u.Id, 'Primary', { maxWidth: 120, tag: u.PrimaryImageTag })}
                            alt={u.Name}
                            className={styles.userAvatarImg}
                          />
                        ) : (
                          <span className={styles.userInitial}>{u.Name.charAt(0)}</span>
                        )}
                      </div>
                      <div className={styles.userLabel}>{u.Name}</div>
                    </button>
                  ))}
                </div>
                <button className={styles.manualBtn} onClick={() => setManualMode(true)}>
                  Manual Login
                </button>
              </>
            )}

            {(selectedUser || manualMode) && (
              <form onSubmit={handlePasswordSubmit} className={styles.form}>
                {selectedUser && (
                  <p className={styles.subtitle}>
                    Password for <strong>{selectedUser.Name}</strong>
                  </p>
                )}
                {manualMode && (
                  <>
                    <label className={styles.label}>Username</label>
                    <input
                      type="text"
                      className={styles.input}
                      value={manualUser}
                      onChange={(e) => setManualUser(e.target.value)}
                      autoFocus
                    />
                  </>
                )}
                <label className={styles.label}>Password</label>
                <input
                  type="password"
                  className={styles.input}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoFocus={!!selectedUser}
                />
                {loginError && <div className={styles.error}>{loginError}</div>}
                <button type="submit" className={styles.btn} disabled={isConnecting}>
                  {isConnecting ? 'Signing in...' : 'Sign In'}
                </button>
              </form>
            )}

            <button className={styles.backBtn} onClick={goBack}>&larr; Change Server</button>
          </div>
        )}
      </div>
    </div>
  );
}
