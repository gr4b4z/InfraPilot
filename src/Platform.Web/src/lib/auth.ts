import type { Configuration } from '@azure/msal-browser';
import { PublicClientApplication } from '@azure/msal-browser';

const clientId = import.meta.env.VITE_AZURE_CLIENT_ID || '';
const tenantId = import.meta.env.VITE_AZURE_TENANT_ID || '';

export const isMsalEnabled = Boolean(clientId && !clientId.startsWith('<'));

const msalConfig: Configuration = {
  auth: {
    clientId: clientId || 'placeholder',
    authority: `https://login.microsoftonline.com/${tenantId || 'common'}`,
    redirectUri: window.location.origin,
  },
  cache: {
    cacheLocation: 'sessionStorage',
    storeAuthStateInCookie: false,
  },
};

export const msalInstance = isMsalEnabled ? new PublicClientApplication(msalConfig) : null;

export const loginRequest = {
  scopes: isMsalEnabled ? [`api://${clientId}/access_as_user`] : [],
};

export async function acquireToken(): Promise<string | null> {
  if (!isMsalEnabled || !msalInstance) return null;

  const accounts = msalInstance.getAllAccounts();
  if (accounts.length === 0) return null;

  try {
    const result = await msalInstance.acquireTokenSilent({
      ...loginRequest,
      account: accounts[0],
    });
    return result.accessToken;
  } catch {
    try {
      const result = await msalInstance.acquireTokenPopup(loginRequest);
      return result.accessToken;
    } catch {
      return null;
    }
  }
}
