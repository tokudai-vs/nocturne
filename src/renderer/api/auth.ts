import type { EmbyServerInfo, AuthResult, PublicUser, IpcResponse } from './types';

export async function connectToServer(url: string): Promise<IpcResponse<EmbyServerInfo>> {
  return window.api.auth.connectToServer(url);
}

export async function getPublicUsers(): Promise<IpcResponse<PublicUser[]>> {
  return window.api.auth.getPublicUsers();
}

export async function login(
  username: string,
  password: string,
): Promise<IpcResponse<AuthResult>> {
  return window.api.auth.login(username, password);
}

export async function logout(): Promise<IpcResponse<void>> {
  return window.api.auth.logout();
}
