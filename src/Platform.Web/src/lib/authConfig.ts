import { buildApiUrl } from './runtimeConfig';

interface AuthConfig {
  mode: string; // "msal", "local", or future types
  clientId: string;
  tenantId: string;
}

let cached: AuthConfig = { mode: 'none', clientId: '', tenantId: '' };

/**
 * Fetch auth config from the backend. Call once at startup (before React mounts).
 * The backend decides the auth mode based on its own configuration.
 */
export async function loadAuthConfig(): Promise<void> {
  try {
    const response = await fetch(buildApiUrl('/auth/config'), { cache: 'no-store' });
    if (response.ok) {
      cached = await response.json();
    }
  } catch {
    // Backend unreachable — fall back to no auth
    cached = { mode: 'none', clientId: '', tenantId: '' };
  }
}

export function getAuthMode(): string {
  return cached.mode;
}

export function isMsalEnabled(): boolean {
  return cached.mode === 'msal' && Boolean(cached.clientId);
}

export function isLocalAuthEnabled(): boolean {
  return cached.mode === 'local';
}

export function getAuthClientId(): string {
  return cached.clientId;
}

export function getAuthTenantId(): string {
  return cached.tenantId;
}
