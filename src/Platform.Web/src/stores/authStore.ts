import { create } from 'zustand';
import { clearStoredToken } from '@/lib/localAuth';

export interface AuthUser {
  id: string;
  name: string;
  email: string;
  /**
   * Every address that is this user: `email` (the sign-in name) first, then the other addresses the
   * directory knows them by. A work item names its people by their mail, which need not be the
   * sign-in name, so "is this person me?" asks {@link isMyEmail}, not `email`. Starts as just
   * `email`; the rest arrive from `/api/me/emails` once signed in.
   */
  emails: string[];
  initials: string;
  roles: string[];
  isAdmin: boolean;
  isQA: boolean;
}

interface AuthState {
  user: AuthUser | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  setUser: (user: AuthUser) => void;
  /** Records the directory's addresses for the signed-in user (see {@link AuthUser.emails}). */
  setEmails: (emails: readonly string[]) => void;
  setLoading: (loading: boolean) => void;
  clear: () => void;
  logout: () => void;
}

function hasRole(roles: string[], role: string): boolean {
  const target = role.toLowerCase();
  return roles.some((r) => r.toLowerCase() === target);
}

/** `primary` first, then `others`, trimmed, without blanks or case-insensitive repeats. */
function mergeEmails(primary: string, others: readonly string[]): string[] {
  const seen = new Set<string>();
  const merged: string[] = [];
  for (const raw of [primary, ...others]) {
    const email = (raw ?? '').trim();
    if (!email || seen.has(email.toLowerCase())) continue;
    seen.add(email.toLowerCase());
    merged.push(email);
  }
  return merged;
}

/** True when `email` is one of the signed-in user's addresses (any case). */
export function isMyEmail(user: AuthUser | null | undefined, email: string | null | undefined): boolean {
  const candidate = (email ?? '').trim().toLowerCase();
  if (!user || !candidate) return false;
  return mergeEmails(user.email, user.emails ?? []).some((e) => e.toLowerCase() === candidate);
}

function getInitials(name: string): string {
  const parts = name.trim().split(/\s+/);
  if (parts.length >= 2) {
    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
  }
  return (parts[0]?.[0] ?? '?').toUpperCase();
}

export function createAuthUser(
  id: string,
  name: string,
  email: string,
  roles: string[],
): AuthUser {
  return {
    id,
    name,
    email,
    emails: mergeEmails(email, []),
    initials: getInitials(name),
    roles,
    isAdmin: hasRole(roles, 'InfraPortal.Admin'),
    isQA: hasRole(roles, 'InfraPortal.QA'),
  };
}

export const useAuthStore = create<AuthState>((set) => ({
  user: null,
  isAuthenticated: false,
  isLoading: true,
  // Signing in again as the same account (MSAL re-reads it as tokens refresh) keeps the addresses
  // already loaded for it; the load that fetched them runs once per sign-in.
  setUser: (user) =>
    set((s) => ({
      user:
        s.user && s.user.id === user.id
          ? { ...user, emails: mergeEmails(user.email, s.user.emails ?? []) }
          : user,
      isAuthenticated: true,
      isLoading: false,
    })),
  setEmails: (emails) =>
    set((s) => (s.user ? { user: { ...s.user, emails: mergeEmails(s.user.email, emails) } } : {})),
  setLoading: (isLoading) => set({ isLoading }),
  clear: () => set({ user: null, isAuthenticated: false, isLoading: false }),
  logout: () => {
    clearStoredToken();
    set({ user: null, isAuthenticated: false, isLoading: false });
  },
}));
