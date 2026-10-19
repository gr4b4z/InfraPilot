import { useEffect } from 'react';
import { create } from 'zustand';
import { api, type DirectoryCredentialFailure } from './api';

/**
 * Whether the portal's directory (Microsoft Graph) app can sign in, for the Admin banner.
 *
 * Every directory read swallows a failed sign-in on purpose — person search comes back empty,
 * group-based approval checks answer "not a member" — so on 2026-10-19 an expired client secret
 * went unnoticed for about 20 hours. The API remembers the failure (`GET /settings/directory-health`,
 * admin-only); this polls it for Admins, and the shell shows a banner while it is set.
 */

interface DirectoryHealthState {
  /** The sign-in failure still current, or null while the directory answers. */
  failure: DirectoryCredentialFailure | null;
  /** `since` of the outage the banner was dismissed for — a new outage brings it back. */
  dismissedSince: string | null;
  dismiss: () => void;
}

export const useDirectoryHealthStore = create<DirectoryHealthState>()((set, get) => ({
  failure: null,
  dismissedSince: null,
  dismiss: () => set({ dismissedSince: get().failure?.since ?? null }),
}));

const POLL_INTERVAL_MS = 5 * 60_000;
/** Tab focus can flap; this bounds the visibility refresh to one per minute however often it does. */
const MIN_REFRESH_GAP_MS = 60_000;
let lastRefresh = 0;

async function refreshDirectoryHealth(): Promise<void> {
  lastRefresh = Date.now();
  try {
    const { credentialFailure } = await api.getDirectoryHealth();
    useDirectoryHealthStore.setState({ failure: credentialFailure ?? null });
  } catch {
    // The API being unreachable has a banner of its own; keep what the last answer said.
  }
}

/**
 * Keeps {@link useDirectoryHealthStore} current while `enabled` (the signed-in user is an Admin):
 * now, every few minutes while the tab is visible, and when it comes back into view.
 */
export function useDirectoryHealthPolling(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) {
      useDirectoryHealthStore.setState({ failure: null });
      return;
    }
    void refreshDirectoryHealth();
    const id = window.setInterval(() => {
      if (document.visibilityState === 'visible') void refreshDirectoryHealth();
    }, POLL_INTERVAL_MS);
    const onVisible = () => {
      if (document.visibilityState !== 'visible') return;
      if (Date.now() - lastRefresh < MIN_REFRESH_GAP_MS) return;
      void refreshDirectoryHealth();
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.clearInterval(id);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [enabled]);
}
