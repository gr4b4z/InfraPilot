import { create } from 'zustand';
import { clearStoredToken } from '@/lib/localAuth';

export interface AuthUser {
  id: string;
  name: string;
  email: string;
  initials: string;
  roles: string[];
  isAdmin: boolean;
}

interface AuthState {
  user: AuthUser | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  setUser: (user: AuthUser) => void;
  setLoading: (loading: boolean) => void;
  clear: () => void;
  logout: () => void;
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
    initials: getInitials(name),
    roles,
    isAdmin: roles.includes('InfraPortal.Admin'),
  };
}

export const useAuthStore = create<AuthState>((set) => ({
  user: null,
  isAuthenticated: false,
  isLoading: true,
  setUser: (user) => set({ user, isAuthenticated: true, isLoading: false }),
  setLoading: (isLoading) => set({ isLoading }),
  clear: () => set({ user: null, isAuthenticated: false, isLoading: false }),
  logout: () => {
    clearStoredToken();
    set({ user: null, isAuthenticated: false, isLoading: false });
  },
}));
