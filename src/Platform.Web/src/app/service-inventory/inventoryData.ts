/**
 * PROTOTYPE — stand-in for the service inventory API. Everything here reads the static fixtures in
 * ./fixtures (catalog.json, status.json and cost-2026-11.json are FICTIONAL files with the exact
 * shape of the published artifacts; cost-samples.json holds three more months, labelled as sample data). No fetcher,
 * no database, no API call. Throwaway: this branch is for reviewing the views only.
 */
import catalogFixture from './fixtures/catalog.json';
import statusFixture from './fixtures/status.json';
import costNovember from './fixtures/cost-2026-11.json';
import costSamples from './fixtures/cost-samples.json';

// ── Shapes (only the fields the views read; unknown fields are ignored) ─────────────

/** Amount per ISO 4217 currency. Never summed across keys. An empty object means zero. */
export type Money = Record<string, number>;

export interface CatalogService {
  id: string;
  description?: string;
  product: string;
  team: string;
  lifecycle: string;
  type?: string;
  repo?: string;
  exposure?: string;
  customer_facing?: boolean;
  environments: Record<string, { aks?: { cluster?: string; namespace?: string } }>;
  workloads?: Record<string, { kind?: string }>;
  bindings?: Record<string, unknown>;
}
export interface CatalogTeam { id: string; lead?: string; deputy?: string; github_team?: string; entra_group?: string; products?: string[] }
export interface CatalogProduct { id: string; name: string; business_owner?: string; technical_owner?: string }
export interface CatalogShared { id: string; kind: string; owner: string; description?: string }
export interface Catalog {
  catalog_version: number;
  services: CatalogService[];
  teams: CatalogTeam[];
  products: CatalogProduct[];
  shared: CatalogShared[];
}

export interface DbBinding { database: string; server?: string; owner?: boolean; workloads?: string[] }

export type StepStatus = 'done' | 'pending' | 'unknown';
export interface OnboardingStep { status: StepStatus; at?: string; detail?: string; owner?: string }
export interface StatusService {
  id: string;
  stale: boolean;
  unverified: boolean;
  warnings: { code: string; message: string }[];
  onboarding: Record<string, OnboardingStep | string>;
}
export interface ObservedBinding {
  service: string; environment: string; kind: string; database: string; server?: string;
  state: 'present' | 'absent' | 'ambiguous' | 'unresolved'; resource?: string;
}
export interface Status {
  generated_at: string;
  catalog_version: number;
  services: StatusService[];
  azure?: {
    observed_at: string;
    services: { id: string; resources?: { id: string; type?: string; env?: string }[] }[];
    shared: { id: string; state: 'ok' | 'ambiguous' | 'unresolved'; resources: { id: string; type?: string; env?: string }[] }[];
    bindings: ObservedBinding[];
    untagged: { subscription: string; resource_group: string; count: number }[];
  };
}

export interface CostLine { source: string; id: string; cost: Money; via?: 'tag' | 'override' }
export interface CostService {
  id: string; product?: string; team?: string;
  direct: Money; shared: Money; total: Money;
  resources: CostLine[];
  shared_from: { shared: string; cost: Money }[];
}
export interface CostTotals { total: Money; direct: Money; shared: Money; shared_unsplit: Money; unallocated: Money }
export interface CostMonth {
  _sample?: string;
  period: string;
  complete: boolean;
  generated_at: string;
  catalog_version: number | null;
  sources: { id: string; rows: number; scopes: number; totals: CostTotals }[];
  totals: CostTotals;
  services: CostService[];
  shared: { id: string; cost: Money; services: string[]; unsplit?: Money; resources: CostLine[] }[];
  unallocated: { untagged: Money; unknown_tag: Money; by_scope: { source: string; scope: string; group?: string; cost: Money }[] };
}

// ── Prototype scenarios ─────────────────────────────────────────────────────────────

/**
 * Review switch, read from `?inv=` so a screenshot URL reproduces a state:
 * - `ok`          fixtures as published
 * - `failed`      the last catalog/status refresh failed; the previous copy is still shown
 * - `unsupported` the newest cost artifact has a schemaVersion this portal does not read
 * - `unresolved`  observed state with `unresolved` / `unknown` values and an unverified service
 */
export type Scenario = 'ok' | 'failed' | 'unsupported' | 'unresolved';
export const SCENARIOS: { key: Scenario; label: string }[] = [
  { key: 'ok', label: 'Fresh data' },
  { key: 'failed', label: 'Refresh failed' },
  { key: 'unsupported', label: 'Unsupported schema' },
  { key: 'unresolved', label: 'Unresolved values' },
];
export function readScenario(search: string): Scenario {
  const v = new URLSearchParams(search).get('inv');
  return SCENARIOS.some((s) => s.key === v) ? (v as Scenario) : 'ok';
}

// ── Feed freshness ──────────────────────────────────────────────────────────────────

export type FeedKey = 'catalog' | 'status' | 'cost';
export interface FeedState {
  feed: FeedKey;
  label: string;
  /** Where it is read from — shown to admins only. */
  source: string;
  /** What the stored copy is: catalog version, status generation time, cost period. */
  version: string;
  /** When the stored (last good) copy was fetched. */
  refreshedAt: string;
  /** When the last attempt ran, successful or not. */
  attemptedAt: string;
  /** Why the last attempt did not replace the copy. Null when it succeeded. */
  error: string | null;
  pollEvery: string;
}

/**
 * "Now" for the prototype is pinned shortly after the fixtures were generated, so the ages on the
 * screenshots read like a normal day rather than drifting with the reviewer's clock.
 */
export const PROTOTYPE_NOW = new Date('2026-11-20T15:41:00Z');

function minutesBefore(iso: string | Date, minutes: number) {
  return new Date(new Date(iso).getTime() - minutes * 60_000).toISOString();
}

export function feedStates(scenario: Scenario): FeedState[] {
  const now = PROTOTYPE_NOW;
  const failed = scenario === 'failed';
  return [
    {
      feed: 'catalog',
      label: 'Catalog',
      source: 'oci · registry.example.com/service-catalog/catalog:latest',
      version: `v${catalogFixture.catalog_version}`,
      refreshedAt: failed ? minutesBefore(now, 6 * 60 + 12) : minutesBefore(now, 4),
      attemptedAt: minutesBefore(now, 4),
      error: failed ? 'The registry refused the credentials (401 Unauthorized) when reading the tag.' : null,
      pollEvery: '15 min',
    },
    {
      feed: 'status',
      label: 'Observed state',
      source: 'oci · registry.example.com/service-catalog/status:latest',
      version: `generated ${statusFixture.generated_at}`,
      refreshedAt: failed ? minutesBefore(now, 6 * 60 + 12) : minutesBefore(now, 11),
      attemptedAt: minutesBefore(now, 4),
      error: failed ? 'The registry refused the credentials (401 Unauthorized) when reading the tag.' : null,
      pollEvery: '10 min',
    },
    {
      feed: 'cost',
      label: 'Cost',
      source: 'oci · registry.example.com/service-cost/allocation:latest (+ closed months by tag)',
      version: `${costNovember.period} to date`,
      refreshedAt: minutesBefore(now, 15),
      attemptedAt: minutesBefore(now, scenario === 'unsupported' ? 3 : 15),
      error:
        scenario === 'unsupported'
          ? 'The newest cost file uses format version 3, which this portal cannot read yet. The previous copy is kept.'
          : null,
      pollEvery: '1 h',
    },
  ];
}

/** Hours after which a copy counts as old even if no refresh failed. Per feed. */
export const STALE_AFTER_MIN: Record<FeedKey, number> = { catalog: 24 * 60, status: 2 * 60, cost: 36 * 60 };

// ── Data ────────────────────────────────────────────────────────────────────────────

export const catalog = catalogFixture as unknown as Catalog;

export function getStatus(scenario: Scenario): Status {
  const base = structuredClone(statusFixture) as unknown as Status;
  if (scenario !== 'unresolved') return base;
  // Prototype-only variant: the shared server was not visible in the last scan, so nothing is known
  // about the binding — and one service never had a usable component file.
  base.azure!.shared[0].state = 'unresolved';
  base.azure!.bindings = base.azure!.bindings.map((b) =>
    b.environment === 'prod' ? { ...b, state: 'unresolved', resource: undefined } : b,
  );
  const em = base.services.find((s) => s.id === 'ledger-api')!;
  em.unverified = true;
  em.warnings = [{ code: 'component_file_missing', message: 'The service repository has no component file yet.' }];
  return base;
}

/** Oldest first. The three sample months come before the one real month. */
export const costMonths: CostMonth[] = [
  ...(costSamples as unknown as CostMonth[]),
  costNovember as unknown as CostMonth,
];
export const currentMonth = costMonths[costMonths.length - 1];

export function findCatalogService(serviceName: string): CatalogService | undefined {
  // Prototype join rule: the portal service name equals the catalog id (case-insensitive).
  const key = serviceName.toLowerCase();
  return catalog.services.find((s) => s.id === key);
}

// ── Money helpers ───────────────────────────────────────────────────────────────────

export function currencies(...amounts: Money[]): string[] {
  const set = new Set<string>();
  for (const m of amounts) for (const k of Object.keys(m ?? {})) set.add(k);
  return [...set].sort();
}

export function addMoney(a: Money, b: Money): Money {
  const out: Money = { ...a };
  for (const [cur, v] of Object.entries(b ?? {})) out[cur] = (out[cur] ?? 0) + v;
  return out;
}

export function formatAmount(value: number, currency: string, compact = false): string {
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency,
    maximumFractionDigits: compact ? 0 : 2,
    minimumFractionDigits: compact ? 0 : 2,
  }).format(value);
}

/** Sort key for "sorted by cost": the first currency's amount. Display never mixes currencies. */
export function primaryAmount(m: Money, currency = 'USD'): number {
  return m?.[currency] ?? Object.values(m ?? {})[0] ?? 0;
}

export function monthLabel(period: string, short = false): string {
  const [y, mo] = period.split('-').map(Number);
  return new Date(Date.UTC(y, mo - 1, 1)).toLocaleDateString('en-US', {
    month: short ? 'short' : 'long',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

export function productName(id: string | undefined): string {
  return catalog.products.find((p) => p.id === id)?.name ?? id ?? '—';
}

/** `TODO…` values are unfinished catalog data, shown as "not set" rather than as a name. */
export function isPlaceholder(v: string | undefined): boolean {
  return !v || /^TODO/i.test(v);
}
