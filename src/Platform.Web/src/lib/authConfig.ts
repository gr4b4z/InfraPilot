import { buildApiUrl } from './runtimeConfig';
import { fetchJson, type ConnectionProblem } from './connection';

interface AuthConfig {
  mode: string; // "msal", "local", or future types
  clientId: string;
  tenantId: string;
}

let cached: AuthConfig = { mode: 'none', clientId: '', tenantId: '' };

// Dev keeps the old instant fallback so the shell still loads without a backend running.
const CONFIG_ATTEMPTS = import.meta.env.DEV ? 1 : 5;

/**
 * Fetch auth config from the backend. Call before rendering anything that reads it (see
 * `StartupGate`). The backend decides the auth mode based on its own configuration.
 *
 * Network errors, non-JSON answers and 5xx (the API restarting behind the proxy) are retried with
 * a short backoff, so a restart blip doesn't flash an error screen. A timeout isn't — it has already
 * waited long enough. Returns why it still failed, or null. Outside dev the caller then shows the
 * problem, since guessing mode 'none' would hand every visitor the fake dev admin.
 */
export async function loadAuthConfig(): Promise<ConnectionProblem | null> {
  for (let attempt = 1; ; attempt++) {
    const result = await fetchJson<AuthConfig>(buildApiUrl('/auth/config'), { cache: 'no-store' });
    if (result.ok) {
      cached = result.data;
      return null;
    }
    const { problem } = result;
    const retryable =
      problem.kind !== 'timeout' && !(problem.kind === 'http-error' && (problem.status ?? 0) < 500);
    if (!retryable || attempt >= CONFIG_ATTEMPTS) {
      if (import.meta.env.DEV) {
        // No backend in dev — fall back to no auth
        cached = { mode: 'none', clientId: '', tenantId: '' };
        return null;
      }
      return problem;
    }
    await new Promise((resolve) => setTimeout(resolve, 500 * 2 ** (attempt - 1)));
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
