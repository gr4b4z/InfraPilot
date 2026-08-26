import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useDeploymentStore } from '@/stores/deploymentStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { formatDistanceToNow } from 'date-fns';
import { Rocket, Loader2, CheckCircle, AlertTriangle, Check, ChevronRight, EyeOff, Search, SearchX, X } from 'lucide-react';
import { EnvBadge, EnvLabel } from '@/components/environments/EnvBadge';
import { FilterPanel } from '@/components/ui/FilterPanel';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { useSearchScope, type SearchHit } from '@/stores/searchScopeStore';
import { useUserPrefsStore } from '@/stores/userPrefsStore';
import { api } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import type { ProductSummary, ServiceSearchResult } from '@/lib/types';

/** Below this the box is treated as empty — one letter matches half the fleet. */
const MIN_SEARCH_LENGTH = 2;

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

  // The service search box: find a service without knowing which product it lives in. Server-side,
  // because this page only ever loaded the product matrix — the services aren't here to filter.
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<ServiceSearchResult[]>([]);
  const [searching, setSearching] = useState(false);
  const trimmedQuery = searchQuery.trim();
  const searchActive = trimmedQuery.length >= MIN_SEARCH_LENGTH;

  // Clearing and the spinner flip live in the change handler rather than the fetch effect — the
  // effect only talks to the server, so it never sets state synchronously during a render pass.
  const handleSearchChange = useCallback(
    (value: string) => {
      const prevTrimmed = searchQuery.trim();
      setSearchQuery(value);
      const trimmed = value.trim();
      if (trimmed === prevTrimmed) return;
      if (trimmed.length < MIN_SEARCH_LENGTH) {
        setSearching(false);
        setSearchResults([]);
      } else {
        setSearching(true);
      }
    },
    [searchQuery],
  );

  useEffect(() => {
    if (!searchActive) return;
    let cancelled = false;
    // Debounced so a keystroke burst costs one round trip, not one per letter.
    const timer = setTimeout(() => {
      api
        .searchDeploymentServices(trimmedQuery, 50)
        .then((r) => {
          if (cancelled) return;
          setSearchResults(r.results);
          setSearching(false);
        })
        .catch(() => {
          if (cancelled) return;
          setSearchResults([]);
          setSearching(false);
        });
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [searchActive, trimmedQuery, deploymentsTick]);

  // `/` searches both: products from the loaded matrix, services via the same server search the
  // box uses — so the quick-find can also answer "where does this service live".
  useSearchScope(
    {
      label: 'Products & services',
      placeholder: 'Find a product or service…',
      search: async (query) => {
        const needle = query.toLowerCase();
        const productHits: SearchHit[] = products
          .filter((p) => p.product.toLowerCase().includes(needle))
          .slice(0, 10)
          .map((p) => ({
            id: p.product,
            title: p.product,
            subtitle: `${Object.keys(p.environments).length} environment(s)`,
            to: `/deployments/${p.product}`,
          }));
        let serviceHits: SearchHit[] = [];
        try {
          const r = await api.searchDeploymentServices(query, 15);
          serviceHits = r.results.map((s) => ({
            id: `${s.product}/${s.service}`,
            title: s.service,
            subtitle: `${s.product} · ${s.environments.map((e) => e.environment).join(', ')}`,
            to: `/deployments/${encodeURIComponent(s.product)}/${encodeURIComponent(s.service)}`,
          }));
        } catch {
          // Degrade to product hits; the box on the page will surface the error state instead.
        }
        return [...productHits, ...serviceHits];
      },
    },
    [products],
  );

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start gap-3">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            Deployments
          </h1>
          <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
            Product overview — current deployment state across environments
          </p>
        </div>
        {/* Service search: the one control on this page that reaches past the product matrix. You
           know the service's name, not its product — so it queries the server across all products
           and each hit carries the product it was found in. */}
        <div className="relative w-full sm:w-80 sm:ml-auto">
          <Search
            size={14}
            className="absolute left-3 top-1/2 -translate-y-1/2 pointer-events-none"
            style={{ color: 'var(--text-muted)' }}
          />
          <input
            type="text"
            value={searchQuery}
            onChange={(e) => handleSearchChange(e.target.value)}
            placeholder="Find a service in any product…"
            aria-label="Find a service in any product"
            className="w-full rounded-lg border pl-8 pr-8 py-1.5 text-[13px]"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-primary)',
              color: 'var(--text-primary)',
            }}
          />
          {searchQuery && (
            <button
              type="button"
              onClick={() => handleSearchChange('')}
              aria-label="Clear service search"
              className="absolute right-2 top-1/2 -translate-y-1/2 transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              <X size={14} />
            </button>
          )}
        </div>
      </div>

      {/* While a search is typed the results replace the matrix — they answer a different question
         ("where is this service") and the two lists fighting for the same viewport helps neither. */}
      {searchActive ? (
        <ServiceSearchResults
          results={searchResults}
          searching={searching}
          query={trimmedQuery}
          orderEnvironments={getOrderedEnvironments}
        />
      ) : (
        <>
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
        </>
      )}
    </div>
  );
}

/**
 * The cross-product search results: one row per (product, service) hit, most recently deployed
 * first. The whole row links to the service's detail page — the place that answers everything
 * else about it.
 */
function ServiceSearchResults({
  results,
  searching,
  query,
  orderEnvironments,
}: {
  results: ServiceSearchResult[];
  searching: boolean;
  query: string;
  orderEnvironments: (envs: string[]) => string[];
}) {
  if (searching && results.length === 0) {
    return (
      <div className="flex items-center justify-center py-20">
        <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
      </div>
    );
  }

  if (results.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-20 text-center">
        <SearchX size={40} style={{ color: 'var(--text-muted)' }} />
        <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>
          No services match “{query}”
        </p>
      </div>
    );
  }

  return (
    <div aria-busy={searching}>
      <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
        {results.length} service{results.length === 1 ? '' : 's'} matching “{query}” — most recently
        deployed first
      </p>
      <KeyboardList className="space-y-1.5 mt-2" count={results.length} ariaLabel="Service search results">
        {results.map((hit, index) => (
          <ServiceSearchRow
            key={`${hit.product}/${hit.service}`}
            index={index}
            hit={hit}
            orderEnvironments={orderEnvironments}
          />
        ))}
      </KeyboardList>
    </div>
  );
}

function ServiceSearchRow({
  index,
  hit,
  orderEnvironments,
}: {
  index: number;
  hit: ServiceSearchResult;
  orderEnvironments: (envs: string[]) => string[];
}) {
  // An anchor row, so it activates itself — the hook only adds the roving tabindex + arrow keys.
  const rowProps = useKeyboardListRow(index, () => {}, {
    role: null,
    selfActivating: true,
    label: `${hit.service} in ${hit.product}`,
  });

  const envs = orderEnvironments(hit.environments.map((e) => e.environment));

  return (
    <Link
      {...rowProps}
      to={`/deployments/${encodeURIComponent(hit.product)}/${encodeURIComponent(hit.service)}`}
      className="card-hover rounded-lg border px-3 py-2.5 flex items-center gap-3 transition-colors"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <span className="font-medium text-[13px]" style={{ color: 'var(--text-primary)' }}>
        {hit.service}
      </span>
      <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>{hit.product}</span>
      <span className="hidden sm:flex flex-wrap items-center gap-1">
        {envs.map((env) => (
          <EnvBadge key={env} env={env} size="xs" />
        ))}
      </span>
      <span className="flex-1" />
      <span className="text-[12px] whitespace-nowrap" style={{ color: 'var(--text-muted)' }}>
        {formatDistanceToNow(new Date(hit.lastDeployedAt), { addSuffix: true })}
      </span>
      <ChevronRight size={14} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
    </Link>
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
