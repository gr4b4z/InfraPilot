import { useEffect, useState, type ReactNode } from 'react';
import { MsalProvider, useMsal } from '@azure/msal-react';
import { InteractionStatus } from '@azure/msal-browser';
import { getMsalInstance, isMsalEnabled, initializeMsal, reauthenticate, getActiveAccount } from '@/lib/auth';
import { useAuthStore, createAuthUser } from '@/stores/authStore';
import { useFeatureFlagsStore } from '@/stores/featureFlagsStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useUserPrefsStore } from '@/stores/userPrefsStore';
import { getAuthMode, isLocalAuthEnabled } from '@/lib/authConfig';
import { getStoredToken, fetchCurrentUser } from '@/lib/localAuth';
import { LoginPage } from '@/app/login/LoginPage';
import { AuthErrorScreen } from './AuthErrorScreen';
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
  // The hardcoded dev user is for dev builds and an explicit no-auth backend only — never a
  // fallback for a misconfigured or unreachable one.
  const devUserAllowed = import.meta.env.DEV || getAuthMode() === 'none';
  const [msalReady, setMsalReady] = useState(!msalEnabled);
  const [msalError, setMsalError] = useState<string | null>(null);
  const [localAuthChecked, setLocalAuthChecked] = useState(!localAuth);
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);

  useEffect(() => {
    if (isAuthenticated) {
      useFeatureFlagsStore.getState().load();
      useSettingsStore.getState().load();
      useUserPrefsStore.getState().load();
    }
  }, [isAuthenticated]);

  useEffect(() => {
    if (msalEnabled) {
      if (!msalInstance) return;
      // Initialize MSAL and handle any pending redirect
      initializeMsal()
        .then(() => setMsalReady(true))
        .catch((err) => {
          console.error('MSAL initialization failed:', err);
          setMsalError(err instanceof Error ? err.message : String(err));
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
    if (devUserAllowed) useAuthStore.getState().setUser(DEV_USER);
  }, [msalEnabled, localAuth, msalInstance, devUserAllowed]);

  if (msalError) {
    return <AuthErrorScreen title="Authentication failed" message={msalError} />;
  }

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
  } else if (!devUserAllowed) {
    return (
      <AuthErrorScreen
        title="Sign-in is not configured"
        message={`The server's auth configuration (mode "${getAuthMode()}") is incomplete or unsupported. Contact your administrator.`}
      />
    );
  }

  return <>{children}</>;
}

/**
 * Inner component that triggers MSAL login and extracts user claims.
 * Must be rendered inside MsalProvider.
 */
function MsalAuthGuard({ children }: { children: ReactNode }) {
  const { accounts, inProgress } = useMsal();
  const [error, setError] = useState<Error | null>(null);
  const setUser = useAuthStore((s) => s.setUser);
  const setLoading = useAuthStore((s) => s.setLoading);
  const signedIn = accounts.length > 0;

  useEffect(() => {
    if (signedIn || error || inProgress !== InteractionStatus.None) return;
    // Not yet authenticated — redirect to sign in. A hidden tab waits until it is shown; by then
    // another tab has usually signed in and the shared cache supplies the account.
    reauthenticate().catch((err: unknown) => setError(err instanceof Error ? err : new Error(String(err))));
  }, [signedIn, error, inProgress]);

  useEffect(() => {
    if (!signedIn) {
      setLoading(true);
      return;
    }

    const account = getActiveAccount() ?? accounts[0];
    const claims = account.idTokenClaims as Record<string, unknown> | undefined;

    const id = (claims?.oid as string) ?? account.localAccountId ?? 'unknown';
    const name = account.name ?? 'Unknown User';
    const email = account.username ?? '';
    const roles = (claims?.roles as string[]) ?? [];

    setUser(createAuthUser(id, name, email, roles));
  }, [accounts, signedIn, setUser, setLoading]);

  if (error && !signedIn) {
    // Clearing the error re-runs the sign-in effect above.
    return <AuthErrorScreen title="Authentication failed" message={error.message} onRetry={() => setError(null)} />;
  }

  if (!signedIn) {
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
