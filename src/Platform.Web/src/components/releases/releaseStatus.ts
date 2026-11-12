import type { ReleaseCheckKey, ReleaseCheckState, ReleaseLink, ReleaseManagerGate, ReleaseStatus, ReleaseStepStatus } from '@/lib/api';

/** Colours and words for a release, its steps and its checks — one table, so list and detail agree. */
export const RELEASE_STATUS: Record<ReleaseStatus, { bg: string; fg: string; label: string }> = {
  planned: { bg: 'var(--info-bg)', fg: 'var(--info)', label: 'Planned' },
  in_progress: { bg: 'var(--warning-bg)', fg: 'var(--warning)', label: 'In progress' },
  completed: { bg: 'var(--success-bg)', fg: 'var(--success)', label: 'Released' },
  cancelled: { bg: 'var(--bg-secondary)', fg: 'var(--text-muted)', label: 'Cancelled' },
};

export const STEP_STATUS: Record<ReleaseStepStatus, { fg: string; label: string }> = {
  pending: { fg: 'var(--text-muted)', label: 'To do' },
  in_progress: { fg: 'var(--info)', label: 'In progress' },
  done: { fg: 'var(--success)', label: 'Done' },
  skipped: { fg: 'var(--text-muted)', label: 'Skipped' },
};

export const CHECK_STATE: Record<ReleaseCheckState, { bg: string; fg: string; label: string }> = {
  waiting: { bg: 'var(--bg-secondary)', fg: 'var(--text-muted)', label: 'Nothing yet' },
  in_progress: { bg: 'var(--info-bg)', fg: 'var(--info)', label: 'In progress' },
  complete: { bg: 'var(--success-bg)', fg: 'var(--success)', label: 'Complete' },
  attention: { bg: 'var(--danger-bg)', fg: 'var(--danger)', label: 'Needs attention' },
};

/** What the portal measures for a step — shown above the numbers. */
export const CHECK_LABEL: Record<ReleaseCheckKey, string> = {
  'staging-builds': 'Builds on staging',
  'staging-qa': 'QA Review',
  'production-promotions': 'Production promotions',
  'production-deploys': 'Production deployments',
  'production-migrations': 'Migrations',
};

export function releaseStatusStyle(status: string) {
  return RELEASE_STATUS[status as ReleaseStatus] ?? RELEASE_STATUS.planned;
}

export function releasePath(id: string): string {
  return `/releases/${id}`;
}

/** "12 Nov 2026" for a yyyy-MM-dd date, without shifting it through the viewer's time zone. */
export function formatTargetDate(date: string | null | undefined): string | null {
  if (!date) return null;
  const [y, m, d] = date.split('-').map(Number);
  if (!y || !m || !d) return date;
  return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString(undefined, {
    day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC',
  });
}

/** An ISO instant as the value a `datetime-local` input shows, in the viewer's time zone. */
export function toLocalInput(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** A `datetime-local` value back to an ISO instant; empty stays empty. */
export function fromLocalInput(value: string): string | null {
  if (!value) return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

/** What a link points at — picks its icon. */
export type LinkKind = 'run' | 'ticket' | 'doc' | 'pr' | 'tag' | 'repo' | 'link';

/**
 * Name a link from its address the way people refer to it: "Azure DevOps run 8971132",
 * "mpt-release run 43209283187", "MPT-27923", "Confluence page", "mpt-release #1668". A label the
 * release manager typed wins; the kind is always read off the address.
 */
export function describeLink(url: string, label?: string | null): { text: string; kind: LinkKind } {
  let u: URL;
  try {
    u = new URL(url);
  } catch {
    return { text: label || url, kind: 'link' };
  }
  let path = u.pathname;
  try { path = decodeURIComponent(path); } catch { /* keep it encoded */ }
  const host = u.hostname.toLowerCase();
  const named = (text: string, kind: LinkKind) => ({ text: label || text, kind });

  const buildId = u.searchParams.get('buildId');
  if (path.includes('/_build/results') && buildId) return named(`Azure DevOps run ${buildId}`, 'run');
  let m = path.match(/^\/[^/]+\/([^/]+)\/actions\/runs\/(\d+)/);
  if (host === 'github.com' && m) return named(`${m[1]} run ${m[2]}`, 'run');
  m = path.match(/^\/[^/]+\/([^/]+)\/pull\/(\d+)/);
  if (host === 'github.com' && m) return named(`${m[1]} #${m[2]}`, 'pr');
  m = path.match(/^\/[^/]+\/([^/]+)\/(?:releases\/tag|tree)\/(.+)$/);
  if (host === 'github.com' && m) return named(`${m[1]} ${m[2]}`, 'tag');
  m = path.match(/\/_git\/([^/]+)\/pullrequest\/(\d+)/);
  if (m) return named(`${m[1]} PR ${m[2]}`, 'pr');
  m = path.match(/\/_git\/([^/]+)\/?$/);
  const version = u.searchParams.get('version');
  if (m && version?.startsWith('GT')) return named(`${m[1]} tag ${version.slice(2)}`, 'tag');
  if (m) return named(m[1], 'repo');
  // The site itself or one of its subdomains — never a look-alike such as notatlassian.net.
  if (host === 'atlassian.net' || host.endsWith('.atlassian.net')) {
    m = path.match(/\/browse\/([A-Z][A-Z0-9]+-\d+)/i);
    if (m) return named(m[1].toUpperCase(), 'ticket');
    if (/\/projects\/[^/]+\/versions\/\d+/.test(path)) return named('Jira release version', 'ticket');
    if (path.startsWith('/issues') || u.searchParams.has('jql')) return named('Jira search', 'ticket');
    if (path.startsWith('/wiki')) return named('Confluence page', 'doc');
  }
  m = path.match(/^\/[^/]+\/([^/]+)\/?$/);
  if (host === 'github.com' && m) return named(m[1], 'repo');
  const text = `${host}${path === '/' ? '' : path}`;
  return named(text.length > 60 ? `${text.slice(0, 59)}…` : text, 'link');
}

/** Links as the API takes them: trimmed, without empty rows, a blank name sent as none. */
export function cleanLinks(links: ReleaseLink[]): ReleaseLink[] {
  return links
    .map((l) => ({ url: l.url.trim(), label: l.label?.trim() || null }))
    .filter((l) => l.url.length > 0);
}

/**
 * "mpt: a@x, b@x · mpt-extensions: Release Managers (group)" — the release managers per product, read
 * off the production gates. Empty when no gate names any.
 */
export function describeManagers(gates: ReleaseManagerGate[], products?: string[]): string {
  const byProduct = new Map<string, Set<string>>();
  for (const g of gates) {
    if (products && !products.includes(g.product)) continue;
    const names = byProduct.get(g.product) ?? new Set<string>();
    g.users.forEach((u) => names.add(u));
    g.groups.forEach((x) => names.add(`${x.name || x.id} (group)`));
    byProduct.set(g.product, names);
  }
  return [...byProduct.entries()]
    .filter(([, names]) => names.size > 0)
    .map(([product, names]) => `${product}: ${[...names].join(', ')}`)
    .join(' · ');
}
