import { useMemo } from 'react';
import type { PendingTicket } from '@/lib/api';

/**
 * Picker for narrowing the My-queue list by product / service / target environment.
 *
 * Three side-by-side native selects. Pure client-side filtering — the queue endpoint
 * already returns only what the user is authorised to sign off, so we just narrow what's
 * already loaded. Each dropdown is populated from the unfiltered ticket list so the user
 * never sees a zero-result option.
 *
 * Independent dropdowns (no hierarchical narrowing): if a (product, service) combination
 * has no tickets, the empty-state in MyQueuePage explains.
 */
export type ScopeFilterValue = {
  product: string | null;
  service: string | null;
  targetEnv: string | null;
};

export const SCOPE_FILTER_DEFAULT: ScopeFilterValue = {
  product: null,
  service: null,
  targetEnv: null,
};

const ANY = '__any__';

export function ScopeFilter({
  value,
  onChange,
  tickets,
}: {
  value: ScopeFilterValue;
  onChange: (next: ScopeFilterValue) => void;
  /** Unfiltered queue rows — used to compute the available options. */
  tickets: PendingTicket[];
}) {
  const { products, services, targetEnvs } = useMemo(() => {
    const p = new Set<string>();
    const s = new Set<string>();
    const e = new Set<string>();
    for (const t of tickets) {
      if (t.product) p.add(t.product);
      if (t.service) s.add(t.service);
      if (t.targetEnv) e.add(t.targetEnv);
    }
    const sortAlpha = (a: string, b: string) =>
      a.localeCompare(b, undefined, { sensitivity: 'base' });
    return {
      products: [...p].sort(sortAlpha),
      services: [...s].sort(sortAlpha),
      targetEnvs: [...e].sort(sortAlpha),
    };
  }, [tickets]);

  const setField = <K extends keyof ScopeFilterValue>(key: K, raw: string) => {
    onChange({ ...value, [key]: raw === ANY ? null : raw });
  };

  return (
    <>
      <label
        className="inline-flex items-center gap-1.5 text-[12px]"
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Product</span>
        <select
          value={value.product ?? ANY}
          onChange={(e) => setField('product', e.target.value)}
          className="rounded-lg border px-2 py-1.5 text-[12px] font-medium"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value={ANY}>Any product</option>
          {products.map((p) => (
            <option key={p} value={p}>{p}</option>
          ))}
        </select>
      </label>

      <label
        className="inline-flex items-center gap-1.5 text-[12px]"
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Service</span>
        <select
          value={value.service ?? ANY}
          onChange={(e) => setField('service', e.target.value)}
          className="rounded-lg border px-2 py-1.5 text-[12px] font-medium"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value={ANY}>Any service</option>
          {services.map((s) => (
            <option key={s} value={s}>{s}</option>
          ))}
        </select>
      </label>

      <label
        className="inline-flex items-center gap-1.5 text-[12px]"
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Target env</span>
        <select
          value={value.targetEnv ?? ANY}
          onChange={(e) => setField('targetEnv', e.target.value)}
          className="rounded-lg border px-2 py-1.5 text-[12px] font-medium"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value={ANY}>Any env</option>
          {targetEnvs.map((e) => (
            <option key={e} value={e}>{e}</option>
          ))}
        </select>
      </label>
    </>
  );
}

/** Pure helper: applies a scope filter to a ticket list. Null fields mean "any". */
export function applyScopeFilter(
  tickets: PendingTicket[],
  filter: ScopeFilterValue,
): PendingTicket[] {
  if (!filter.product && !filter.service && !filter.targetEnv) return tickets;
  return tickets.filter((t) => {
    if (filter.product && t.product !== filter.product) return false;
    if (filter.service && t.service !== filter.service) return false;
    if (filter.targetEnv && t.targetEnv !== filter.targetEnv) return false;
    return true;
  });
}

/** True when at least one of the three scope dropdowns is narrowing. */
export function hasActiveScope(filter: ScopeFilterValue): boolean {
  return filter.product !== null || filter.service !== null || filter.targetEnv !== null;
}

// ── localStorage helpers (mirror AssigneeFilter's pattern) ────────────────────────────────
export const SCOPE_FILTER_STORAGE_KEY = 'me.queue.scopeFilter';

export function loadScopeFilter(): ScopeFilterValue {
  try {
    const raw = window.localStorage.getItem(SCOPE_FILTER_STORAGE_KEY);
    if (!raw) return SCOPE_FILTER_DEFAULT;
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return SCOPE_FILTER_DEFAULT;
    const norm = (v: unknown): string | null =>
      typeof v === 'string' && v.length > 0 ? v : null;
    return {
      product: norm(parsed.product),
      service: norm(parsed.service),
      targetEnv: norm(parsed.targetEnv),
    };
  } catch {
    return SCOPE_FILTER_DEFAULT;
  }
}

export function saveScopeFilter(value: ScopeFilterValue): void {
  try {
    window.localStorage.setItem(SCOPE_FILTER_STORAGE_KEY, JSON.stringify(value));
  } catch {
    // Ignore — quota or disabled storage.
  }
}
