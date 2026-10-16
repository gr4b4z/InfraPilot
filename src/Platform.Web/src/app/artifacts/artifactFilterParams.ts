/**
 * The artifact registry's filter state, as it lives in the URL.
 *
 * The filters are in the query string rather than component state so a filtered registry is a link:
 * that is what lets a promotion point at the one artifact it was cut from (`artifactRegistryPath`),
 * and what lets someone answer "which artifacts went out on the 14th?" by sending the answer rather
 * than the recipe. Parsing and serialising live here so the page and the link builder cannot
 * disagree about what a parameter means.
 */

/** The time presets, in the order the control shows them. */
export const TIME_PRESETS = [
  { key: 'all', label: 'Any time' },
  { key: '24h', label: '24h' },
  { key: '7d', label: '7 days' },
  { key: '30d', label: '30 days' },
  { key: 'custom', label: 'Date range' },
] as const;

export type TimePreset = (typeof TIME_PRESETS)[number]['key'];

export function isTimePreset(value: string | null): value is TimePreset {
  return value !== null && TIME_PRESETS.some((p) => p.key === value);
}

export interface ArtifactFilters {
  /** Free-text search, matched as a substring across every column a reader might half-remember. */
  q: string;
  product: string;
  service: string;
  branch: string;
  /** Exact — it is how a link points at ONE artifact. */
  version: string;
  time: TimePreset;
  /** `datetime-local` values ("2026-08-14T09:30"), only meaningful while `time` is `custom`. */
  from: string;
  to: string;
}

export const EMPTY_FILTERS: ArtifactFilters = {
  q: '',
  product: '',
  service: '',
  branch: '',
  version: '',
  time: 'all',
  from: '',
  to: '',
};

export function parseArtifactFilters(params: URLSearchParams): ArtifactFilters {
  const time = params.get('time');
  return {
    q: params.get('q') ?? '',
    product: params.get('product') ?? '',
    service: params.get('service') ?? '',
    branch: params.get('branch') ?? '',
    version: params.get('version') ?? '',
    time: isTimePreset(time) ? time : 'all',
    from: params.get('from') ?? '',
    to: params.get('to') ?? '',
  };
}

/**
 * The registration window a filter state asks for, as ISO instants for the API — `since` inclusive,
 * `until` exclusive.
 *
 * A custom range's bounds are `datetime-local` strings, so they are read in the reader's own time
 * zone: someone asking for artifacts on the 14th means their 14th. A bound left empty is simply
 * open — "from the 14th onwards" is a question people ask as often as a closed range.
 *
 * Recomputed per fetch rather than pinned when the preset is picked: "the last 24 hours" should
 * still mean that after the page has been open for an hour.
 */
export function timeWindow(filters: ArtifactFilters): { since?: string; until?: string } {
  if (filters.time === 'custom') {
    return {
      since: localInputToIso(filters.from),
      until: localInputToIso(filters.to),
    };
  }
  const hours = filters.time === '24h' ? 24 : filters.time === '7d' ? 24 * 7 : filters.time === '30d' ? 24 * 30 : null;
  if (hours === null) return {};
  return { since: new Date(Date.now() - hours * 60 * 60 * 1000).toISOString() };
}

function localInputToIso(value: string): string | undefined {
  if (!value) return undefined;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
}

/**
 * Whether the state narrows anything at all. A `custom` preset with neither bound filled does
 * not — which matters, because the empty list it can produce must not be blamed on a time range
 * that isn't actually set.
 */
export function isFiltered(filters: ArtifactFilters): boolean {
  return countActiveFilters(filters) > 0;
}

/** How many narrowings are in effect — the number the collapsed filter panel reports. */
export function countActiveFilters(filters: ArtifactFilters): number {
  const window = timeWindow(filters);
  return [
    filters.q.trim(),
    filters.product.trim(),
    filters.service.trim(),
    filters.branch.trim(),
    filters.version.trim(),
    window.since || window.until ? 'time' : '',
  ].filter(Boolean).length;
}

/** How a custom range reads in a chip or a browser tab: "14 Aug 09:30 → 15 Aug". */
export function describeTimeFilter(filters: ArtifactFilters): string | null {
  if (filters.time !== 'custom') {
    const preset = TIME_PRESETS.find((p) => p.key === filters.time);
    return filters.time === 'all' ? null : `last ${preset?.label ?? filters.time}`;
  }
  const from = filters.from ? new Date(filters.from).toLocaleString() : null;
  const to = filters.to ? new Date(filters.to).toLocaleString() : null;
  if (from && to) return `${from} → ${to}`;
  if (from) return `since ${from}`;
  if (to) return `before ${to}`;
  return null;
}
