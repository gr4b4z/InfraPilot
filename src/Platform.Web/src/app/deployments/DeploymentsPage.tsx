import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useDeploymentStore } from '@/stores/deploymentStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { formatDistanceToNow } from 'date-fns';
import { Rocket, Loader2, CheckCircle, AlertTriangle, Check, EyeOff } from 'lucide-react';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { FilterPanel } from '@/components/ui/FilterPanel';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { useSearchScope } from '@/stores/searchScopeStore';
import { useUserPrefsStore } from '@/stores/userPrefsStore';
import { api } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import type { ProductSummary } from '@/lib/types';

export function DeploymentsPage() {
  const { products, loading, fetchProducts } = useDeploymentStore();
  const { getOrderedEnvironments } = useSettingsStore();
  const navigate = useNavigate();

  // Which products are shown is a per-user preference rather than URL state, so there is nothing
  // link-specific to report here — this page is the same page for everyone it's sent to.
  useDocumentTitle(['Deployments']);

  // The hidden set is applied by the API, so `products` already excludes them and this page never
  // filters anything itself. What it owns is the control — and the control is the one thing that
  // has to see hidden products, otherwise there is no way to bring one back. Hence the separate
  // fetch: /me/preferences/products is deliberately unfiltered.
  const hiddenProducts = useUserPrefsStore((s) => s.hiddenProducts);
  const saving = useUserPrefsStore((s) => s.saving);
  const setHiddenProducts = useUserPrefsStore((s) => s.setHiddenProducts);
  const [allProductNames, setAllProductNames] = useState<string[]>([]);

  // New deploys move products' latest-activity ordering and env freshness on this index.
  const deploymentsTick = useEntityRefresh(['deployment']);

  useEffect(() => {
    fetchProducts();
  }, [fetchProducts, deploymentsTick]);

  useEffect(() => {
    let cancelled = false;
    api
      .getMyProductVisibility()
      .then((r) => {
        if (!cancelled) setAllProductNames(r.products);
      })
      .catch(() => {
        // Fall back to what the (filtered) matrix knows about. The user can still hide things;
        // they just can't unhide from here until the call succeeds.
        if (!cancelled) setAllProductNames([]);
      });
    return () => {
      cancelled = true;
    };
  }, [hiddenProducts]);

  const hiddenSet = useMemo(() => new Set(hiddenProducts), [hiddenProducts]);

  // Union so the control still lists a product that is hidden AND absent from the current matrix
  // (retired, renamed, or simply hidden — the matrix can't see it either).
  const controlProducts = useMemo(() => {
    const names = new Set<string>(allProductNames);
    for (const p of products) names.add(p.product);
    for (const h of hiddenProducts) names.add(h);
    return Array.from(names).sort((a, b) => a.localeCompare(b));
  }, [allProductNames, products, hiddenProducts]);

  const toggleProduct = useCallback(
    (product: string) => {
      const next = new Set(hiddenProducts);
      if (!next.delete(product)) next.add(product);
      void setHiddenProducts(Array.from(next));
    },
    [hiddenProducts, setHiddenProducts],
  );

  const showAllProducts = useCallback(() => {
    void setHiddenProducts([]);
  }, [setHiddenProducts]);

  // Columns follow the rows the API returned: hiding a product also drops the environments only it
  // deployed to, which is the point of hiding it — a matrix of mostly-empty columns is the thing
  // being filtered away, not just the rows.
  const allEnvs = getOrderedEnvironments(
    Array.from(new Set(products.flatMap((p) => Object.keys(p.environments)))),
  );

  const hiddenCount = hiddenProducts.length;

  // The pill on the filter control: how many products are showing. `activeCount` below carries the
  // hidden count instead, purely to drive the accent treatment and the auto-open.
  const shownCount = products.length;

  // `/` searches products here — the only thing this page lists.
  useSearchScope(
    {
      label: 'Products',
      placeholder: 'Find a product…',
      search: async (query) => {
        const needle = query.toLowerCase();
        return products
          .filter((p) => p.product.toLowerCase().includes(needle))
          .slice(0, 25)
          .map((p) => ({
            id: p.product,
            title: p.product,
            subtitle: `${Object.keys(p.environments).length} environment(s)`,
            to: `/deployments/${p.product}`,
          }));
      },
    },
    [products],
  );

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Deployments
        </h1>
        <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
          Product overview — current deployment state across environments
        </p>
      </div>

      {/* Which products the matrix shows. A chip per product rather than a multi-select: the set is
         small, and a control you can read the current state off at a glance beats one you have to
         open.

         The count reports how many products are showing, not how many are hidden — what you want
         read back is the set you kept. `activeCount` still carries the hidden count internally, so
         the toggle goes accent and starts open when something is filtered out.

         It appears twice by breakpoint, never both at once: on the collapsed toggle below `lg`,
         and at the end of the chip row from `lg` up, where the toggle itself is hidden and there
         would otherwise be nowhere to read it. */}
      {!loading && controlProducts.length > 0 && (
        <FilterPanel label="Products" activeCount={hiddenCount} badge={shownCount}>
          <div className="flex flex-wrap items-center gap-1.5" aria-busy={saving}>
            {controlProducts.map((name) => {
              const shown = !hiddenSet.has(name);
              return (
                <button
                  key={name}
                  type="button"
                  disabled={saving}
                  onClick={() => toggleProduct(name)}
                  aria-pressed={shown}
                  title={shown ? `Hide ${name} everywhere` : `Show ${name} again`}
                  className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full border text-[12px] font-medium transition-colors disabled:opacity-60"
                  style={{
                    borderColor: shown ? 'var(--accent)' : 'var(--border-color)',
                    backgroundColor: shown ? 'var(--accent-bg)' : 'transparent',
                    color: shown ? 'var(--accent)' : 'var(--text-muted)',
                  }}
                >
                  {shown ? <Check size={12} /> : <EyeOff size={12} />}
                  {name}
                </button>
              );
            })}
            <span
              className="hidden lg:inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold"
              style={{
                backgroundColor: hiddenCount > 0 ? 'var(--accent)' : 'var(--bg-secondary)',
                color: hiddenCount > 0 ? '#fff' : 'var(--text-muted)',
              }}
              title={`${shownCount} of ${controlProducts.length} product(s) shown`}
            >
              {shownCount}
            </span>
            {hiddenCount > 0 && (
              <button
                type="button"
                disabled={saving}
                onClick={showAllProducts}
                className="px-2 py-1 text-[12px] font-medium underline underline-offset-2 transition-opacity hover:opacity-80 disabled:opacity-60"
                style={{ color: 'var(--text-muted)' }}
              >
                Show all
              </button>
            )}
          </div>
        </FilterPanel>
      )}

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
        </div>
      ) : products.length === 0 && hiddenCount === 0 ? (
        <div className="flex flex-col items-center justify-center py-20 text-center">
          <Rocket size={40} style={{ color: 'var(--text-muted)' }} />
          <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>No deployments recorded yet</p>
        </div>
      ) : products.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-20 text-center">
          <EyeOff size={40} style={{ color: 'var(--text-muted)' }} />
          <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>
            Every product is hidden
          </p>
          <button
            type="button"
            onClick={showAllProducts}
            className="mt-2 text-sm font-medium transition-opacity hover:opacity-80"
            style={{ color: 'var(--accent)' }}
          >
            Show all products
          </button>
        </div>
      ) : (
        <div className="rounded-xl border overflow-x-auto" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
          <table className="w-full min-w-max text-[13px]">
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border-color)' }}>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Product</th>
                {/* Colour-coded so a column can be picked out at a glance in a wide table. */}
                {allEnvs.map((env) => (
                  <th key={env} className="text-center px-4 py-3 font-medium">
                    <EnvLabel env={env} />
                  </th>
                ))}
              </tr>
            </thead>
            <KeyboardList as="tbody" count={products.length} ariaLabel="Products">
              {products.map((product, index) => (
                  <ProductRow
                    key={product.product}
                    index={index}
                    product={product}
                    allEnvs={allEnvs}
                    onOpen={() => navigate(`/deployments/${product.product}`)}
                  />
              ))}
            </KeyboardList>
          </table>
        </div>
      )}
    </div>
  );
}

/**
 * One product's row in the overview. Focusable so the matrix can be walked with the arrow keys and
 * opened with Enter — the row is the only way into a product's deployments, and as a bare
 * `<tr onClick>` it had no keyboard route at all.
 */
function ProductRow({
  index,
  product,
  allEnvs,
  onOpen,
}: {
  index: number;
  product: ProductSummary;
  allEnvs: string[];
  onOpen: () => void;
}) {
  // `role: null` keeps the implicit row semantics — see useKeyboardListRow.
  const rowProps = useKeyboardListRow(index, onOpen, {
    role: null,
    label: `${product.product} — open deployments`,
  });

  return (
    <tr
      {...rowProps}
      className="cursor-pointer transition-colors hover:opacity-80"
      style={{ borderBottom: '1px solid var(--border-color)' }}
    >
      <td className="px-4 py-3 font-medium" style={{ color: 'var(--text-primary)' }}>
        {product.product}
      </td>
      {allEnvs.map((env) => {
        const summary = product.environments[env];
        if (!summary) {
          return (
            <td key={env} className="text-center px-4 py-3" style={{ color: 'var(--text-muted)' }}>
              —
            </td>
          );
        }
        const allDeployed = summary.deployedServices === summary.totalServices;
        return (
          <td key={env} className="text-center px-4 py-2">
            <div className="inline-flex flex-col items-center gap-0.5">
              <span className="inline-flex items-center gap-1" style={{ color: allDeployed ? 'var(--success)' : 'var(--warning)' }}>
                {allDeployed ? <CheckCircle size={13} /> : <AlertTriangle size={13} />}
                {summary.deployedServices}/{summary.totalServices}
              </span>
              {summary.lastDeployedAt && (
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  {formatDistanceToNow(new Date(summary.lastDeployedAt), { addSuffix: true })}
                </span>
              )}
            </div>
          </td>
        );
      })}
    </tr>
  );
}
