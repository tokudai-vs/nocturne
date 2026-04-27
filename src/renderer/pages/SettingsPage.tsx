import { useEffect, useState, useRef, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  ArrowLeft, Film, Tv, Music, BookOpen, Archive, Folder, Star,
  Plus, X, Trash2,
} from 'lucide-react';
import { useAuthStore } from '../stores/auth-store';
import { useSettingsStore } from '../stores/settings-store';
import { useSyncStore } from '../stores/sync-store';
import { useToastStore } from '../stores/toast-store';
import Toggle from '../components/ui/Toggle';
import Select from '../components/ui/Select';
import Slider from '../components/ui/Slider';
import SettingsRow from '../components/ui/SettingsRow';
import AddServerModal from '../components/ui/AddServerModal';
import ConfirmDialog from '../components/ui/ConfirmDialog';
import TraktAuthModal from '../components/ui/TraktAuthModal';
import TraktHistoryPreviewModal from '../components/ui/TraktHistoryPreviewModal';
import { SUBTITLE_LANGUAGE_OPTIONS, AUDIO_LANGUAGE_OPTIONS } from '../../shared/languages';
import type { BaseItemDto, LibraryMapping, DbStats, DedupStats, UpdateStatus, CombinedMapping, CombinedLibraryRef, ServerConfig, SyncStatus, TraktStatus, TraktAdvancedConfig, TraktSyncStats } from '../api/types';
import { formatDistanceToNow } from 'date-fns';
import styles from './SettingsPage.module.css';

// ── Icon helpers ────────────────────────────────────────

const ICON_COMPONENTS: Record<string, React.ReactNode> = {
  Film: <Film size={16} />,
  Tv: <Tv size={16} />,
  Music: <Music size={16} />,
  BookOpen: <BookOpen size={16} />,
  Archive: <Archive size={16} />,
  Folder: <Folder size={16} />,
  Star: <Star size={16} />,
};

const ICON_NAMES = Object.keys(ICON_COMPONENTS);

function getIcon(name: string, size = 16): React.ReactNode {
  const Component = ICON_COMPONENTS[name];
  if (Component) return Component;
  // Re-render with correct size for non-cached
  switch (name) {
    case 'Film': return <Film size={size} />;
    case 'Tv': return <Tv size={size} />;
    case 'Music': return <Music size={size} />;
    case 'BookOpen': return <BookOpen size={size} />;
    case 'Archive': return <Archive size={size} />;
    case 'Star': return <Star size={size} />;
    default: return <Folder size={size} />;
  }
}

// ── Select options ──────────────────────────────────────

const QUALITY_OPTIONS = [
  { value: 'highest', label: '4K / Highest' },
  { value: 'lowest', label: 'HD / Lowest' },
];

const FONT_OPTIONS = [
  { value: 'Segoe UI Semibold', label: 'Segoe UI' },
  { value: 'Arial', label: 'Arial' },
  { value: 'Roboto', label: 'Roboto' },
  { value: 'Noto Sans', label: 'Noto Sans' },
  { value: 'Verdana', label: 'Verdana' },
  { value: 'Tahoma', label: 'Tahoma' },
  { value: 'Consolas', label: 'Consolas' },
];

const SUB_COLOR_OPTIONS = [
  { value: '#FFFFFF', label: 'White' },
  { value: '#FFFF00', label: 'Yellow' },
  { value: '#00FFFF', label: 'Cyan' },
  { value: '#00FF00', label: 'Green' },
];

const SUB_BG_OPTIONS = [
  { value: 'none', label: 'None' },
  { value: 'semi', label: 'Semi-transparent' },
  { value: 'opaque', label: 'Opaque' },
];

const POWER_MODE_OPTIONS = [
  { value: 'performance', label: 'Performance' },
  { value: 'balanced', label: 'Balanced' },
  { value: 'efficiency', label: 'Efficiency' },
];

const CACHE_SIZE_OPTIONS = [
  { value: '250', label: '250 MB' },
  { value: '500', label: '500 MB' },
  { value: '1000', label: '1 GB' },
  { value: '2000', label: '2 GB' },
];

// ── Component ───────────────────────────────────────────

export default function SettingsPage() {
  const navigate = useNavigate();
  const { user, switchServer, activeServerId } = useAuthStore();
  const { settings, fetchSettings, updateSetting } = useSettingsStore();
  const { running: syncRunning } = useSyncStore();
  const addToast = useToastStore((s) => s.addToast);

  const [views, setViews] = useState<BaseItemDto[]>([]);
  const [stats, setStats] = useState<DbStats | null>(null);
  const [dedupStats, setDedupStats] = useState<DedupStats | null>(null);
  const [rebuildingDedup, setRebuildingDedup] = useState(false);
  const [dedupInfo, setDedupInfo] = useState<{
    dedupStatus: SyncStatus['dedupStatus'];
    lastDedupBuild: string | null;
  } | null>(null);
  const [updateStatus, setUpdateStatus] = useState<UpdateStatus | null>(null);
  const [savedVisible, setSavedVisible] = useState(false);
  const savedTimer = useRef<ReturnType<typeof setTimeout>>(undefined);

  // Server management state
  const [servers, setServers] = useState<ServerConfig[]>([]);
  const [showAddServer, setShowAddServer] = useState(false);
  const [removeConfirm, setRemoveConfirm] = useState<ServerConfig | null>(null);
  const [switchingId, setSwitchingId] = useState<string | null>(null);
  const [serverStatus, setServerStatus] = useState<Record<string, 'online' | 'offline' | 'auth-expired' | undefined>>({});
  const [reloginUrl, setReloginUrl] = useState<string | null>(null);
  const [showResetConfirm, setShowResetConfirm] = useState(false);
  const [resetting, setResetting] = useState(false);

  // Trakt state
  const [traktStatus, setTraktStatus] = useState<TraktStatus | null>(null);
  const [traktStats, setTraktStats] = useState<TraktSyncStats | null>(null);
  const [showTraktModal, setShowTraktModal] = useState(false);
  const [showTraktPreview, setShowTraktPreview] = useState(false);
  const [showTraktDisconnect, setShowTraktDisconnect] = useState(false);
  const [traktAdvanced, setTraktAdvanced] = useState<TraktAdvancedConfig | null>(null);
  const [traktDraftId, setTraktDraftId] = useState('');
  const [traktDraftSecret, setTraktDraftSecret] = useState('');
  const [traktAdvancedSaved, setTraktAdvancedSaved] = useState(false);
  const [traktDraining, setTraktDraining] = useState(false);
  const [traktSyncingNow, setTraktSyncingNow] = useState(false);

  // Library mapping state
  const [mappings, setMappings] = useState<Record<string, LibraryMapping>>({});
  const [libraryMode, setLibraryMode] = useState<'separate' | 'combined'>('separate');
  const [combinedMappings, setCombinedMappings] = useState<Record<string, CombinedMapping>>({});
  const [allServerLibs, setAllServerLibs] = useState<CombinedLibraryRef[]>([]);
  const [dragItem, setDragItem] = useState<string | null>(null);
  const [dragOverGroup, setDragOverGroup] = useState<string | null>(null);
  const [iconPickerGroup, setIconPickerGroup] = useState<string | null>(null);

  const loadServers = useCallback(() => {
    window.api.servers.getAll().then((res) => {
      if (res.success && res.data) {
        const list = res.data as ServerConfig[];
        setServers(list);
        // Check reachability + token validity for all servers in parallel
        for (const s of list) {
          window.api.auth.checkServer(s.url).then((r) => {
            if (r.success && r.data === true) {
              setServerStatus((prev) => ({ ...prev, [s.id]: 'online' }));
            } else {
              setServerStatus((prev) => ({ ...prev, [s.id]: 'offline' }));
            }
          });
        }
      }
    });
  }, []);

  const loadTraktStatus = useCallback(() => {
    window.api.trakt.getStatus().then((res) => {
      if (res.success && res.data) setTraktStatus(res.data as TraktStatus);
    });
    window.api.trakt.getAdvancedConfig().then((res) => {
      if (res.success && res.data) {
        const cfg = res.data as TraktAdvancedConfig;
        setTraktAdvanced(cfg);
        setTraktDraftId(cfg.clientIdOverride);
        setTraktDraftSecret(cfg.clientSecretOverride);
      }
    });
    window.api.trakt.getStats().then((res) => {
      if (res.success && res.data) setTraktStats(res.data as TraktSyncStats);
    });
  }, []);

  useEffect(() => {
    fetchSettings();
    loadViews();
    loadStats();
    loadServers();
    loadTraktStatus();
    window.api.updater.getStatus().then((res) => {
      if (res.success && res.data) setUpdateStatus(res.data as UpdateStatus);
    });
    const unsubUpdater = window.api.updater.onStatus((s) => setUpdateStatus(s));
    const unsubAuth = window.api.trakt.onAuthSuccess(() => {
      loadTraktStatus();
      addToast('Connected to Trakt', 'success');
      // Phase 2: chain into the history preview modal automatically.
      // Settings is the right surface to host this — the auth modal closes
      // immediately on success and we want the preview while context is fresh.
      setShowTraktPreview(true);
    });
    const unsubRefresh = window.api.trakt.onTokenRefreshFailed(() => {
      loadTraktStatus();
      addToast('Trakt session expired — please reconnect', 'error');
    });
    const unsubScrobbleErr = window.api.trakt.onScrobbleError(() => {
      loadTraktStatus();
    });
    const unsubBgSync = window.api.trakt.onSyncComplete((data) => {
      loadTraktStatus();
      if (data.newlyWatched > 0) {
        addToast(`Trakt sync: marked ${data.newlyWatched} new item${data.newlyWatched === 1 ? '' : 's'} watched`, 'success');
      }
    });
    return () => {
      unsubUpdater();
      unsubAuth();
      unsubRefresh();
      unsubScrobbleErr();
      unsubBgSync();
    };
  }, [fetchSettings, loadServers, loadTraktStatus, addToast]);

  async function loadViews() {
    const res = await window.api.library.getViews();
    if (res.success) {
      setViews((res.data as { Items: BaseItemDto[] }).Items || []);
    }
  }

  const loadAllServerLibs = useCallback(async () => {
    // Fetch live views from all servers for combined mode
    const res = await window.api.library.getAllServersViews();
    if (res.success && res.data) {
      const result = res.data as {
        views: Array<{ Id: string; Name: string; serverId: string; serverName: string }>;
        errors: Array<{ serverId: string; serverName: string; reason: 'offline' | 'auth-expired' }>;
      };
      const libs = result.views.map((v) => ({
        serverId: v.serverId,
        serverName: v.serverName,
        libraryId: v.Id,
        libraryName: v.Name,
      }));
      setAllServerLibs(libs);

      // Update server status for errored servers
      for (const err of result.errors) {
        setServerStatus((prev) => ({ ...prev, [err.serverId]: err.reason }));
        if (err.reason === 'auth-expired') {
          addToast(`Session expired for ${err.serverName}. Re-login in Settings.`, 'error');
        }
      }

      // Auto-suggest combined groups only on first-time initialization.
      // Once the user has saved mappings once (even if later cleared),
      // combinedMappingsInitialized becomes true and we never regenerate.
      const initRes = await window.api.settings.getValue('combinedMappingsInitialized');
      const initialized = initRes.success ? Boolean(initRes.data) : false;
      if (!initialized && libs.length > 0) {
        autoSuggestCombinedGroups(libs);
      }
    }
  }, [addToast]);

  useEffect(() => {
    // Load per-server mappings via server manager API
    window.api.servers.getMappings().then((res) => {
      if (res.success && res.data) {
        setMappings(res.data as Record<string, LibraryMapping>);
      }
    });
    // Load combined mode data
    window.api.servers.getLibraryMode().then((res) => {
      if (res.success && res.data) {
        const mode = res.data as 'separate' | 'combined';
        setLibraryMode(mode);
        if (mode === 'combined') loadAllServerLibs();
      }
    });
    window.api.servers.getCombinedMappings().then((res) => {
      if (res.success && res.data) setCombinedMappings(res.data as Record<string, CombinedMapping>);
    });
  }, [settings, loadAllServerLibs]);

  async function loadStats() {
    const res = await window.api.cache.getStats();
    if (res.success) setStats(res.data as DbStats);
    const dRes = await window.api.dedup.getStats();
    if (dRes.success) setDedupStats(dRes.data as DedupStats);
    const sRes = await window.api.sync.getStatus();
    if (sRes.success && sRes.data) {
      setDedupInfo({
        dedupStatus: sRes.data.dedupStatus,
        lastDedupBuild: sRes.data.lastDedupBuild,
      });
    }
  }

  async function handleRebuildDedup() {
    setRebuildingDedup(true);
    const res = await window.api.dedup.rebuild();
    if (!res.success) {
      addToast(`Dedup rebuild failed: ${res.error ?? 'unknown error'}`, 'error');
    } else if (res.data && !res.data.success) {
      addToast(`Dedup rebuild failed: ${res.data.error ?? 'unknown error'}`, 'error');
    }
    await loadStats();
    setRebuildingDedup(false);
  }

  // Keep dedup state fresh when the background dedup phase completes or fails
  useEffect(() => {
    const offComplete = window.api.dedup.onComplete(() => {
      loadStats();
    });
    const offError = window.api.dedup.onError((err) => {
      addToast(`Dedup failed: ${err.message}`, 'error');
      loadStats();
    });
    return () => {
      offComplete();
      offError();
    };
  }, [addToast]);

  const showSaved = useCallback(() => {
    setSavedVisible(true);
    if (savedTimer.current) clearTimeout(savedTimer.current);
    savedTimer.current = setTimeout(() => setSavedVisible(false), 1500);
  }, []);

  const handleSettingChange = useCallback(
    (key: string, value: unknown) => {
      updateSetting(key, value);
      showSaved();
    },
    [updateSetting, showSaved],
  );

  // ── Library mapping helpers ───────────────────────────

  const assignedLibIds = new Set(
    Object.values(mappings).flatMap((g) => g.libraryIds),
  );
  const unmappedLibs = views.filter((v) => !assignedLibIds.has(v.Id));

  function saveMappings(next: Record<string, LibraryMapping>) {
    setMappings(next);
    window.api.servers.setMappings(next);
    showSaved();
  }

  function addGroup() {
    const id = `group_${Date.now()}`;
    saveMappings({
      ...mappings,
      [id]: { name: 'New Group', icon: 'Folder', libraryIds: [] },
    });
  }

  function deleteGroup(groupId: string) {
    const next = { ...mappings };
    delete next[groupId];
    saveMappings(next);
  }

  function renameGroup(groupId: string, name: string) {
    saveMappings({
      ...mappings,
      [groupId]: { ...mappings[groupId], name },
    });
  }

  function setGroupIcon(groupId: string, icon: string) {
    saveMappings({
      ...mappings,
      [groupId]: { ...mappings[groupId], icon },
    });
    setIconPickerGroup(null);
  }

  function assignLibToGroup(libId: string, groupId: string) {
    // Remove from any current group first
    const next: Record<string, LibraryMapping> = {};
    for (const [gid, group] of Object.entries(mappings)) {
      next[gid] = {
        ...group,
        libraryIds: group.libraryIds.filter((id) => id !== libId),
      };
    }
    next[groupId] = {
      ...next[groupId],
      libraryIds: [...next[groupId].libraryIds, libId],
    };
    saveMappings(next);
  }

  function removeLibFromGroup(libId: string, groupId: string) {
    saveMappings({
      ...mappings,
      [groupId]: {
        ...mappings[groupId],
        libraryIds: mappings[groupId].libraryIds.filter((id) => id !== libId),
      },
    });
  }

  // ── Combined mapping helpers ──────────────────────────

  function saveCombinedMappings(next: Record<string, CombinedMapping>) {
    setCombinedMappings(next);
    window.api.servers.setCombinedMappings(next);
    showSaved();
  }

  const assignedCombinedLibKeys = new Set(
    Object.values(combinedMappings).flatMap((g) =>
      g.libraries.map((l) => `${l.serverId}:${l.libraryId}`),
    ),
  );
  const unmappedCombinedLibs = allServerLibs.filter(
    (l) => !assignedCombinedLibKeys.has(`${l.serverId}:${l.libraryId}`),
  );

  function addCombinedGroup() {
    const id = `cgroup_${Date.now()}`;
    saveCombinedMappings({
      ...combinedMappings,
      [id]: { name: 'New Group', icon: 'Folder', libraries: [] },
    });
  }

  function autoSuggestCombinedGroups(libs: CombinedLibraryRef[]) {
    const QUALITY_RE = /\s+(4K|HD|UHD|HDR|DV|Dolby\s?Vision|Remux)\s*/gi;
    const PAREN_RE = /\s*\(.*?\)\s*/g;
    const ICON_GUESS: Record<string, string> = {
      movie: 'Film', film: 'Film', tv: 'Tv', show: 'Tv', series: 'Tv',
      music: 'Music', book: 'BookOpen', collection: 'Archive',
    };

    const groups = new Map<string, CombinedLibraryRef[]>();
    for (const lib of libs) {
      let base = lib.libraryName.replace(PAREN_RE, ' ').trim();
      base = base.replace(QUALITY_RE, ' ').trim();
      const key = base.replace(/s$/i, '').trim().toLowerCase() || lib.libraryName.toLowerCase();
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(lib);
    }

    const result: Record<string, CombinedMapping> = {};
    for (const [key, members] of groups) {
      const displayName = key.charAt(0).toUpperCase() + key.slice(1) + (key.endsWith('s') ? '' : 's');
      const lower = displayName.toLowerCase();
      let icon = 'Folder';
      for (const [kw, ic] of Object.entries(ICON_GUESS)) {
        if (lower.includes(kw)) { icon = ic; break; }
      }
      result[`cgroup_${key.replace(/\s+/g, '_')}`] = { name: displayName, icon, libraries: members };
    }
    saveCombinedMappings(result);
  }

  function deleteCombinedGroup(groupId: string) {
    const next = { ...combinedMappings };
    delete next[groupId];
    saveCombinedMappings(next);
  }

  function renameCombinedGroup(groupId: string, name: string) {
    saveCombinedMappings({
      ...combinedMappings,
      [groupId]: { ...combinedMappings[groupId], name },
    });
  }

  function setCombinedGroupIcon(groupId: string, icon: string) {
    saveCombinedMappings({
      ...combinedMappings,
      [groupId]: { ...combinedMappings[groupId], icon },
    });
    setIconPickerGroup(null);
  }

  function assignCombinedLib(lib: CombinedLibraryRef, groupId: string) {
    const key = `${lib.serverId}:${lib.libraryId}`;
    // Remove from any current group first
    const next: Record<string, CombinedMapping> = {};
    for (const [gid, group] of Object.entries(combinedMappings)) {
      next[gid] = {
        ...group,
        libraries: group.libraries.filter(
          (l) => `${l.serverId}:${l.libraryId}` !== key,
        ),
      };
    }
    next[groupId] = {
      ...next[groupId],
      libraries: [...next[groupId].libraries, lib],
    };
    saveCombinedMappings(next);
  }

  function removeCombinedLib(serverId: string, libraryId: string, groupId: string) {
    const key = `${serverId}:${libraryId}`;
    saveCombinedMappings({
      ...combinedMappings,
      [groupId]: {
        ...combinedMappings[groupId],
        libraries: combinedMappings[groupId].libraries.filter(
          (l) => `${l.serverId}:${l.libraryId}` !== key,
        ),
      },
    });
  }

  // Drag handlers — shared state for both modes
  // combinedDragLib stores the full ref for combined mode drag
  const [combinedDragLib, setCombinedDragLib] = useState<CombinedLibraryRef | null>(null);

  // Drag handlers
  function onDragStart(libId: string) {
    setDragItem(libId);
  }

  function onDragEnd() {
    setDragItem(null);
    setDragOverGroup(null);
  }

  function onDropOnGroup(groupId: string) {
    if (dragItem) {
      assignLibToGroup(dragItem, groupId);
    }
    setDragItem(null);
    setDragOverGroup(null);
  }

  // ── Server management ─────────────────────────────────

  async function handleConnectServer(serverId: string) {
    setSwitchingId(serverId);
    const ok = await switchServer(serverId);
    setSwitchingId(null);
    if (ok) {
      addToast('Switched server successfully', 'success');
      loadServers();
      loadViews();
      loadStats();
    } else {
      addToast('Failed to switch — session may have expired', 'error');
    }
  }

  function handleRemoveServer(server: ServerConfig) {
    if (servers.length <= 1) {
      addToast('Add another server before removing this one', 'error');
      return;
    }
    if (server.id === activeServerId && servers.length <= 1) {
      addToast('Add another server before removing this one', 'error');
      return;
    }
    setRemoveConfirm(server);
  }

  async function confirmRemoveServer() {
    if (!removeConfirm) return;
    await window.api.servers.remove(removeConfirm.id);
    setRemoveConfirm(null);
    addToast('Server removed', 'success');
    loadServers();
    loadStats();
  }

  function handleServerAdded() {
    loadServers();
    addToast('Server added successfully', 'success');
  }

  // ── Cache actions ─────────────────────────────────────

  async function handleSyncNow() {
    await window.api.sync.startFull();
  }

  async function handleClearCache() {
    await window.api.cache.clear();
    loadStats();
    showSaved();
  }

  async function handleResetFull() {
    setResetting(true);
    try {
      await window.api.app.resetFull();
    } catch (err) {
      console.error('[reset] failed', err);
      setResetting(false);
      setShowResetConfirm(false);
    }
  }

  // ── Trakt actions ─────────────────────────────────────

  async function handleTraktConfirmDisconnect() {
    await window.api.trakt.disconnect();
    setShowTraktDisconnect(false);
    loadTraktStatus();
    addToast('Disconnected from Trakt', 'success');
  }

  async function handleSaveTraktAdvanced() {
    await window.api.trakt.setAdvancedConfig({
      clientId: traktDraftId.trim(),
      clientSecret: traktDraftSecret.trim(),
    });
    setTraktAdvancedSaved(true);
    setTimeout(() => setTraktAdvancedSaved(false), 1500);
    loadTraktStatus();
  }

  async function handleDrainTraktQueue() {
    setTraktDraining(true);
    const res = await window.api.trakt.drainQueue();
    setTraktDraining(false);
    loadTraktStatus();
    if (res.success && res.data) {
      const remaining = (res.data as { remaining: number }).remaining;
      addToast(
        remaining === 0 ? 'Trakt queue cleared' : `${remaining} scrobble${remaining === 1 ? '' : 's'} still pending`,
        remaining === 0 ? 'success' : 'error',
      );
    }
  }

  async function handleTraktSyncNow() {
    setTraktSyncingNow(true);
    const res = await window.api.trakt.syncNow();
    setTraktSyncingNow(false);
    loadTraktStatus();
    if (res.success && res.data) {
      const { history, watchlist } = res.data;
      addToast(
        `Synced — ${history.newlyWatched} new watched, ${watchlist.count} watchlist item${watchlist.count === 1 ? '' : 's'}`,
        'success',
      );
    } else {
      addToast(`Sync failed: ${res.error ?? 'unknown error'}`, 'error');
    }
  }

  if (!settings) return null;

  const libCountMap = new Map<string, number>();
  if (stats) {
    for (const entry of stats.itemsByLibrary) {
      libCountMap.set(entry.library_id, entry.count);
    }
  }

  return (
    <div className={`${styles.page} fade-in`}>
      {/* Header */}
      <div className={styles.header}>
        <button className={styles.backBtn} onClick={() => navigate(-1)}>
          <ArrowLeft size={20} />
        </button>
        <h1 className={styles.title}>Settings</h1>
      </div>

      {/* Server */}
      <div className={styles.section}>
        <div className={styles.sectionHeader}>Server</div>
        <div className={styles.serverCards}>
          {servers.map((s) => {
            const isActive = s.id === activeServerId;
            const isCombined = libraryMode === 'combined';
            const status = serverStatus[s.id];
            const isOnline = status === 'online';
            const isAuthExpired = status === 'auth-expired';
            const dotClass = isCombined
              ? (isOnline ? styles.active : isAuthExpired ? styles.authExpired : styles.offline)
              : (isActive ? styles.active : '');
            return (
              <div key={s.id} className={styles.serverCard}>
                <div className={`${styles.serverDot} ${dotClass}`} />
                <div className={styles.serverCardBody}>
                  <div className={styles.serverCardName}>
                    {s.name} {s.version ? `(v${s.version})` : ''}
                    {isCombined
                      ? <span className={`${styles.serverBadge} ${isOnline ? styles.serverBadgeOnline : isAuthExpired ? styles.serverBadgeExpired : styles.serverBadgeOffline}`}>
                          {status === undefined ? '...' : isOnline ? 'Online' : isAuthExpired ? 'Auth expired' : 'Offline'}
                        </span>
                      : isActive && <span className={`${styles.serverBadge} ${styles.serverBadgeActive}`}>Active</span>}
                  </div>
                  <div className={styles.serverCardMeta}>
                    {s.username} &middot; {s.lastConnected
                      ? formatDistanceToNow(new Date(s.lastConnected), { addSuffix: true })
                      : 'Never connected'}
                  </div>
                </div>
                <div className={styles.serverCardActions}>
                  {isCombined && isAuthExpired && (
                    <button
                      className={styles.connectBtn}
                      onClick={() => { setReloginUrl(s.url); setShowAddServer(true); }}
                    >
                      Re-login
                    </button>
                  )}
                  {!isCombined && !isActive && (
                    <button
                      className={styles.connectBtn}
                      onClick={() => handleConnectServer(s.id)}
                      disabled={switchingId !== null}
                    >
                      {switchingId === s.id ? 'Connecting...' : 'Connect'}
                    </button>
                  )}
                  <button className={styles.removeBtn} onClick={() => handleRemoveServer(s)}>
                    Remove
                  </button>
                </div>
              </div>
            );
          })}
        </div>
        <button className={styles.addServerBtn} onClick={() => setShowAddServer(true)}>
          <Plus size={14} /> Add Server
        </button>
      </div>

      {/* Library Mode — only when 2+ servers */}
      {servers.length >= 2 && (
        <div className={styles.section}>
          <div className={styles.sectionHeader}>Library Mode</div>
          <SettingsRow label="Mode" description="Separate: per-server groups. Combined: merge across all servers.">
            <Select
              value={libraryMode}
              options={[
                { value: 'separate', label: 'Separate' },
                { value: 'combined', label: 'Combined' },
              ]}
              onChange={async (v) => {
                const mode = v as 'separate' | 'combined';
                setLibraryMode(mode);
                handleSettingChange('libraryMode', v);
                if (mode === 'combined') {
                  await loadAllServerLibs();
                }
              }}
            />
          </SettingsRow>
        </div>
      )}

      {/* Libraries */}
      <div className={styles.section}>
        <div className={styles.sectionHeader}>Libraries</div>

        <div style={{ fontSize: 'var(--text-sm)', color: 'var(--text-secondary)', padding: '8px 0' }}>
          {libraryMode === 'combined'
            ? 'Organize libraries from all servers into unified groups.'
            : 'Organize your Emby libraries into groups. Drag libraries into groups or click to assign.'}
        </div>

        {libraryMode === 'separate' && (
          <div className={styles.mappingLayout}>
            {/* Left: Unmapped */}
            <div className={styles.mappingColumn}>
              <div className={styles.mappingColumnTitle}>Available Libraries</div>
              {unmappedLibs.length === 0 && (
                <div style={{ fontSize: 'var(--text-xs)', color: 'var(--text-muted)', padding: '8px 0' }}>
                  All libraries assigned
                </div>
              )}
              {unmappedLibs.map((lib) => (
                <div
                  key={lib.Id}
                  className={`${styles.libraryItem} ${dragItem === lib.Id ? styles.dragging : ''}`}
                  draggable
                  onDragStart={() => onDragStart(lib.Id)}
                  onDragEnd={onDragEnd}
                >
                  {getIcon('Folder')}
                  <span>{lib.Name}</span>
                  <span className={styles.libraryItemCount}>{libCountMap.get(lib.Id) ?? 0}</span>
                </div>
              ))}
            </div>

            {/* Right: Groups */}
            <div className={styles.mappingColumn}>
              <div className={styles.mappingColumnTitle}>Groups</div>
              {Object.entries(mappings).map(([groupId, group]) => (
                <div
                  key={groupId}
                  className={`${styles.groupCard} ${dragOverGroup === groupId ? styles.dragOver : ''}`}
                  onDragOver={(e) => { e.preventDefault(); setDragOverGroup(groupId); }}
                  onDragLeave={() => setDragOverGroup(null)}
                  onDrop={(e) => { e.preventDefault(); onDropOnGroup(groupId); }}
                >
                  <div className={styles.groupHeader}>
                    <div style={{ position: 'relative' }}>
                      <button
                        className={styles.groupIconBtn}
                        onClick={() => setIconPickerGroup(iconPickerGroup === groupId ? null : groupId)}
                      >
                        {getIcon(group.icon)}
                      </button>
                      {iconPickerGroup === groupId && (
                        <div className={styles.iconPicker}>
                          {ICON_NAMES.map((name) => (
                            <button
                              key={name}
                              className={`${styles.iconOption} ${group.icon === name ? styles.selected : ''}`}
                              onClick={() => setGroupIcon(groupId, name)}
                            >
                              {getIcon(name)}
                            </button>
                          ))}
                        </div>
                      )}
                    </div>
                    <input
                      className={styles.groupNameInput}
                      value={group.name}
                      onChange={(e) => renameGroup(groupId, e.target.value)}
                    />
                    <button className={styles.deleteGroupBtn} onClick={() => deleteGroup(groupId)}>
                      <Trash2 size={14} />
                    </button>
                  </div>
                  <div className={styles.groupLibraries}>
                    {group.libraryIds.map((libId) => {
                      const lib = views.find((v) => v.Id === libId);
                      return (
                        <div key={libId} className={styles.groupLibItem}>
                          <span>{lib?.Name ?? libId}</span>
                          <button
                            className={styles.removeLibBtn}
                            onClick={() => removeLibFromGroup(libId, groupId)}
                          >
                            <X size={12} />
                          </button>
                        </div>
                      );
                    })}
                  </div>
                  <div className={`${styles.dropZone} ${dragOverGroup === groupId ? styles.active : ''}`}>
                    Drop library here
                  </div>
                </div>
              ))}
              <button className={styles.addGroupBtn} onClick={addGroup}>
                <Plus size={14} /> Create New Group
              </button>
            </div>
          </div>
        )}

        {libraryMode === 'combined' && (
          <div className={styles.mappingLayout}>
            {/* Left: Unmapped from ALL servers */}
            <div className={styles.mappingColumn}>
              <div className={styles.mappingColumnTitle}>Available Libraries</div>
              {unmappedCombinedLibs.length === 0 && (
                <div style={{ fontSize: 'var(--text-xs)', color: 'var(--text-muted)', padding: '8px 0' }}>
                  All libraries assigned
                </div>
              )}
              {unmappedCombinedLibs.map((lib) => (
                <div
                  key={`${lib.serverId}:${lib.libraryId}`}
                  className={`${styles.libraryItem} ${combinedDragLib?.libraryId === lib.libraryId && combinedDragLib?.serverId === lib.serverId ? styles.dragging : ''}`}
                  draggable
                  onDragStart={() => { setCombinedDragLib(lib); setDragItem(`${lib.serverId}:${lib.libraryId}`); }}
                  onDragEnd={() => { setCombinedDragLib(null); onDragEnd(); }}
                >
                  {getIcon('Folder')}
                  <span className={styles.combinedLibName}>
                    <span className={styles.serverTag}>[{lib.serverName}]</span> {lib.libraryName}
                  </span>
                </div>
              ))}
            </div>

            {/* Right: Combined Groups */}
            <div className={styles.mappingColumn}>
              <div className={styles.mappingColumnTitle}>Groups</div>
              {Object.entries(combinedMappings).map(([groupId, group]) => (
                <div
                  key={groupId}
                  className={`${styles.groupCard} ${dragOverGroup === groupId ? styles.dragOver : ''}`}
                  onDragOver={(e) => { e.preventDefault(); setDragOverGroup(groupId); }}
                  onDragLeave={() => setDragOverGroup(null)}
                  onDrop={(e) => {
                    e.preventDefault();
                    if (combinedDragLib) assignCombinedLib(combinedDragLib, groupId);
                    setCombinedDragLib(null);
                    onDragEnd();
                  }}
                >
                  <div className={styles.groupHeader}>
                    <div style={{ position: 'relative' }}>
                      <button
                        className={styles.groupIconBtn}
                        onClick={() => setIconPickerGroup(iconPickerGroup === groupId ? null : groupId)}
                      >
                        {getIcon(group.icon)}
                      </button>
                      {iconPickerGroup === groupId && (
                        <div className={styles.iconPicker}>
                          {ICON_NAMES.map((name) => (
                            <button
                              key={name}
                              className={`${styles.iconOption} ${group.icon === name ? styles.selected : ''}`}
                              onClick={() => setCombinedGroupIcon(groupId, name)}
                            >
                              {getIcon(name)}
                            </button>
                          ))}
                        </div>
                      )}
                    </div>
                    <input
                      className={styles.groupNameInput}
                      value={group.name}
                      onChange={(e) => renameCombinedGroup(groupId, e.target.value)}
                    />
                    <button className={styles.deleteGroupBtn} onClick={() => deleteCombinedGroup(groupId)}>
                      <Trash2 size={14} />
                    </button>
                  </div>
                  <div className={styles.groupLibraries}>
                    {group.libraries.map((lib) => (
                      <div key={`${lib.serverId}:${lib.libraryId}`} className={styles.groupLibItem}>
                        <span>
                          <span className={styles.serverTag}>[{lib.serverName}]</span> {lib.libraryName}
                        </span>
                        <button
                          className={styles.removeLibBtn}
                          onClick={() => removeCombinedLib(lib.serverId, lib.libraryId, groupId)}
                        >
                          <X size={12} />
                        </button>
                      </div>
                    ))}
                  </div>
                  <div className={`${styles.dropZone} ${dragOverGroup === groupId ? styles.active : ''}`}>
                    Drop library here
                  </div>
                </div>
              ))}
              <button className={styles.addGroupBtn} onClick={addCombinedGroup}>
                <Plus size={14} /> Create New Group
              </button>
            </div>
          </div>
        )}

        <SettingsRow label="Show unmapped libraries individually">
          <Toggle
            value={settings.showUnmappedLibraries}
            onChange={(v) => handleSettingChange('showUnmappedLibraries', v)}
          />
        </SettingsRow>
      </div>

      {/* Playback */}
      <div className={styles.section}>
        <div className={styles.sectionHeader}>Playback</div>
        <SettingsRow label="Preferred Quality" description="When multiple versions exist, prefer this quality">
          <Select
            value={settings.preferredQuality}
            options={QUALITY_OPTIONS}
            onChange={(v) => handleSettingChange('preferredQuality', v)}
          />
        </SettingsRow>
        <SettingsRow label="Default Subtitles">
          <Select
            value={settings.defaultSubtitleLanguage}
            options={SUBTITLE_LANGUAGE_OPTIONS}
            onChange={(v) => handleSettingChange('defaultSubtitleLanguage', v)}
          />
        </SettingsRow>
        <SettingsRow label="Default Audio">
          <Select
            value={settings.defaultAudioLanguage}
            options={AUDIO_LANGUAGE_OPTIONS}
            onChange={(v) => handleSettingChange('defaultAudioLanguage', v)}
          />
        </SettingsRow>
        <SettingsRow label="Auto-play next episode">
          <Toggle
            value={settings.autoPlayNextEpisode}
            onChange={(v) => handleSettingChange('autoPlayNextEpisode', v)}
          />
        </SettingsRow>
      </div>

      {/* Trakt */}
      <div className={styles.section}>
        <div className={styles.sectionHeader}>Trakt</div>
        {traktStatus && !traktStatus.encryptionAvailable && (
          <div style={{ fontSize: 'var(--text-xs)', color: 'var(--danger)', padding: '8px 0' }}>
            System credential storage is unavailable on this machine. Trakt cannot be connected safely.
          </div>
        )}
        {traktStatus && traktStatus.encryptionAvailable && !traktStatus.configured && !traktAdvanced?.bundledIdPresent && (
          <div style={{ fontSize: 'var(--text-xs)', color: 'var(--text-muted)', padding: '8px 0' }}>
            Trakt credentials are not bundled in this build. Add a client_id and secret under Advanced below to connect.
          </div>
        )}
        {!traktStatus?.connected ? (
          <SettingsRow
            label="Connect Trakt account"
            description="Auto-scrobble, sync watched state, and surface your Trakt watchlist."
          >
            <button
              className={styles.connectBtn}
              onClick={() => setShowTraktModal(true)}
              disabled={!traktStatus?.encryptionAvailable || !traktStatus?.configured}
            >
              Connect
            </button>
          </SettingsRow>
        ) : (
          <>
            {/* Account header — connected as / since / disconnect */}
            <div className={styles.statRow}>
              <span>
                Connected as <strong style={{ color: 'var(--text-primary)' }}>@{traktStatus.username || 'unknown'}</strong>
                {traktStatus.connectedAt && (
                  <span style={{ color: 'var(--text-muted)', marginLeft: 8 }}>
                    · since {formatDistanceToNow(new Date(traktStatus.connectedAt), { addSuffix: true })}
                  </span>
                )}
              </span>
              <button
                className={`${styles.actionBtn} ${styles.actionBtnDanger}`}
                onClick={() => setShowTraktDisconnect(true)}
              >
                Disconnect
              </button>
            </div>

            {/* SYNC group */}
            <div style={{ fontSize: 'var(--text-xs)', textTransform: 'uppercase', letterSpacing: 1, color: 'var(--text-muted)', marginTop: 'var(--space-3)', marginBottom: 4 }}>
              Sync
            </div>
            <SettingsRow label="Auto-scrobble playback" description="Send start, pause, and stop events to Trakt as you watch.">
              <Toggle
                value={settings.traktAutoScrobble ?? true}
                onChange={(v) => handleSettingChange('traktAutoScrobble', v)}
              />
            </SettingsRow>
            <SettingsRow label="Sync watched state" description="Pull every 6h; mark items watched here when watched on Trakt and vice versa.">
              <Toggle
                value={settings.traktSyncWatchedState ?? true}
                onChange={(v) => handleSettingChange('traktSyncWatchedState', v)}
              />
            </SettingsRow>
            <SettingsRow label="Show watchlist in sidebar" description="Add a Trakt Watchlist virtual library to the sidebar.">
              <Toggle
                value={settings.traktShowWatchlistInSidebar ?? true}
                onChange={(v) => handleSettingChange('traktShowWatchlistInSidebar', v)}
              />
            </SettingsRow>
            <div className={styles.statRow}>
              <span>
                Last synced
                <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 2 }}>
                  {traktStats?.lastHistorySync
                    ? formatDistanceToNow(new Date(traktStats.lastHistorySync), { addSuffix: true })
                    : 'Never'}
                  {traktStats && (
                    <> · {traktStats.watched.movies} movies, {traktStats.watched.episodes} episodes mirrored</>
                  )}
                </div>
              </span>
              <button
                className={`${styles.actionBtn} ${styles.actionBtnAccent}`}
                onClick={handleTraktSyncNow}
                disabled={traktSyncingNow}
              >
                {traktSyncingNow ? 'Syncing…' : 'Sync now'}
              </button>
            </div>

            {/* QUEUE group — only renders when something is queued */}
            {traktStatus.queueCount > 0 && (
              <>
                <div style={{ fontSize: 'var(--text-xs)', textTransform: 'uppercase', letterSpacing: 1, color: 'var(--text-muted)', marginTop: 'var(--space-3)', marginBottom: 4 }}>
                  Queue
                </div>
                <div className={styles.statRow}>
                  <span style={{ color: 'var(--accent)' }}>
                    {traktStatus.queueCount} pending event{traktStatus.queueCount === 1 ? '' : 's'}
                  </span>
                  <button
                    className={styles.actionBtn}
                    onClick={handleDrainTraktQueue}
                    disabled={traktDraining}
                  >
                    {traktDraining ? 'Retrying…' : 'Retry now'}
                  </button>
                </div>
              </>
            )}
          </>
        )}

        <details style={{ marginTop: 'var(--space-3)' }}>
          <summary
            style={{
              cursor: 'pointer',
              fontSize: 'var(--text-xs)',
              color: 'var(--text-muted)',
              padding: '4px 0',
              userSelect: 'none',
            }}
          >
            Advanced
          </summary>
          <div style={{ paddingTop: 'var(--space-2)' }}>
            <div style={{ fontSize: 'var(--text-xs)', color: 'var(--text-muted)', paddingBottom: 'var(--space-2)' }}>
              Override the bundled Trakt application credentials. Register an app at
              {' '}<a
                href="#"
                onClick={(e) => { e.preventDefault(); window.api.trakt.openVerification('https://trakt.tv/oauth/applications'); }}
                style={{ color: 'var(--accent)' }}
              >trakt.tv/oauth/applications</a>
              {' '}with redirect URI{' '}<code>urn:ietf:wg:oauth:2.0:oob</code>. Leave blank to use the bundled defaults.
              {traktStatus?.connected && (
                <div style={{ marginTop: 4, color: 'var(--accent)' }}>
                  Disconnect first if you change these — current tokens will become invalid.
                </div>
              )}
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-2)' }}>
              <input
                type="text"
                placeholder="client_id (override)"
                value={traktDraftId}
                onChange={(e) => setTraktDraftId(e.target.value)}
                style={{
                  padding: '6px 10px',
                  fontSize: 'var(--text-sm)',
                  background: 'var(--bg-tertiary)',
                  border: '1px solid var(--border)',
                  borderRadius: 'var(--radius)',
                  color: 'var(--text-primary)',
                  fontFamily: 'var(--font-mono, Consolas, monospace)',
                }}
              />
              <input
                type="password"
                placeholder="client_secret (override)"
                value={traktDraftSecret}
                onChange={(e) => setTraktDraftSecret(e.target.value)}
                style={{
                  padding: '6px 10px',
                  fontSize: 'var(--text-sm)',
                  background: 'var(--bg-tertiary)',
                  border: '1px solid var(--border)',
                  borderRadius: 'var(--radius)',
                  color: 'var(--text-primary)',
                  fontFamily: 'var(--font-mono, Consolas, monospace)',
                }}
              />
              <div style={{ display: 'flex', gap: 'var(--space-2)', alignItems: 'center' }}>
                <button className={styles.actionBtn} onClick={handleSaveTraktAdvanced}>
                  Save credentials
                </button>
                {traktAdvancedSaved && (
                  <span style={{ fontSize: 'var(--text-xs)', color: 'var(--accent)' }}>Saved</span>
                )}
              </div>
            </div>
          </div>
        </details>
      </div>

      {/* Subtitles */}
      <div className={styles.section}>
        <div className={styles.sectionHeader}>Subtitles</div>
        <SettingsRow label="Font">
          <Select
            value={settings.subtitleFont}
            options={FONT_OPTIONS}
            onChange={(v) => handleSettingChange('subtitleFont', v)}
          />
        </SettingsRow>
        <SettingsRow label="Size">
          <Slider
            value={settings.subtitleSize}
            min={20}
            max={80}
            step={2}
            onChange={(v) => handleSettingChange('subtitleSize', v)}
          />
        </SettingsRow>
        <SettingsRow label="Color">
          <Select
            value={settings.subtitleColor}
            options={SUB_COLOR_OPTIONS}
            onChange={(v) => handleSettingChange('subtitleColor', v)}
          />
        </SettingsRow>
        <SettingsRow label="Border Size">
          <Slider
            value={settings.subtitleBorderSize}
            min={0}
            max={5}
            step={0.5}
            onChange={(v) => handleSettingChange('subtitleBorderSize', v)}
          />
        </SettingsRow>
        <SettingsRow label="Background">
          <Select
            value={settings.subtitleBackground}
            options={SUB_BG_OPTIONS}
            onChange={(v) => handleSettingChange('subtitleBackground', v)}
          />
        </SettingsRow>
        <SettingsRow label="Position" description="Higher = lower on screen">
          <Slider
            value={settings.subtitlePosition}
            min={50}
            max={100}
            step={1}
            label={`${settings.subtitlePosition}%`}
            onChange={(v) => handleSettingChange('subtitlePosition', v)}
          />
        </SettingsRow>
      </div>

      {/* Performance */}
      <div className={styles.section}>
        <div className={styles.sectionHeader}>Performance</div>
        <SettingsRow
          label="Power Mode"
          description={
            (settings.powerMode || 'balanced') === 'performance'
              ? 'Full animations and updates even when minimized'
              : (settings.powerMode || 'balanced') === 'efficiency'
                ? 'Aggressive throttling when the window is hidden'
                : 'Pauses non-essential work when minimized'
          }
        >
          <Select
            value={settings.powerMode || 'balanced'}
            options={POWER_MODE_OPTIONS}
            onChange={(v) => handleSettingChange('powerMode', v)}
          />
        </SettingsRow>
      </div>

      {/* Cache & Sync */}
      <div className={styles.section}>
        <div className={styles.sectionHeader}>Cache & Sync</div>
        {stats && (
          <>
            <div className={styles.statRow}>
              <span>Items indexed</span>
              <span className={styles.statValue}>{stats.totalItems.toLocaleString()}</span>
            </div>
            {dedupStats && dedupStats.groupCount > 0 && (
              <>
                <div className={styles.statRow}>
                  <span>Dedup groups</span>
                  <span className={styles.statValue}>
                    {dedupStats.groupCount.toLocaleString()} ({dedupStats.mergedItems.toLocaleString()} items merged)
                  </span>
                </div>
                <div className={styles.statRow}>
                  <span>Unique items after dedup</span>
                  <span className={styles.statValue}>
                    {(stats.totalItems - dedupStats.mergedItems + dedupStats.groupCount).toLocaleString()}
                  </span>
                </div>
              </>
            )}
            <div className={styles.statRow}>
              <span>Last sync</span>
              <span className={styles.statValue}>
                {stats.lastSyncTime
                  ? formatDistanceToNow(new Date(stats.lastSyncTime), { addSuffix: true })
                  : 'Never'}
              </span>
            </div>
            <div className={styles.statRow}>
              <span>Last dedup</span>
              <span
                className={styles.statValue}
                style={dedupInfo?.dedupStatus === 'failed' ? { color: 'var(--danger)' } : undefined}
              >
                {dedupInfo?.dedupStatus === 'failed'
                  ? 'Failed'
                  : dedupInfo?.dedupStatus === 'in-progress'
                    ? 'In progress…'
                    : dedupInfo?.lastDedupBuild
                      ? formatDistanceToNow(new Date(dedupInfo.lastDedupBuild), { addSuffix: true })
                      : 'Never'}
              </span>
            </div>
          </>
        )}
        <div className={styles.serverActions}>
          <button
            className={`${styles.actionBtn} ${styles.actionBtnAccent}`}
            onClick={handleSyncNow}
            disabled={syncRunning}
          >
            {syncRunning ? 'Syncing...' : 'Sync Now'}
          </button>
          <button
            className={`${styles.actionBtn} ${styles.actionBtnDanger}`}
            onClick={handleClearCache}
          >
            Clear Cache
          </button>
          <button
            className={styles.actionBtn}
            onClick={handleRebuildDedup}
            disabled={rebuildingDedup}
          >
            {rebuildingDedup ? 'Rebuilding...' : 'Rebuild Dedup Index'}
          </button>
        </div>
        <SettingsRow label="Sync on startup">
          <Toggle
            value={settings.syncOnStartup}
            onChange={(v) => handleSettingChange('syncOnStartup', v)}
          />
        </SettingsRow>
        <SettingsRow label="Image cache limit">
          <Select
            value={String(settings.imageCacheMaxMB)}
            options={CACHE_SIZE_OPTIONS}
            onChange={(v) => handleSettingChange('imageCacheMaxMB', Number(v))}
          />
        </SettingsRow>

        <div className={styles.sectionHeader} style={{ marginTop: 24 }}>Danger Zone</div>
        <div className={styles.statRow}>
          <span>
            Reset App
            <div style={{ fontSize: 12, opacity: 0.65, marginTop: 2 }}>
              Deletes all servers, cache, settings, and downloaded images, then restarts the app.
            </div>
          </span>
          <button
            className={`${styles.actionBtn} ${styles.actionBtnDanger}`}
            onClick={() => setShowResetConfirm(true)}
            disabled={resetting}
          >
            {resetting ? 'Resetting...' : 'Reset App'}
          </button>
        </div>
      </div>

      {/* About */}
      <div className={styles.section}>
        <div className={styles.sectionHeader}>About</div>
        <div className={styles.aboutInfo}>
          <div className={styles.aboutVersion}>Nocturne v3.0.0</div>
          <div className={styles.aboutLine}>Desktop Emby client built with Electron + React + mpv</div>
          <div className={styles.aboutUpdate}>
            {updateStatus?.state === 'available' && (
              <span className={styles.updateAvailable}>v{updateStatus.info?.version} available</span>
            )}
            {updateStatus?.state === 'downloaded' && (
              <span className={styles.updateAvailable}>v{updateStatus.info?.version} ready to install</span>
            )}
            {updateStatus?.state === 'checking' && (
              <span className={styles.aboutLine}>Checking for updates...</span>
            )}
            <button
              className={styles.checkUpdateBtn}
              onClick={() => window.api.updater.check()}
              disabled={updateStatus?.state === 'checking' || updateStatus?.state === 'downloading'}
            >
              Check for Updates
            </button>
          </div>
        </div>
      </div>

      {/* Saved toast */}
      <div className={`${styles.savedToast} ${savedVisible ? styles.visible : ''}`}>
        Settings saved
      </div>

      {/* Add Server Modal */}
      {showAddServer && (
        <AddServerModal
          initialUrl={reloginUrl ?? undefined}
          onClose={() => { setShowAddServer(false); setReloginUrl(null); }}
          onServerAdded={handleServerAdded}
        />
      )}

      {/* Remove Server Confirmation */}
      {removeConfirm && (
        <ConfirmDialog
          title="Remove Server?"
          message={`This will remove "${removeConfirm.name}" and delete all cached data for this server.`}
          confirmLabel="Remove"
          danger
          onConfirm={confirmRemoveServer}
          onCancel={() => setRemoveConfirm(null)}
        />
      )}

      {/* Full Reset Confirmation */}
      {showResetConfirm && (
        <ConfirmDialog
          title="Reset App?"
          message="This will permanently delete all servers, cached data, settings, and downloaded images, then restart Nocturne. This cannot be undone."
          confirmLabel="Reset Everything"
          danger
          onConfirm={handleResetFull}
          onCancel={() => setShowResetConfirm(false)}
        />
      )}

      {/* Trakt OAuth Modal */}
      {showTraktModal && (
        <TraktAuthModal
          onClose={() => setShowTraktModal(false)}
          onSuccess={() => {
            setShowTraktModal(false);
            loadTraktStatus();
          }}
        />
      )}

      {/* Trakt initial-pull preview (auto-opens after auth-success) */}
      {showTraktPreview && (
        <TraktHistoryPreviewModal
          username={traktStatus?.username ?? null}
          onClose={() => setShowTraktPreview(false)}
          onApplied={() => loadTraktStatus()}
        />
      )}

      {/* Trakt Disconnect Confirmation */}
      {showTraktDisconnect && (
        <ConfirmDialog
          title="Disconnect Trakt?"
          message="Nocturne will stop scrobbling, drop the watchlist sidebar entry, and discard any queued events. You can reconnect later."
          confirmLabel="Disconnect"
          danger
          onConfirm={handleTraktConfirmDisconnect}
          onCancel={() => setShowTraktDisconnect(false)}
        />
      )}
    </div>
  );
}
