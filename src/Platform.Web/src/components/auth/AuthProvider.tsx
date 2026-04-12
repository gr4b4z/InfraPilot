import { useEffect, useState, type ReactNode } from 'react';
import { MsalProvider, useMsal, useMsalAuthentication } from '@azure/msal-react';
import { InteractionType } from '@azure/msal-browser';
import { msalInstance, isMsalEnabled, loginRequest } from '@/lib/auth';
import { useAuthStore, createAuthUser } from '@/stores/authStore';
import { Loader2 } from 'lucide-react';

const DEV_USER = createAuthUser(
  'dev-user',
  'Dev User',
  'dev@localhost',
  ['InfraPortal.Admin', 'InfraPortal.User'],
);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [msalReady, setMsalReady] = useState(!isMsalEnabled);

  useEffect(() => {
    if (!isMsalEnabled || !msalInstance) {
      // Dev mode — populate store with dev user immediately
      useAuthStore.getState().setUser(DEV_USER);
      return;
    }

    // Initialize MSAL and handle any pending redirect
    msalInstance
      .initialize()
      .then(() => msalInstance!.handleRedirectPromise())
      .then(() => setMsalReady(true))
      .catch((err) => {
        console.error('MSAL initialization failed:', err);
        // Fall back to dev mode on MSAL failure
        useAuthStore.getState().setUser(DEV_USER);
        setMsalReady(true);
      });
  }, []);

  if (!msalReady) {
    return <LoadingScreen />;
  }

  if (isMsalEnabled && msalInstance) {
    return (
      <MsalProvider instance={msalInstance}>
        <MsalAuthGuard>{children}</MsalAuthGuard>
      </MsalProvider>
    );
  }

  return <>{children}</>;
}

/**
 * Inner component that triggers MSAL login and extracts user claims.
 * Must be rendered inside MsalProvider.
 */
function MsalAuthGuard({ children }: { children: ReactNode }) {
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
