import type { Configuration, PopupRequest } from '@azure/msal-browser';
import { PublicClientApplication } from '@azure/msal-browser';
import { getAzureClientId, getAzureTenantId } from './runtimeConfig';

// MSAL is initialised lazily the first time any caller asks for it, so we pick up
// values populated by `loadRuntimeConfig()` (which runs before React mounts in
// `main.tsx`). That means the same built image can be retargeted at a different
// tenant by editing `/config.json` at container start — no rebuild needed.
interface MsalState {
  enabled: boolean;
  instance: PublicClientApplication | null;
  loginRequest: PopupRequest;
}

let cached: MsalState | null = null;

function init(): MsalState {
  if (cached) return cached;

  const clientId = getAzureClientId();
  const tenantId = getAzureTenantId();
  const enabled = Boolean(clientId && !clientId.startsWith('<'));

  if (!enabled) {
    cached = { enabled: false, instance: null, loginRequest: { scopes: [] } };
    return cached;
  }

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
