import type { Configuration, PopupRequest } from '@azure/msal-browser';
import { PublicClientApplication } from '@azure/msal-browser';
import {
  isMsalEnabled as checkMsal,
  getAuthClientId,
  getAuthTenantId,
} from './authConfig';

// MSAL is initialised lazily the first time any caller asks for it, using
// auth config fetched from the backend at startup (`loadAuthConfig()` in main.tsx).
// The backend decides whether MSAL is active and supplies clientId/tenantId,
// so the same built image works across tenants with no rebuild.
interface MsalState {
  enabled: boolean;
  instance: PublicClientApplication | null;
  loginRequest: PopupRequest;
}

let cached: MsalState | null = null;

function init(): MsalState {
  if (cached) return cached;

  const enabled = checkMsal();
  if (!enabled) {
    cached = { enabled: false, instance: null, loginRequest: { scopes: [] } };
    return cached;
  }

  const clientId = getAuthClientId();
  const tenantId = getAuthTenantId();

  const msalConfig: Configuration = {
    auth: {
      clientId,
      authority: `https://login.microsoftonline.com/${tenantId || 'common'}`,
      redirectUri: window.location.origin,
    },
    cache: {
      cacheLocation: 'sessionStorage',
      storeAuthStateInCookie: false,
    },
  };

  cached = {
    enabled: true,
    instance: new PublicClientApplication(msalConfig),
    loginRequest: { scopes: [`api://${clientId}/access_as_user`] },
  };
  return cached;
}

export function isMsalEnabled(): boolean {
  return init().enabled;
}

export function getMsalInstance(): PublicClientApplication | null {
  return init().instance;
}

export function getLoginRequest(): PopupRequest {
  return init().loginRequest;
}

export async function logout(): Promise<void> {
  const { enabled, instance } = init();
  if (!enabled || !instance) return;
  const account = instance.getAllAccounts()[0];
  await instance.logoutRedirect({
    account,
    postLogoutRedirectUri: window.location.origin,
  });
}

// Guards against firing multiple concurrent redirects (e.g. several parallel API
// calls all 401-ing at once), which MSAL rejects with "interaction_in_progress".
let redirecting = false;

/**
 * Force an interactive, full-page redirect to re-authenticate. Use when silent
 * token acquisition can't recover (expired session / refresh token) or the API
 * returns 401. A redirect — not a popup — is essential: popups triggered from a
 * background fetch are blocked, and a plain reload won't recover because the
 * stale account persists in sessionStorage, so the redirect guard in AuthProvider
 * never re-fires. This navigates the page away and resolves only nominally.
 */
export async function reauthenticate(): Promise<void> {
  const { enabled, instance, loginRequest } = init();
  if (!enabled || !instance || redirecting) return;
  redirecting = true;
  const account = instance.getAllAccounts()[0];
  await instance.acquireTokenRedirect({ ...loginRequest, account });
}

export async function acquireToken(): Promise<string | null> {
  const { enabled, instance, loginRequest } = init();
  if (!enabled || !instance) return null;

  const accounts = instance.getAllAccounts();
  if (accounts.length === 0) return null;

  try {
    const result = await instance.acquireTokenSilent({
      ...loginRequest,
      account: accounts[0],
    });
    return result.accessToken;
  } catch {
    // Silent renewal failed — recover via a full-page redirect rather than a
    // (blocked) popup. Returns null while the browser navigates to the IdP.
    await reauthenticate();
    return null;
  }
}
