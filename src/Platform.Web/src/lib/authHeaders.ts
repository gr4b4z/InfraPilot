import { acquireToken, isMsalEnabled } from './auth';
import { isLocalAuthEnabled } from './authConfig';
import { getStoredToken } from './localAuth';

/**
 * Resolves the bearer token for the configured auth mode: Entra via MSAL in production, the local
 * JWT in development, and an explicitly-set token otherwise.
 *
 * Shared by the API client and the assistant's own fetches. Those two used to differ — the agent
 * endpoint was anonymous and its callers sent no token at all — so any caller reaching the backend
 * goes through here to keep them from drifting apart again.
 */
export async function resolveAuthToken(fallback?: string | null): Promise<string | null> {
  if (isMsalEnabled()) return await acquireToken();
  if (isLocalAuthEnabled()) return getStoredToken();
  return fallback ?? null;
}

/** Request headers including Authorization when a token is available. */
export async function authHeaders(
  extra: Record<string, string> = {},
  fallbackToken?: string | null,
): Promise<Record<string, string>> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...extra,
  };

  const token = await resolveAuthToken(fallbackToken);
  if (token) headers['Authorization'] = `Bearer ${token}`;

  return headers;
}
