import { buildApiUrl } from './runtimeConfig';

const TOKEN_KEY = 'platform_auth_token';

export interface LocalAuthUser {
  id: string;
  name: string;
  email: string;
  roles: string[];
  isAdmin: boolean;
  isQA: boolean;
}

interface LoginResponse {
  token: string;
  user: LocalAuthUser;
}

export function getStoredToken(): string | null {
  return localStorage.getItem(TOKEN_KEY);
}

export function setStoredToken(token: string): void {
  localStorage.setItem(TOKEN_KEY, token);
}

export function clearStoredToken(): void {
  localStorage.removeItem(TOKEN_KEY);
}

export async function localLogin(email: string, password: string): Promise<LoginResponse> {
  const response = await fetch(buildApiUrl('/auth/login'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });

  if (!response.ok) {
    throw new Error('Invalid email or password');
  }

  return response.json();
}

export async function fetchCurrentUser(token: string): Promise<LocalAuthUser> {
  const response = await fetch(buildApiUrl('/auth/me'), {
    headers: { Authorization: `Bearer ${token}` },
  });

  if (!response.ok) {
    throw new Error('Token expired or invalid');
  }

  return response.json();
}
