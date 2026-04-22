import { useEffect, useState, type ReactNode } from 'react';
import { MsalProvider, useMsal, useMsalAuthentication } from '@azure/msal-react';
import { InteractionType } from '@azure/msal-browser';
import { getMsalInstance, isMsalEnabled, getLoginRequest } from '@/lib/auth';
import { useAuthStore, createAuthUser } from '@/stores/authStore';
import { useFeatureFlagsStore } from '@/stores/featureFlagsStore';
import { isLocalAuthEnabled } from '@/lib/authConfig';
import { getStoredToken, fetchCurrentUser } from '@/lib/localAuth';
import { LoginPage } from '@/app/login/LoginPage';
import { Loader2 } from 'lucide-react';

const DEV_USER = createAuthUser(
  'dev-user',
  'Dev User',
  'dev@localhost',
  ['InfraPortal.Admin', 'InfraPortal.User'],
);

export function AuthProvider({ children }: { children: ReactNode }) {
  const msalEnabled = isMsalEnabled();
  const localAuth = isLocalAuthEnabled();
  const msalInstance = getMsalInstance();
  const [msalReady, setMsalReady] = useState(!msalEnabled);
  const [localAuthChecked, setLocalAuthChecked] = useState(!localAuth);
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);

  useEffect(() => {
    if (isAuthenticated) {
      useFeatureFlagsStore.getState().load();
    }
  }, [isAuthenticated]);

  useEffect(() => {
    if (msalEnabled) {
      if (!msalInstance) return;
      // Initialize MSAL and handle any pending redirect
      msalInstance
        .initialize()
        .then(() => msalInstance.handleRedirectPromise())
        .then(() => setMsalReady(true))
        .catch((err) => {
          console.error('MSAL initialization failed:', err);
          useAuthStore.getState().setUser(DEV_USER);
          setMsalReady(true);
        });
      return;
    }

    if (localAuth) {
      // Check for existing token in localStorage
      const token = getStoredToken();
      if (token) {
        fetchCurrentUser(token)
          .then((user) => {
            useAuthStore.getState().setUser(
              createAuthUser(user.id, user.name, user.email, user.roles),
            );
          })
          .catch(() => {
            // Token is invalid/expired — clear it, user will see login page
            localStorage.removeItem('platform_auth_token');
          })
          .finally(() => setLocalAuthChecked(true));
      } else {
        setLocalAuthChecked(true);
      }
      return;
    }

    // Neither MSAL nor local auth — legacy dev mode with hardcoded user
    useAuthStore.getState().setUser(DEV_USER);
  }, [msalEnabled, localAuth, msalInstance]);

  // MSAL loading
  if (msalEnabled && !msalReady) {
    return <LoadingScreen />;
  }

  // MSAL flow
  if (msalEnabled && msalInstance) {
    return (
      <MsalProvider instance={msalInstance}>
        <MsalAuthGuard>{children}</MsalAuthGuard>
      </MsalProvider>
    );
  }

  // Local auth — show login page if not authenticated
  if (localAuth) {
    if (!localAuthChecked) return <LoadingScreen />;
    if (!isAuthenticated) return <LoginPage />;
  }

  return <>{children}</>;
}

/**
 * Inner component that triggers MSAL login and extracts user claims.
 * Must be rendered inside MsalProvider.
 */
function MsalAuthGuard({ children }: { children: ReactNode }) {
  const loginRequest = getLoginRequest();
  const { accounts } = useMsal();
  const { login, error } = useMsalAuthentication(InteractionType.Redirect, loginRequest);
  const setUser = useAuthStore((s) => s.setUser);
  const setLoading = useAuthStore((s) => s.setLoading);

  useEffect(() => {
    if (accounts.length === 0) {
      // Not yet authenticated — MSAL will redirect
      setLoading(true);
      return;
    }

    const account = accounts[0];
    const claims = account.idTokenClaims as Record<string, unknown> | undefined;

    const id = (claims?.oid as string) ?? account.localAccountId ?? 'unknown';
    const name = account.name ?? 'Unknown User';
    const email = account.username ?? '';
    const roles = (claims?.roles as string[]) ?? [];

    setUser(createAuthUser(id, name, email, roles));
  }, [accounts, setUser, setLoading]);

  if (error) {
    return (
      <div className="flex flex-col items-center justify-center h-screen gap-4" style={{ backgroundColor: 'var(--bg-primary)' }}>
        <p className="text-[14px] font-medium" style={{ color: 'var(--danger)' }}>
          Authentication failed
        </p>
        <p className="text-[13px] max-w-md text-center" style={{ color: 'var(--text-muted)' }}>
          {error.message}
        </p>
        <button
          onClick={() => login(InteractionType.Redirect, loginRequest)}
          className="px-4 py-2 text-[13px] font-medium rounded-lg text-white"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          Try Again
        </button>
      </div>
    );
  }

  if (accounts.length === 0) {
    return <LoadingScreen message="Redirecting to sign in..." />;
  }

  return <>{children}</>;
}

function LoadingScreen({ message = 'Loading...' }: { message?: string }) {
  return (
    <div
      className="flex flex-col items-center justify-center h-screen gap-3"
      style={{ backgroundColor: 'var(--bg-primary)' }}
    >
      <Loader2 size={24} className="animate-spin" style={{ color: 'var(--accent)' }} />
      <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>{message}</p>
    </div>
  );
}
