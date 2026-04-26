import { useState, type FormEvent } from 'react';
import { X, ArrowLeft } from 'lucide-react';
import type { PublicUser, EmbyServerInfo } from '../../api/types';
import styles from './AddServerModal.module.css';

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

interface Props {
  initialUrl?: string;
  onClose: () => void;
  onServerAdded: () => void;
}

export default function AddServerModal({ initialUrl, onClose, onServerAdded }: Props) {
  const [step, setStep] = useState<'url' | 'login'>('url');
  const [url, setUrl] = useState(initialUrl ?? '');
  const [serverInfo, setServerInfo] = useState<EmbyServerInfo | null>(null);
  const [connecting, setConnecting] = useState(false);
  const [error, setError] = useState('');

  // Login state
  const [publicUsers, setPublicUsers] = useState<PublicUser[]>([]);
  const [selectedUser, setSelectedUser] = useState<PublicUser | null>(null);
  const [manualMode, setManualMode] = useState(false);
  const [manualUser, setManualUser] = useState('');
  const [password, setPassword] = useState('');
  const [loginError, setLoginError] = useState('');
  const [loggingIn, setLoggingIn] = useState(false);

  const handleConnect = async (e: FormEvent) => {
    e.preventDefault();
    const cleaned = url.trim().replace(/\/+$/, '');
    if (!cleaned) return;

    setConnecting(true);
    setError('');
    const res = await window.api.auth.connectToServerStandalone(cleaned);
    setConnecting(false);

    if (res.success) {
      setServerInfo(res.data as EmbyServerInfo);
      setStep('login');

      // Fetch public users from the target server (not active server)
      const usersRes = await window.api.auth.getPublicUsersForServer(cleaned);
      if (usersRes.success && (usersRes.data as PublicUser[]).length > 0) {
        setPublicUsers(usersRes.data as PublicUser[]);
      } else {
        setManualMode(true);
      }
    } else {
      setError(friendlyError(res.error ?? 'Connection failed'));
    }
  };

  const handleUserClick = async (user: PublicUser) => {
    setLoginError('');
    if (!user.HasPassword) {
      await doLogin(user.Name, '');
      return;
    }
    setSelectedUser(user);
    setPassword('');
  };

  const handlePasswordSubmit = async (e: FormEvent) => {
    e.preventDefault();
    const username = manualMode ? manualUser : selectedUser?.Name;
    if (!username) return;
    await doLogin(username, password);
  };

  async function doLogin(username: string, pw: string) {
    setLoggingIn(true);
    setLoginError('');
    const cleaned = url.trim().replace(/\/+$/, '');
    const res = await window.api.auth.loginToServer(cleaned, username, pw);
    if (res.success) {
      const auth = res.data as { AccessToken: string; User: { Id: string; Name: string } };

      // Save as new server (don't switch to it)
      await window.api.servers.add({
        name: serverInfo!.ServerName,
        url: cleaned,
        userId: auth.User.Id,
        username: auth.User.Name,
        accessToken: auth.AccessToken,
        version: serverInfo!.Version,
      });

      setLoggingIn(false);
      onServerAdded();
      onClose();
    } else {
      setLoggingIn(false);
      setLoginError(res.error ?? 'Invalid credentials');
    }
  }

  const goBack = () => {
    setStep('url');
    setServerInfo(null);
    setPublicUsers([]);
    setSelectedUser(null);
    setManualMode(false);
    setLoginError('');
  };

  return (
    <div className={styles.backdrop} onClick={onClose}>
      <div className={styles.modal} onClick={(e) => e.stopPropagation()}>
        {/* Header */}
        <div className={styles.header}>
          <h2 className={styles.title}>
            {step === 'url' ? 'Add Server' : `Add Server \u2014 ${serverInfo?.ServerName} (v${serverInfo?.Version})`}
          </h2>
          <button className={styles.closeBtn} onClick={onClose}>
            <X size={18} />
          </button>
        </div>

        {/* Step 1: URL */}
        {step === 'url' && (
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
            {error && <div className={styles.error}>{error}</div>}
            <div className={styles.actions}>
              <div />
              <button type="submit" className={styles.primaryBtn} disabled={connecting}>
                {connecting ? 'Connecting...' : 'Connect'}
              </button>
            </div>
          </form>
        )}

        {/* Step 2: Login */}
        {step === 'login' && (
          <div className={styles.form}>
            {/* Public users */}
            {!selectedUser && !manualMode && publicUsers.length > 0 && (
              <>
                <div className={styles.userGrid}>
                  {publicUsers.map((u) => (
                    <button key={u.Id} className={styles.userCard} onClick={() => handleUserClick(u)}>
                      <div className={styles.userAvatar}>
                        {u.PrimaryImageTag ? (
                          <img
                            src={`${url.trim().replace(/\/+$/, '')}/emby/Users/${u.Id}/Images/Primary?maxWidth=80&tag=${u.PrimaryImageTag}`}
                            alt={u.Name}
                            className={styles.userAvatarImg}
                          />
                        ) : (
                          <span className={styles.userInitial}>{u.Name.charAt(0)}</span>
                        )}
                      </div>
                      <div className={styles.userName}>{u.Name}</div>
                    </button>
                  ))}
                </div>
                <button className={styles.linkBtn} onClick={() => setManualMode(true)}>
                  Manual Login
                </button>
              </>
            )}

            {/* Password / manual form */}
            {(selectedUser || manualMode) && (
              <form onSubmit={handlePasswordSubmit} className={styles.form}>
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
                {selectedUser && (
                  <div className={styles.selectedUserLabel}>
                    Password for <strong>{selectedUser.Name}</strong>
                  </div>
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
                <div className={styles.actions}>
                  <button type="button" className={styles.secondaryBtn} onClick={goBack}>
                    <ArrowLeft size={14} /> Back
                  </button>
                  <button type="submit" className={styles.primaryBtn} disabled={loggingIn}>
                    {loggingIn ? 'Signing in...' : 'Sign In'}
                  </button>
                </div>
              </form>
            )}

            {/* Back button when viewing public users */}
            {!selectedUser && !manualMode && (
              <div className={styles.actions}>
                <button type="button" className={styles.secondaryBtn} onClick={goBack}>
                  <ArrowLeft size={14} /> Back
                </button>
                <div />
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
