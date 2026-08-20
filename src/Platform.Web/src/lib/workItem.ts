import type { PromotionSourceEventParticipant, WorkItemDecision } from '@/lib/api';
import { roleDisplay } from '@/lib/roleLabel';

/**
 * In-app route to a work item's detail page. A work item's identity for sign-off is the triple
 * (key, product, targetEnv) — the same grain the decisions and comments key on — so product and
 * target env travel as query params alongside the key.
 *
 * Pass `fromCandidateId` when linking out of a promotion: the detail page turns it into a "Back to
 * promotion" breadcrumb so a reviewer who came in to sign something off lands back where they were
 * instead of in the work-item queue. It rides in the URL rather than router state so a refresh or a
 * shared link keeps the trail.
 */
export function workItemDetailPath(
  key: string,
  product: string,
  targetEnv: string,
  fromCandidateId?: string | null,
): string {
  const params = new URLSearchParams({ product, targetEnv });
  if (fromCandidateId) params.set('from', fromCandidateId);
  return `/work-items/${encodeURIComponent(key)}?${params.toString()}`;
}

/**
 * Reads the `from` param written by {@link workItemDetailPath} back out. Returns null unless the
 * value is a well-formed GUID — the param lands in an href, so a junk value should fall back to the
 * default breadcrumb rather than render a link to nowhere.
 */
export function referringCandidateId(value: string | null | undefined): string | null {
  const raw = (value ?? '').trim();
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(raw) ? raw : null;
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
 * Reads a list of canonical role keys as a sentence fragment: "QA Owner", "QA Owner and Reviewer",
 * "QA Owner, Reviewer and Author". Used for the "needs attention" wording wherever a work item is
 * missing somebody in a role its promotion policy requires — see components/promotions/MissingRoles.
 */
export function missingRolesLabel(roles: string[]): string {
  const labels = roles.map((r) => roleDisplay({ role: r }));
  if (labels.length === 0) return '';
  if (labels.length === 1) return labels[0];
  return `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1]}`;
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
 * Web link to the provider's commit-diff view between two revisions — "what exactly is being
 * promoted", one click from the promotion page. Built from the repository reference's URL and the
 * candidate's fromRevision/toRevision. Returns null when the provider's compare URL shape is
 * unknown or either input is missing: a wrong guess renders a 404 link, which is worse than none.
 */
export function commitCompareUrl(
  repositoryUrl: string | null | undefined,
  provider: string | null | undefined,
  fromRevision: string | null | undefined,
  toRevision: string | null | undefined,
): string | null {
  const from = (fromRevision ?? '').trim();
  const to = (toRevision ?? '').trim();
  let base = (repositoryUrl ?? '').trim().replace(/\/+$/, '');
  if (base.endsWith('.git')) base = base.slice(0, -4);
  // All-zero revisions are onboarding placeholders, not commits a compare view could resolve.
  if (!base || !from || !to || /^0+$/.test(from) || /^0+$/.test(to)) return null;

  switch ((provider ?? '').trim().toLowerCase()) {
    case 'github':
      return `${base}/compare/${from}...${to}`;
    case 'gitlab':
      return `${base}/-/compare/${from}...${to}`;
    case 'azure-devops':
    case 'azuredevops':
    case 'ado':
      return `${base}/branchCompare?baseVersion=GC${from}&targetVersion=GC${to}&_a=commits`;
    default:
      return null;
  }
}

/**
 * Presentation for a work-item decision. One source of truth so the queue, the promotion row, and
 * the detail page can't drift on what a decision looks like or reads as.
 *
 * Three text forms, because one string can't serve all three sentences: `label` is the badge
 * ("Issue"), `attributed` prefixes an actor ("Issue raised by Ada"), and `youDid` completes a
 * first-person sentence ("You raised an issue on this work item."). A single label forced
 * constructions like "You issue this work item".
 *
 * Issue shares the warning palette with the undecided "Pending" state — the two never appear on the
 * same row, and the label plus the icon carry the distinction.
 */
export function decisionStyle(decision: WorkItemDecision): {
  label: string;
  /** Reads as `${attributed} by <name>`. */
  attributed: string;
  /** Reads as `You ${youDid}.` */
  youDid: string;
  color: string;
  bg: string;
} {
  switch (decision) {
    case 'Approved':
      return {
        label: 'Approved',
        attributed: 'Approved',
        youDid: 'approved this work item',
        color: 'var(--success)',
        bg: 'var(--success-bg)',
      };
    case 'Issue':
      return {
        label: 'Issue',
        attributed: 'Issue raised',
        youDid: 'raised an issue on this work item',
        color: 'var(--warning)',
        bg: 'var(--warning-bg)',
      };
    case 'Blocked':
      return {
        label: 'Blocked',
        attributed: 'Blocked',
        youDid: 'blocked this work item',
        color: 'var(--danger)',
        bg: 'var(--danger-bg)',
      };
  }
}
