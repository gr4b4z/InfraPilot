import type { PersonalAccessToken } from '@/lib/api';

/**
 * How a personal access token is shown, shared by the owner's Agent access page and the admin's
 * Settings → Agent Tokens, so a token reads the same in both.
 */

export type TokenStatus = 'active' | 'expired' | 'revoked';

export const TOKEN_STATUS_STYLE: Record<TokenStatus, { label: string; color: string; bg: string }> = {
  active: { label: 'Active', color: 'var(--success)', bg: 'var(--success-bg)' },
  expired: { label: 'Expired', color: 'var(--text-muted)', bg: 'var(--bg-secondary)' },
  revoked: { label: 'Revoked', color: 'var(--danger)', bg: 'var(--danger-bg)' },
};

/** Revoked wins over expired: it is the decision someone made, not the clock running out. */
export function tokenStatus(token: Pick<PersonalAccessToken, 'revokedAt' | 'expiresAt'>, now: number): TokenStatus {
  if (token.revokedAt) return 'revoked';
  if (new Date(token.expiresAt).getTime() < now) return 'expired';
  return 'active';
}

/** `InfraPortal.QA` → `QA`. The app-role prefix is the same on every role and says nothing. */
export function tokenRoleName(role: string): string {
  return role.replace(/^InfraPortal\./i, '');
}
