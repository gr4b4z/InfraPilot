import type { PersonalAccessToken, TokenAccess } from '@/lib/api';

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

/**
 * How each access level is named and described. `badge` is null for Standard: it is the default
 * and what every token was before levels existed, so only the elevated ones are called out.
 */
export const TOKEN_ACCESS: Record<
  TokenAccess,
  { label: string; summary: string; badge: { label: string; color: string; bg: string } | null }
> = {
  Standard: {
    label: 'Standard',
    summary: 'Your roles except Admin. Reads, comments and promotes builds; makes no decisions.',
    badge: null,
  },
  QA: {
    label: 'QA',
    summary: 'Adds the QA role and work-item sign-off (approve, raise an issue, block). No Admin, no promotion decisions.',
    badge: { label: 'QA access', color: 'var(--info)', bg: 'var(--info-bg)' },
  },
  Admin: {
    label: 'Admin',
    summary: 'Keeps your Admin role and makes every decision you can: approve, reject, bypass, sign off.',
    badge: { label: 'Admin access', color: 'var(--warning)', bg: 'var(--warning-bg)' },
  },
};

/** `InfraPortal.QA` → `QA`. The app-role prefix is the same on every role and says nothing. */
export function tokenRoleName(role: string): string {
  return role.replace(/^InfraPortal\./i, '');
}
