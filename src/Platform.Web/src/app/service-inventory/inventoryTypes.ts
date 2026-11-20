// Shapes of the service inventory API (notes/service-inventory-api.md). The portal's own fields
// are camelCase; observed state under `onboarding`, `bindings`, `shared` and `taggedResources` is
// passed through with the field names the published status file uses.

/** Amount per ISO 4217 currency. Never summed across keys. */
export type Money = Record<string, number>;

export type FeedType = 'catalog' | 'status' | 'cost';

/** How old a feed's data is and whether the last refresh worked. */
export interface Freshness {
  feed: FeedType;
  period: string | null;
  version: string | null;
  /** When the copy shown was fetched. */
  fetchedAt: string | null;
  /** Last successful check (an unchanged source counts). */
  refreshedAt: string | null;
  lastAttemptAt: string | null;
  /** Why the last attempt did not replace the copy. Admins get the reason; others a general line. */
  lastError: string | null;
  consecutiveFailures: number;
}

export interface InventoryServiceResponse {
  service: {
    id: string;
    description: string | null;
    product: string | null;
    team: string | null;
    lifecycle: string | null;
    type: string | null;
    repo: string | null;
    tier: string | null;
    exposure: string | null;
    customerFacing: boolean | null;
    replacedBy: string | null;
    retiredAt: string | null;
    environments: { environment: string; cluster: string | null; namespace: string | null }[];
    workloads: { name: string; kind: string | null }[] | null;
    databases: {
      kind: string;
      database: string;
      server: string | null;
      owner: boolean;
      workloads: string[];
      /** Declared for these environments only; null: every environment. */
      onlyInEnvironments: string[] | null;
      /** Environments that declare this database on another server instead. */
      notInEnvironments: string[];
    }[];
  };
  team: { id: string; lead: string | null; deputy: string | null; githubTeam: string | null } | null;
  product: { id: string; name: string | null } | null;
  portal: { product: string; productDiffers: boolean } | null;
  catalogVersion: number | null;
  freshness: Freshness;
}

export type OnboardingStatus = 'done' | 'pending' | 'unknown';
export interface OnboardingStep {
  status?: OnboardingStatus;
  at?: string;
  detail?: string;
  owner?: string;
}

export interface ObservedBinding {
  service: string;
  environment: string;
  kind: string;
  database: string;
  server?: string | null;
  /** present | absent | ambiguous | unresolved (not observed) */
  state: string;
  resource?: string | null;
}

export interface InventoryObservedResponse {
  serviceId: string;
  stale: boolean;
  unverified: boolean;
  warnings: { code: string | null; message: string | null }[];
  onboarding: Record<string, OnboardingStep | string> | null;
  generatedAt: string | null;
  azure: {
    observedAt: string | null;
    bindings: ObservedBinding[];
    shared: { id: string; state: string | null; resources: { id: string | null; type: string | null; env: string | null }[] }[];
    taggedResources: { id?: string; resources?: { id?: string; type?: string; env?: string }[] }[];
  } | null;
  freshness: Freshness;
}

export interface InventoryServiceCostResponse {
  serviceId: string;
  period: string;
  complete: boolean;
  direct: Money;
  shared: Money;
  total: Money;
  resources: { source: string; id: string; cost: Money; via?: string | null }[];
  sharedFrom: { shared: string; cost: Money }[];
  freshness: Freshness;
}

export interface InventoryMonth {
  period: string;
  complete: boolean;
}

export interface CostGroupRow {
  id: string;
  name: string;
  services: number;
  direct: Money;
  shared: Money;
  total: Money;
}

export interface CostServiceRow {
  id: string;
  product: string;
  productName: string;
  team: string;
  direct: Money;
  shared: Money;
  total: Money;
  sharedFrom: string[];
  /** Where the service's own page is: the product it is deployed under. Null: never deployed. */
  deployments: { product: string; service: string } | null;
}

export interface InventoryCostResponse {
  /** Null while no cost file has been read. */
  period: string | null;
  complete?: boolean;
  catalogVersion?: number | null;
  /** The whole month, all services. */
  month?: { total: Money; direct: Money; shared: Money; unallocated: Money };
  months: InventoryMonth[];
  options: { products: { id: string; name: string }[]; teams: string[] };
  scope: { kind: 'product' | 'team'; id: string; name: string; known: boolean } | null;
  // Without a scope
  products?: CostGroupRow[];
  teams?: CostGroupRow[];
  // With a scope
  totals?: { direct: Money; shared: Money; total: Money };
  services?: CostServiceRow[];
  trend?: { period: string; complete: boolean; direct: Money; shared: Money }[];
  freshness: Freshness;
}

export interface CoverageCurrency {
  currency: string;
  total: number;
  direct: number;
  shared: number;
  sharedUnsplit: number;
  untagged: number;
  unknownTag: number;
  largestGroups: { source: string; scope: string; group: string; amount: number }[];
  largestScopes: { source: string; scope: string; amount: number }[];
}

export interface InventoryCoverageResponse {
  period: string | null;
  complete?: boolean;
  months: InventoryMonth[];
  currencies?: CoverageCurrency[];
  sources?: { source: string; currency: string; total: number; attributed: number; rows: number | null; scopes: number | null }[];
  unsplitShared?: { id: string; unsplit: Money }[];
  trend?: { period: string; complete: boolean; currency: string; total: number; attributed: number }[];
  freshness: Freshness;
}

export interface InventoryFeedsResponse {
  configurationErrors: string[];
  feeds: { name: string; type: string; sourceKind: string; location: string | null; pollMinutes: number }[];
  state: {
    feed: string;
    configured: boolean;
    period: string | null;
    type: string;
    stored: { sourceLocation: string; version: string; fetchedAt: string } | null;
    lastSuccessAt: string | null;
    lastAttemptAt: string | null;
    lastError: string | null;
    consecutiveFailures: number;
  }[];
}

/** What `api` throws, with the HTTP status and the parsed body. */
export interface ApiError extends Error {
  status?: number;
  body?: { error?: string; freshness?: Freshness };
}
