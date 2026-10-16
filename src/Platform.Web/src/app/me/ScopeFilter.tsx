import { useMemo } from 'react';
import type { PendingTicket } from '@/lib/api';
import { useEnvControlStyle } from '@/components/environments/useEnvColor';
import { useSettingsStore } from '@/stores/settingsStore';
import { filterLabelClass, filterSelectClass } from '@/components/ui/FilterPanel';

/**
 * Picker for narrowing the My-queue list by product / service / environment.
 *
 * Two environment axes, because a work item sits on two: the promotion edge its sign-off gates
 * ("Target env") and the environments its change is actually deployed to ("Testable in"). The
 * second is what "the staging work items" normally means, and it's the one a reviewer can act on.
 *
 * Side-by-side native selects. Pure client-side filtering — the queue endpoint already returns
 * only what the user is authorised to sign off, so we just narrow what's already loaded. Each
 * dropdown is populated from the unfiltered ticket list so the user never sees a zero-result
 * option.
 *
 * Independent dropdowns (no hierarchical narrowing): if a (product, service) combination
 * has no tickets, the empty-state in MyQueuePage explains.
 */
export type ScopeFilterValue = {
  product: string | null;
  service: string | null;
  targetEnv: string | null;
  /**
   * An environment the change is actually running in — matched against the row's deployed
   * environments, not the promotion edge. "The staging work items" usually means these: where the
   * change can be exercised, which is the question a reviewer is asking. `targetEnv` answers the
   * different question of which promotion the sign-off gates.
   */
  deployedEnv: string | null;
};

export const SCOPE_FILTER_DEFAULT: ScopeFilterValue = {
  product: null,
  service: null,
  targetEnv: null,
  deployedEnv: null,
};

const ANY = '__any__';

/**
 * The option list for a select, guaranteed to contain the value it is currently showing.
 *
 * Every dropdown here is populated from the rows that happen to be loaded, and the pick is persisted
 * across tabs and sessions — so a saved value routinely isn't in the list any more (a different tab
 * returns a different set of environments, the last item on a service gets signed off). A
 * <c>&lt;select&gt;</c> whose value matches no option doesn't fail: the browser quietly displays the
 * first one. That renders as "Any env" while the filter is still narrowing the list — and for the two
 * environment selects it keeps the environment's colour too, so the control ends up green and reading
 * "Any env" at the same time.
 *
 * Appending the value keeps the control honest. It is deliberately not <i>reset</i> instead: the rows
 * are fetched per tab, so "not in the current options" is a normal transient state, and clearing the
 * filter on every tab switch would throw away a pick the user still wants. When it genuinely matches
 * nothing, the queue's empty state already says so.
 */
function withCurrentValue(options: string[], value: string | null): string[] {
  if (!value || options.includes(value)) return options;
  return [...options, value];
}

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
  const getOrderedEnvironments = useSettingsStore((s) => s.getOrderedEnvironments);
  const { products, services, targetEnvs, deployedEnvs } = useMemo(() => {
    const p = new Set<string>();
    const s = new Set<string>();
    const e = new Set<string>();
    const d = new Set<string>();
    for (const t of tickets) {
      if (t.product) p.add(t.product);
      if (t.service) s.add(t.service);
      if (t.targetEnv) e.add(t.targetEnv);
      for (const env of t.environments ?? []) {
        if (env.environment) d.add(env.environment);
      }
    }
    const sortAlpha = (a: string, b: string) =>
      a.localeCompare(b, undefined, { sensitivity: 'base' });
    return {
      // Each list also carries the currently-selected value, even when no loaded row has it — see
      // withCurrentValue for why a select that can't render its own value is the bug being fixed.
      products: withCurrentValue([...p].sort(sortAlpha), value.product),
      services: withCurrentValue([...s].sort(sortAlpha), value.service),
      // Environments keep their configured order — that's the deployment order (dev → staging →
      // prod), which is the sequence a reader is thinking in. Alphabetising it would interleave the
      // pipeline stages meaninglessly.
      targetEnvs: getOrderedEnvironments(withCurrentValue([...e], value.targetEnv)),
      deployedEnvs: getOrderedEnvironments(withCurrentValue([...d], value.deployedEnv)),
    };
  }, [tickets, getOrderedEnvironments, value.product, value.service, value.targetEnv, value.deployedEnv]);

  const setField = <K extends keyof ScopeFilterValue>(key: K, raw: string) => {
    onChange({ ...value, [key]: raw === ANY ? null : raw });
  };

  const envSelectStyle = useEnvControlStyle(value.targetEnv);
  const deployedEnvSelectStyle = useEnvControlStyle(value.deployedEnv);
  const getDisplayName = useSettingsStore((s) => s.getDisplayName);

  return (
    <>
      <label
        className={filterLabelClass}
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Product</span>
        <select
          value={value.product ?? ANY}
          onChange={(e) => setField('product', e.target.value)}
          className={filterSelectClass}
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
        className={filterLabelClass}
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Service</span>
        <select
          value={value.service ?? ANY}
          onChange={(e) => setField('service', e.target.value)}
          className={filterSelectClass}
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
        className={filterLabelClass}
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Target env</span>
        {/* Takes the selected environment's colour so an active env narrowing is visible
            at a glance alongside the queue rows it filtered. */}
        <select
          value={value.targetEnv ?? ANY}
          onChange={(e) => setField('targetEnv', e.target.value)}
          className={filterSelectClass}
          style={envSelectStyle}
        >
          <option value={ANY}>Any env</option>
          {targetEnvs.map((e) => (
            <option key={e} value={e}>{getDisplayName(e)}</option>
          ))}
        </select>
      </label>

      {/* Where the change is running, which is the environment a reviewer means when they say
          "the staging items" — the rows label it "Testable in". Only offered when there is something
          to narrow by, which includes an active pick: without that, a saved value on a queue with no
          deploy data hid the whole control while still filtering the list by it. */}
      {deployedEnvs.length > 0 && (
        <label
          className={filterLabelClass}
          style={{ color: 'var(--text-muted)' }}
        >
          <span>Testable in</span>
          <select
            value={value.deployedEnv ?? ANY}
            onChange={(e) => setField('deployedEnv', e.target.value)}
            className={filterSelectClass}
            style={deployedEnvSelectStyle}
          >
            <option value={ANY}>Any env</option>
            {deployedEnvs.map((e) => (
              <option key={e} value={e}>{getDisplayName(e)}</option>
            ))}
          </select>
        </label>
      )}
    </>
  );
}

/** Pure helper: applies a scope filter to a ticket list. Null fields mean "any". */
export function applyScopeFilter(
  tickets: PendingTicket[],
  filter: ScopeFilterValue,
): PendingTicket[] {
  if (!hasActiveScope(filter)) return tickets;
  return tickets.filter((t) => {
    if (filter.product && t.product !== filter.product) return false;
    if (filter.service && t.service !== filter.service) return false;
    if (filter.targetEnv && t.targetEnv !== filter.targetEnv) return false;
    if (
      filter.deployedEnv
      && !(t.environments ?? []).some((e) => e.environment === filter.deployedEnv)
    ) {
      return false;
    }
    return true;
  });
}

/** True when at least one of the scope dropdowns is narrowing. */
export function hasActiveScope(filter: ScopeFilterValue): boolean {
  return filter.product !== null
    || filter.service !== null
    || filter.targetEnv !== null
    || filter.deployedEnv !== null;
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
      // Absent in payloads saved before this dropdown existed — reads as "any".
      deployedEnv: norm(parsed.deployedEnv),
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
