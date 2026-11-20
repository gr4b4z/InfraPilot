import type { FeedType, Freshness, Money } from './inventoryTypes';

/** Currencies of the given amounts, sorted. Amounts in different currencies are never added. */
export function currencies(...amounts: (Money | undefined)[]): string[] {
  const set = new Set<string>();
  for (const m of amounts) for (const k of Object.keys(m ?? {})) set.add(k);
  return [...set].sort();
}

export function formatAmount(value: number, currency: string, compact = false): string {
  try {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
      maximumFractionDigits: compact ? 0 : 2,
      minimumFractionDigits: compact ? 0 : 2,
    }).format(value);
  } catch {
    // Not a currency Intl knows: print the code as published.
    return `${value.toFixed(compact ? 0 : 2)} ${currency}`;
  }
}

export function isZero(money: Money | undefined): boolean {
  return Object.values(money ?? {}).every((v) => v === 0);
}

export function monthLabel(period: string, short = false): string {
  const [y, m] = period.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, 1)).toLocaleDateString('en-US', {
    month: short ? 'short' : 'long',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

/** `TODO…` values are unfinished catalog data, shown as "not set" rather than as a name. */
export function isPlaceholder(value: string | null | undefined): boolean {
  return !value || /^TODO/i.test(value);
}

/**
 * A link for a catalog `repo` value, or null when it is not one we will link to. The value comes
 * from a published file: only an https URL, or an `org/repo` shorthand (GitHub), becomes a link —
 * never another scheme.
 */
export function repoLink(repo: string | null | undefined): { href: string; label: string } | null {
  if (!repo) return null;
  if (/^https:\/\/[^\s]+$/i.test(repo)) {
    return { href: repo, label: repo.replace(/^https:\/\/(dev\.azure\.com|github\.com)\//i, '') };
  }
  if (/^[\w.-]+\/[\w.-]+$/.test(repo)) return { href: `https://github.com/${repo}`, label: repo };
  return null;
}

/** After how long without a successful check a feed's copy counts as old, per feed. */
const STALE_AFTER_MS: Record<FeedType, number> = {
  catalog: 24 * 3600_000,
  status: 2 * 3600_000,
  cost: 36 * 3600_000,
};

export const FEED_LABEL: Record<FeedType, string> = {
  catalog: 'Catalog',
  status: 'Observed state',
  cost: 'Cost',
};

/** A failed last refresh, or no successful check for longer than the feed's allowance. */
export function isOutOfDate(f: Freshness, now = Date.now()): boolean {
  if (f.lastError) return true;
  if (!f.refreshedAt) return false;
  return now - new Date(f.refreshedAt).getTime() > STALE_AFTER_MS[f.feed];
}

/** The two cost series. The hidden themes do not define them, hence the fallbacks. */
export const SERIES_DIRECT = 'var(--series-direct, var(--accent))';
export const SERIES_SHARED = 'var(--series-shared, var(--info))';
