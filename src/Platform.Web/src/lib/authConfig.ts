import { buildApiUrl } from './runtimeConfig';

interface AuthConfig {
  mode: string; // "msal", "local", or future types
  clientId: string;
  tenantId: string;
}

let cached: AuthConfig = { mode: 'none', clientId: '', tenantId: '' };

// Dev keeps the old instant fallback so the shell still loads without a backend running.
const CONFIG_ATTEMPTS = import.meta.env.DEV ? 1 : 5;

/**
 * Fetch auth config from the backend. Call once at startup (before React mounts).
 * The backend decides the auth mode based on its own configuration.
 *
 * Network errors and 5xx (the API restarting behind the proxy) are retried with backoff. Returns
 * false if the config still couldn't be loaded: outside dev the caller shows an error screen, since
 * guessing mode 'none' would hand every visitor the fake dev admin.
 */
export async function loadAuthConfig(): Promise<boolean> {
  for (let attempt = 1; ; attempt++) {
    let retryable = true;
    try {
      const response = await fetch(buildApiUrl('/auth/config'), { cache: 'no-store' });
      if (response.ok) {
        cached = await response.json();
        return true;
      }
      retryable = response.status >= 500;
    } catch {
      // Backend unreachable (or not JSON) — retry
    }
    if (!retryable || attempt >= CONFIG_ATTEMPTS) break;
    await new Promise((resolve) => setTimeout(resolve, 500 * 2 ** (attempt - 1)));
  }

  if (import.meta.env.DEV) {
    // No backend in dev — fall back to no auth
    cached = { mode: 'none', clientId: '', tenantId: '' };
    return true;
  }
  return false;
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
