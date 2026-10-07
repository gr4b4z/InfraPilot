import type { AccountInfo, Configuration, PopupRequest } from '@azure/msal-browser';
import {
  AuthError,
  BrowserAuthErrorCodes,
  InteractionRequiredAuthError,
  PublicClientApplication,
} from '@azure/msal-browser';
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
      // Also where silent iframe renewal lands; main.tsx relays that response back to the
      // waiting frame, so no separate redirect page has to be registered.
      redirectUri: window.location.origin,
    },
    cache: {
      // Shared by every tab, so a new tab starts signed in instead of making its own Entra round
      // trip; MSAL syncs the tabs' caches. Entries are encrypted with a key kept in a session
      // cookie, so they become unreadable when the browser session ends — but until then anyone
      // using the same browser profile stays signed in. XSS is no worse off: page script could
      // already get tokens via acquireTokenSilent.
      cacheLocation: 'localStorage',
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

/** The signed-in account: the active one, falling back to the first cached. */
export function getActiveAccount(): AccountInfo | null {
  const { instance } = init();
  if (!instance) return null;
  return instance.getActiveAccount() ?? instance.getAllAccounts()[0] ?? null;
}

/**
 * Initialise MSAL and finish any sign-in redirect that brought the browser back here.
 * Must complete before anything asks for a token.
 */
export async function initializeMsal(): Promise<void> {
  const { instance } = init();
  if (!instance) return;
  await instance.initialize();
  const result = await instance.handleRedirectPromise();
  // Pin the account that just signed in (or the cached one) as active, so token calls in every
  // tab — the active account lives in the shared cache — agree on who is signed in.
  const account = result?.account ?? getActiveAccount();
  if (account && instance.getActiveAccount()?.homeAccountId !== account.homeAccountId) {
    instance.setActiveAccount(account);
  }
}

export async function logout(): Promise<void> {
  const { enabled, instance } = init();
  if (!enabled || !instance) return;
  await instance.logoutRedirect({
    account: getActiveAccount() ?? undefined,
    postLogoutRedirectUri: window.location.origin,
  });
}

const SILENT_ATTEMPTS = 2;
const SILENT_RETRY_DELAY_MS = 1000;

// The hidden-iframe renewal itself failed (no answer came back through the redirect bridge). Once
// the refresh token is gone that iframe is the only silent path left, so if it keeps failing,
// retrying silently forever would leave the tab with failing requests and no way to sign in.
const SILENT_FRAME_FAILURES: ReadonlySet<string> = new Set([
  BrowserAuthErrorCodes.timedOut,
  BrowserAuthErrorCodes.iframeClosedPrematurely,
  BrowserAuthErrorCodes.redirectBridgeEmptyResponse,
]);

/**
 * A token without user interaction, or null when only the user can fix it: no account, Entra
 * answered interaction_required (refresh token and session expired, consent, MFA), or the silent
 * iframe failed on the retry too. Anything else — a network error, Entra 429/5xx — is transient
 * and a redirect wouldn't fix it: retry once after a short pause, then throw so only this request
 * fails and the next one starts over.
 */
async function silentToken(instance: PublicClientApplication, forceRefresh: boolean): Promise<string | null> {
  for (let attempt = 1; ; attempt++) {
    const account = getActiveAccount();
    if (!account) return null;
    try {
      const result = await instance.acquireTokenSilent({ ...getLoginRequest(), account, forceRefresh });
      return result.accessToken;
    } catch (err) {
      if (err instanceof InteractionRequiredAuthError) return null;
      if (attempt >= SILENT_ATTEMPTS) {
        if (err instanceof AuthError && SILENT_FRAME_FAILURES.has(err.errorCode)) return null;
        throw err;
      }
      await new Promise((resolve) => setTimeout(resolve, SILENT_RETRY_DELAY_MS));
    }
  }
}

function whenVisible(): Promise<void> {
  if (document.visibilityState === 'visible') return Promise.resolve();
  return new Promise((resolve) => {
    const onChange = () => {
      if (document.visibilityState !== 'visible') return;
      document.removeEventListener('visibilitychange', onChange);
      resolve();
    };
    document.addEventListener('visibilitychange', onChange);
  });
}

async function redirectToSignIn(instance: PublicClientApplication): Promise<void> {
  // Passing the known account lets Entra skip the account picker (login_hint / sid).
  const redirect = () =>
    instance.acquireTokenRedirect({ ...getLoginRequest(), account: getActiveAccount() ?? undefined });
  try {
    await redirect();
  } catch (err) {
    if (!(err instanceof AuthError) || err.errorCode !== BrowserAuthErrorCodes.interactionInProgress) {
      throw err;
    }
    // This tab's previous interaction hasn't finished — a redirect response still being processed.
    // handleRedirectPromise hands back that in-flight promise; once it settles, sign-in may have
    // completed already, otherwise try the redirect once more.
    await instance.handleRedirectPromise().catch(() => null);
    if (await silentToken(instance, false)) return;
    await redirect();
  }
}

// One interactive sign-in per tab at a time, however many requests ask for it (MSAL rejects a
// second one with "interaction_in_progress"). Cleared once it settles — in practice only when the
// redirect failed, since a started one navigates away — so a later call can try again.
let signIn: Promise<void> | null = null;

/**
 * Sign in interactively with a full-page redirect — a popup triggered from a background fetch
 * would be blocked. Use when silent token acquisition can't recover or the API keeps returning 401.
 *
 * Only the tab the user is looking at redirects. A hidden tab waits until it is shown and first
 * checks the shared cache: usually another tab has signed in by then, so it carries on without
 * its own Entra round trip — with 30 tabs open, one sign-in instead of 30. Resolves only in that
 * case; otherwise the page navigates away, or the promise rejects if the redirect couldn't start.
 *
 * (navigator.locks can't coordinate this across tabs: a lock is released as soon as its page
 * navigates to Entra, long before the sign-in it guards has finished.)
 */
export function reauthenticate(): Promise<void> {
  const { enabled, instance } = init();
  if (!enabled || !instance) return Promise.resolve();
  signIn ??= (async () => {
    if (document.visibilityState !== 'visible') {
      await whenVisible();
      if (await silentToken(instance, false)) return;
    }
    await redirectToSignIn(instance);
  })().finally(() => {
    signIn = null;
  });
  return signIn;
}

/**
 * Bearer token for the API. Redirects to sign in only when Entra needs the user (see
 * `reauthenticate`); transient failures throw instead, so callers fail the one request.
 * `forceRefresh` skips the cached access token, for when the API rejected it.
 */
export async function acquireToken({ forceRefresh = false } = {}): Promise<string | null> {
  const { enabled, instance } = init();
  if (!enabled || !instance) return null;

  const token = await silentToken(instance, forceRefresh);
  if (token) return token;

  // reauthenticate() only returns if this tab was hidden and another one has signed in since.
  await reauthenticate();
  return silentToken(instance, false);
}
