import { randomUUID } from 'crypto';
import { embyClient } from './emby-client';
import { getSettings, setSetting, type ServerConfig, type NocturneSettings, type CombinedMapping } from './settings';
import { clearServerData, getSyncState, getDistinctLibrariesFromItems } from './database';
import { syncEngine } from './sync-engine';

class ServerManager {
  addServer(config: Omit<ServerConfig, 'id' | 'addedAt' | 'lastConnected'>): ServerConfig {
    const settings = getSettings();
    const existing = settings.servers.find(
      (s) => s.url === config.url && s.userId === config.userId,
    );

    if (existing) {
      // Update existing server entry
      existing.accessToken = config.accessToken;
      existing.name = config.name;
      existing.version = config.version;
      existing.username = config.username;
      existing.lastConnected = new Date().toISOString();
      setSetting('servers', settings.servers);
      if (!settings.activeServerId) {
        setSetting('activeServerId', existing.id);
      }
      return existing;
    }

    const server: ServerConfig = {
      ...config,
      id: randomUUID().slice(0, 8),
      addedAt: new Date().toISOString(),
      lastConnected: new Date().toISOString(),
    };

    const servers = [...settings.servers, server];
    setSetting('servers', servers);
    if (!settings.activeServerId || settings.servers.length === 0) {
      setSetting('activeServerId', server.id);
    }

    return server;
  }

  removeServer(serverId: string): void {
    const settings = getSettings();
    const servers = settings.servers.filter((s) => s.id !== serverId);
    setSetting('servers', servers);

    // Clear this server's cached data
    clearServerData(serverId);

    // Remove library mappings for this server
    const mappings = { ...settings.libraryMappings };
    delete mappings[serverId];
    setSetting('libraryMappings', mappings);

    // Clean combined mappings — remove all libraries belonging to this server
    const combined = settings.combinedMappings || {};
    const cleaned: Record<string, CombinedMapping> = {};
    for (const [groupId, group] of Object.entries(combined)) {
      const filteredLibraries = group.libraries.filter(
        (lib) => lib.serverId !== serverId,
      );
      if (filteredLibraries.length > 0) {
        cleaned[groupId] = { ...group, libraries: filteredLibraries };
      }
    }
    setSetting('combinedMappings', cleaned);

    // If we removed the active server, clear it
    if (settings.activeServerId === serverId) {
      setSetting('activeServerId', servers.length > 0 ? servers[0].id : null);
    }
  }

  getServers(): ServerConfig[] {
    return getSettings().servers;
  }

  getActiveServer(): ServerConfig | null {
    const settings = getSettings();
    if (!settings.activeServerId) return null;
    return settings.servers.find((s) => s.id === settings.activeServerId) ?? null;
  }

  getActiveServerId(): string {
    return getSettings().activeServerId || 'default';
  }

  async switchServer(serverId: string): Promise<boolean> {
    const settings = getSettings();
    const server = settings.servers.find((s) => s.id === serverId);
    if (!server) return false;

    // Update active server
    setSetting('activeServerId', serverId);

    // Update last connected
    server.lastConnected = new Date().toISOString();
    setSetting('servers', settings.servers);

    // Re-initialize EmbyClient
    embyClient.setServer(server.url);
    embyClient.setAuth(server.accessToken, server.userId);

    // Validate the token still works
    try {
      await embyClient.getCurrentUser();
    } catch {
      return false;
    }

    // Trigger sync for this server
    syncEngine.startIncrementalSync();

    return true;
  }

  /** Get library mappings for the active server */
  getActiveLibraryMappings(): Record<string, { name: string; icon: string; libraryIds: string[] }> {
    const settings = getSettings();
    const serverId = settings.activeServerId;
    if (!serverId) return {};

    const perServer = settings.libraryMappings[serverId];
    if (perServer) return perServer;

    // Migration: if libraryMappings has flat data (v1 format with groupId keys
    // that have name/icon/libraryIds), migrate it under the current serverId
    const keys = Object.keys(settings.libraryMappings);
    if (keys.length > 0) {
      const firstVal = settings.libraryMappings[keys[0]] as unknown;
      if (firstVal && typeof firstVal === 'object' && 'libraryIds' in (firstVal as Record<string, unknown>)) {
        // This is flat v1 data — migrate it
        const flat = settings.libraryMappings as unknown as Record<string, { name: string; icon: string; libraryIds: string[] }>;
        this.setActiveLibraryMappings(flat);
        return flat;
      }
    }

    return {};
  }

  /** Set library mappings for the active server */
  setActiveLibraryMappings(
    mappings: Record<string, { name: string; icon: string; libraryIds: string[] }>,
  ): void {
    const settings = getSettings();
    const serverId = settings.activeServerId;
    if (!serverId) return;
    const allMappings = { ...settings.libraryMappings };
    allMappings[serverId] = mappings;
    setSetting('libraryMappings', allMappings as NocturneSettings['libraryMappings']);
  }

  // ── Combined mode ──────────────────────────────────

  getLibraryMode(): 'separate' | 'combined' {
    return getSettings().libraryMode || 'separate';
  }

  isCombinedMode(): boolean {
    return this.getLibraryMode() === 'combined';
  }

  getCombinedMappings(): Record<string, CombinedMapping> {
    return getSettings().combinedMappings || {};
  }

  setCombinedMappings(mappings: Record<string, CombinedMapping>): void {
    setSetting('combinedMappings', mappings);
    // Any explicit save from the user (including clearing all groups) counts as initialization.
    // Prevents the Settings page from auto-regenerating groups the user deleted.
    setSetting('combinedMappingsInitialized', true);
  }

  /** Get a specific server by ID */
  getServer(serverId: string): ServerConfig | null {
    return getSettings().servers.find((s) => s.id === serverId) ?? null;
  }

  /** Get all library IDs from all servers. Prefers sync_state; falls back to
   * distinct rows in the items table so we still surface libraries when
   * sync_state hasn't been written yet (e.g. early in a cold start). */
  getAllServerLibraries(): { serverId: string; serverName: string; libraryId: string; libraryName: string }[] {
    const servers = this.getServers();
    const serverIds = new Set(servers.map((s) => s.id));
    const seen = new Set<string>();
    const result: { serverId: string; serverName: string; libraryId: string; libraryName: string }[] = [];

    for (const server of servers) {
      const libsRaw = getSyncState(`libraries_${server.id}`) || getSyncState('libraries');
      if (!libsRaw) continue;
      try {
        const libs: { Id: string; Name: string }[] = JSON.parse(libsRaw);
        for (const lib of libs) {
          const key = `${server.id}::${lib.Id}`;
          if (seen.has(key)) continue;
          seen.add(key);
          result.push({
            serverId: server.id,
            serverName: server.name,
            libraryId: lib.Id,
            libraryName: lib.Name,
          });
        }
      } catch {
        // Skip invalid JSON
      }
    }

    // Fallback: fill in anything the items table knows about but sync_state doesn't.
    // Only include rows whose serverId is still a configured server.
    const fromItems = getDistinctLibrariesFromItems();
    for (const row of fromItems) {
      if (!serverIds.has(row.serverId)) continue;
      const key = `${row.serverId}::${row.libraryId}`;
      if (seen.has(key)) continue;
      seen.add(key);
      const server = this.getServer(row.serverId);
      result.push({
        serverId: row.serverId,
        serverName: server?.name ?? row.serverId,
        libraryId: row.libraryId,
        libraryName: row.libraryName,
      });
    }

    return result;
  }
}

export const serverManager = new ServerManager();
