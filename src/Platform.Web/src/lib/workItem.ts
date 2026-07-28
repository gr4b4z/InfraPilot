import type { PromotionSourceEventParticipant, WorkItemDecision } from '@/lib/api';
import { roleDisplay } from '@/lib/roleLabel';

/**
 * In-app route to a work item's detail page. A work item's identity for sign-off is the triple
 * (key, product, targetEnv) — the same grain the decisions and comments key on — so product and
 * target env travel as query params alongside the key.
 */
export function workItemDetailPath(key: string, product: string, targetEnv: string): string {
  const params = new URLSearchParams({ product, targetEnv });
  return `/work-items/${encodeURIComponent(key)}?${params.toString()}`;
}

/**
 * Compact one-line label for a participant on a work item. Format: "Role: Display <email>",
 * falling back to email-only or just the role label when no human name is available. Display names
 * are truncated so a long full name can't blow a row's layout.
 */
export function formatReferenceParticipant(p: PromotionSourceEventParticipant): string {
  const role = roleDisplay(p);
  const name = (p.displayName ?? '').trim();
  const truncatedName = name.length > 40 ? `${name.slice(0, 37)}...` : name;
  const email = (p.email ?? '').trim();
  if (truncatedName && email) return `${role}: ${truncatedName} <${email}>`;
  if (truncatedName) return `${role}: ${truncatedName}`;
  if (email) return `${role}: ${email}`;
  return role;
}

/**
 * Human name for a reference provider, for link labels like "View in Jira". Falls back to the raw
 * value (title-cased on separators) so an unrecognised provider still reads as a name rather than a
 * slug, and to a generic word when there's nothing to go on.
 */
export function providerLabel(provider: string | null | undefined, fallback = 'tracker'): string {
  const raw = (provider ?? '').trim();
  if (!raw) return fallback;
  switch (raw.toLowerCase()) {
    case 'jira':
      return 'Jira';
    case 'azure-devops':
    case 'azuredevops':
    case 'ado':
      return 'Azure DevOps';
    case 'github':
      return 'GitHub';
    case 'gitlab':
      return 'GitLab';
    case 'bitbucket':
      return 'Bitbucket';
    default:
      return raw
        .split(/[-_\s]+/)
        .filter(Boolean)
        .map((w) => w[0].toUpperCase() + w.slice(1))
        .join(' ');
  }
}

/** Abbreviated commit hash for display. Git's own 7-character convention. */
export function shortHash(hash: string): string {
  const trimmed = (hash ?? '').trim();
  return trimmed.length > 7 ? trimmed.slice(0, 7) : trimmed;
}

/**
 * Presentation for a work-item decision. One source of truth so the queue, the promotion row, and
 * the detail page can't drift on what "Blocked" looks like.
 *
 * Blocked shares the warning palette with the undecided "Pending" state — the two never appear on
 * the same row, and the label plus the icon carry the distinction.
 */
export function decisionStyle(decision: WorkItemDecision): {
  label: string;
  color: string;
  bg: string;
} {
  switch (decision) {
    case 'Approved':
      return { label: 'Approved', color: 'var(--success)', bg: 'var(--success-bg)' };
    case 'Rejected':
      return { label: 'Rejected', color: 'var(--danger)', bg: 'var(--danger-bg)' };
    case 'Blocked':
      return { label: 'Blocked', color: 'var(--warning)', bg: 'var(--warning-bg)' };
  }
}
