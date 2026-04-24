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
    try {
      const result = await instance.acquireTokenPopup(loginRequest);
      return result.accessToken;
    } catch {
      return null;
    }
  }
}
