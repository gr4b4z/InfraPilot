import { buildApiUrl } from './runtimeConfig';
import { fetchJson, type ConnectionProblem } from './connection';

interface AuthConfig {
  mode: string; // "msal", "local", or future types
  clientId: string;
  tenantId: string;
}

let cached: AuthConfig = { mode: 'none', clientId: '', tenantId: '' };

/**
 * Fetch auth config from the backend. Call before rendering anything that reads it (see
 * `StartupGate`). The backend decides the auth mode based on its own configuration.
 *
 * Returns why it failed, or null. A failure leaves the mode at `none`; it used to be taken as the
 * answer, which signed everyone in as the built-in dev user against an API that wasn't there.
 */
export async function loadAuthConfig(): Promise<ConnectionProblem | null> {
  const result = await fetchJson<AuthConfig>(buildApiUrl('/auth/config'), { cache: 'no-store' });
  if (!result.ok) return result.problem;
  cached = result.data;
  return null;
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
