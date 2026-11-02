import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { formatDistanceToNow } from 'date-fns';
import { Database, ExternalLink, Loader2, Rocket, Search, X } from 'lucide-react';
import { api } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { useSettingsStore } from '@/stores/settingsStore';
import { useEntityRefresh, useIsBackgroundRefresh } from '@/hooks/useEntityEvents';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { ListEmptyState } from '@/components/ui/ListEmptyState';
import { inputClass, inputStyle } from '@/app/settings/formStyles';
import {
  KIND_LABEL,
  migrationItemStyle,
  migrationPath,
  migrationStatusStyle,
} from '@/components/migrations/migrationStatus';
import { MigrationCounts } from '@/components/migrations/MigrationCounts';
import type { MigrationRunSummary, MigrationSearchResult } from '@/lib/types';

const PAGE = 100;

const STATUS_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'All statuses' },
  { value: 'attention', label: 'Needs attention' },
  { value: 'pending', label: 'Pending' },
  { value: 'running', label: 'Running' },
  { value: 'failed', label: 'Failed' },
  { value: 'succeeded', label: 'Succeeded' },
  { value: 'skipped', label: 'Skipped' },
  { value: 'superseded', label: 'Superseded' },
];

/**
 * Migrations — every database migration the deployments ran or still owe, read-only.
 *
 * Two questions bring people here. "What is waiting or broken?" is the list: schema migrations run
 * inside each deployment, data migrations after it, and a data migration the service is already
 * deployed without shows as Pending until it runs — or is superseded by a newer version's, since only
 * the newest one ever runs. "Has migration X run on staging?" is the search above it, which matches
 * partial and misspelt names and answers per service and environment.
 *
 * Filters live in the URL so a link to "failed data migrations on prod" is shareable.
 */
export function MigrationsPage() {
  useDocumentTitle(['Migrations']);
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const { activeEnvironments, getDisplayName } = useSettingsStore();

  const product = params.get('product') ?? '';
  const environment = params.get('env') ?? '';
  const kind = params.get('kind') ?? '';
  const status = params.get('status') ?? '';
  const setParam = useCallback((key: string, value: string) => {
    setParams((prev) => {
      const next = new URLSearchParams(prev);
      if (value) next.set(key, value); else next.delete(key);
      return next;
    }, { replace: true });
  }, [setParams]);

  const [products, setProducts] = useState<string[]>([]);
  useEffect(() => {
    api.getDeploymentProducts().then((l) => setProducts(l.map((p) => p.product))).catch(() => setProducts([]));
  }, []);

  const [items, setItems] = useState<MigrationRunSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const tick = useEntityRefresh(['migration']);
  const isBackgroundRefresh = useIsBackgroundRefresh();
  const queryKey = `${product}|${environment}|${kind}|${status}`;

  useEffect(() => {
    let cancelled = false;
    if (!isBackgroundRefresh(queryKey)) setLoading(true);
    api
      .listMigrations({ product, environment, kind, status, limit: PAGE })
      .then((res) => {
        if (cancelled) return;
        setItems(res.items);
        setTotal(res.total);
        setError(null);
      })
      .catch((e) => { if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load migrations'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [queryKey, product, environment, kind, status, tick, isBackgroundRefresh]);

  const loadMore = async () => {
    setLoadingMore(true);
    try {
      const res = await api.listMigrations({ product, environment, kind, status, limit: PAGE, offset: items.length });
      setItems((prev) => [...prev, ...res.items]);
      setTotal(res.total);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load more migrations');
    } finally {
      setLoadingMore(false);
    }
  };

  const clearFilters = () => setParams((prev) => {
    const next = new URLSearchParams(prev);
    ['product', 'env', 'kind', 'status'].forEach((k) => next.delete(k));
    return next;
  }, { replace: true });

  const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap';
  const td = 'px-2 py-2 whitespace-nowrap';

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2">
        <Database size={20} style={{ color: 'var(--accent)' }} />
        <div>
          <h1 className="text-xl font-semibold">Migrations</h1>
          <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            Schema migrations run inside each deployment; data migrations run after it, and only the newest
            version's ever runs. Read-only — this is what the release pipelines reported.
          </p>
        </div>
      </div>

      <MigrationSearch product={product} environment={environment} />

      <div className="flex flex-wrap items-center gap-2">
        <select value={product} onChange={(e) => setParam('product', e.target.value)} className={inputClass} style={inputStyle} aria-label="Product">
          <option value="">All products</option>
          {products.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
        <select value={environment} onChange={(e) => setParam('env', e.target.value)} className={inputClass} style={inputStyle} aria-label="Environment">
          <option value="">All environments</option>
          {activeEnvironments.map((e) => <option key={e.key} value={e.key}>{e.displayName}</option>)}
        </select>
        <select value={kind} onChange={(e) => setParam('kind', e.target.value)} className={inputClass} style={inputStyle} aria-label="Kind">
          <option value="">Schema and data</option>
          <option value="schema">Schema</option>
          <option value="data">Data</option>
        </select>
        <select value={status} onChange={(e) => setParam('status', e.target.value)} className={inputClass} style={inputStyle} aria-label="Status">
          {STATUS_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
      </div>

      {error && (
        <div className="px-4 py-3 rounded-xl border text-[13px]" style={{ borderColor: 'var(--danger)', backgroundColor: 'var(--danger-bg)', color: 'var(--danger)' }}>
          {error}
        </div>
      )}

      {loading ? (
        <div className="skeleton h-40" />
      ) : items.length === 0 ? (
        <ListEmptyState
          icon={Database}
          tone={status === 'attention' ? 'good' : product || environment || kind || status ? 'filtered' : 'neutral'}
          title={status === 'attention' ? 'Nothing needs attention' : 'No migrations'}
          body={status === 'attention'
            ? 'No migration is pending, running or failed for these filters.'
            : 'Migrations appear here as deployments report them: schema migrations from the deploy’s own output, data migrations when the release pipeline queues and runs them.'}
          filters={[
            ...(product ? [{ label: 'Product', value: product, onClear: () => setParam('product', '') }] : []),
            ...(environment ? [{ label: 'Environment', value: getDisplayName(environment), onClear: () => setParam('env', '') }] : []),
            ...(kind ? [{ label: 'Kind', value: KIND_LABEL[kind as 'schema' | 'data'] ?? kind, onClear: () => setParam('kind', '') }] : []),
            ...(status ? [{ label: 'Status', value: STATUS_OPTIONS.find((o) => o.value === status)?.label ?? status, onClear: () => setParam('status', '') }] : []),
          ]}
          onClearFilters={clearFilters}
        />
      ) : (
        <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
          <p className="text-[12px] mb-2" style={{ color: 'var(--text-muted)' }}>
            {total} {total === 1 ? 'migration' : 'migrations'}
          </p>
          <div className="overflow-x-auto -mx-2">
            <table className="w-full text-[12px]">
              <thead style={{ color: 'var(--text-muted)' }}>
                <tr>
                  <th className={th}>Due</th>
                  <th className={th}>Service</th>
                  <th className={th}>Environment</th>
                  <th className={th}>Version</th>
                  <th className={th}>Kind</th>
                  <th className={th}>Status</th>
                  <th className={th}>Migrations</th>
                  <th className={th}></th>
                </tr>
              </thead>
              <tbody>
                {items.map((m) => {
                  const st = migrationStatusStyle(m.status);
                  return (
                    <tr
                      key={m.id}
                      className="border-t cursor-pointer transition-colors hover:bg-[var(--bg-secondary)]"
                      style={{ borderColor: 'var(--border-color)' }}
                      onClick={() => navigate(migrationPath(m.id))}
                    >
                      <td className={td} title={new Date(m.queuedAt).toLocaleString()} style={{ color: 'var(--text-secondary)' }}>
                        {formatDistanceToNow(new Date(m.queuedAt), { addSuffix: true })}
                      </td>
                      <td className={td}>
                        <Link to={migrationPath(m.id)} className="hover:underline" onClick={(e) => e.stopPropagation()}>
                          <span style={{ color: 'var(--text-muted)' }}>{m.product} · </span>
                          <span style={{ color: 'var(--text-primary)' }}>{m.service}</span>
                        </Link>
                      </td>
                      <td className={td}><EnvLabel env={m.environment} /></td>
                      <td className={`${td} font-mono`}>v{m.version}</td>
                      <td className={td}>{KIND_LABEL[m.kind]}</td>
                      <td className={td}>
                        <span className="badge" style={{ backgroundColor: st.bg, color: st.fg }} title={m.failureReason ?? m.skipReason ?? undefined}>
                          {st.label}
                        </span>
                      </td>
                      <td className={td} style={{ color: 'var(--text-secondary)' }}>
                        <MigrationCounts run={m} />
                      </td>
                      <td className={`${td} text-right`}>
                        <span className="inline-flex items-center gap-2">
                          {m.deployEventId && (
                            <Link
                              to={`/deployments/events/${m.deployEventId}`}
                              onClick={(e) => e.stopPropagation()}
                              title="Open the deployment"
                              className="hover:opacity-70"
                              style={{ color: 'var(--text-muted)' }}
                            >
                              <Rocket size={13} />
                            </Link>
                          )}
                          {m.runUrl && (
                            <a
                              href={m.runUrl}
                              target="_blank"
                              rel="noopener noreferrer"
                              onClick={(e) => e.stopPropagation()}
                              title="Open the pipeline run"
                              className="hover:opacity-70"
                              style={{ color: 'var(--text-muted)' }}
                            >
                              <ExternalLink size={13} />
                            </a>
                          )}
                        </span>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {items.length < total && (
            <button
              onClick={() => void loadMore()}
              disabled={loadingMore}
              className="mt-3 inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
              style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}
            >
              {loadingMore && <Loader2 size={13} className="animate-spin" />} Show more
            </button>
          )}
        </section>
      )}
    </div>
  );
}

/**
 * "Has migration X run on staging?" A search box over every migration name on record — partial,
 * case-insensitive, tolerant of typos and word order — answering per service and environment with
 * the outcome there. The library never re-runs an applied migration, so "applied" means it ran in
 * some earlier deployment there; "not seen" means no captured output mentions it, which for a
 * migration that ran before the portal recorded migrations is not proof it never ran.
 */
function MigrationSearch({ product, environment }: { product: string; environment: string }) {
  const [params, setParams] = useSearchParams();
  const [text, setText] = useState(params.get('q') ?? '');
  const [result, setResult] = useState<MigrationSearchResult | null>(null);
  const [searching, setSearching] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { activeEnvironments, getDisplayName } = useSettingsStore();
  const abort = useRef<AbortController | null>(null);
  const query = text.trim();

  useEffect(() => {
    // Keep the query in the URL, so "has it run on staging" can be answered with a link.
    setParams((prev) => {
      const next = new URLSearchParams(prev);
      if (query) next.set('q', query); else next.delete('q');
      return next;
    }, { replace: true });
    if (query.length < 2) return;
    const handle = setTimeout(() => {
      abort.current?.abort();
      const controller = new AbortController();
      abort.current = controller;
      setSearching(true);
      api
        .searchMigrations(query, { product, environment }, controller.signal)
        .then((r) => { setResult(r); setError(null); })
        .catch((e) => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : 'Search failed'); })
        .finally(() => { if (!controller.signal.aborted) setSearching(false); });
    }, 250);
    return () => clearTimeout(handle);
  }, [query, product, environment, setParams]);

  // A query too short to search shows nothing, rather than the answer to the previous one.
  const shown = query.length >= 2 ? result : null;
  const shownError = query.length >= 2 ? error : null;

  // The environments to answer for: the filtered one, or every active one in pipeline order.
  const envs = useMemo(
    () => (environment ? [environment] : activeEnvironments.map((e) => e.key)),
    [environment, activeEnvironments],
  );

  return (
    <section className="rounded-xl border p-4 space-y-3" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <label className="block">
        <span className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>Find a migration</span>
        <span className="block text-[12px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Has it run yet, and where? Part of the name is enough, and typos are forgiven.
        </span>
        <span className="relative mt-2 flex items-center">
          <Search size={14} className="absolute left-2.5" style={{ color: 'var(--text-muted)' }} />
          <input
            type="search"
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder="e.g. AlterAccountName, account name, 4650"
            className={`${inputClass} w-full pl-8 pr-8`}
            style={inputStyle}
            aria-label="Migration name"
          />
          {searching && <Loader2 size={14} className="absolute right-2.5 animate-spin" style={{ color: 'var(--text-muted)' }} />}
          {!searching && text && (
            <button onClick={() => setText('')} className="absolute right-2.5 hover:opacity-70" aria-label="Clear search" style={{ color: 'var(--text-muted)' }}>
              <X size={14} />
            </button>
          )}
        </span>
      </label>

      {shownError && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{shownError}</p>}

      {shown && shown.matches.length === 0 && (
        <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
          No migration named like <b>{shown.query}</b> in the recorded output
          {environment ? <> for <b>{getDisplayName(environment)}</b></> : null}
          {product ? <> of <b>{product}</b></> : null}.
        </p>
      )}

      {shown && shown.matches.length > 0 && (
        <div className="space-y-3">
          {shown.matches.map((match) => {
            const services = [...new Set(match.hits.map((h) => `${h.product}\u0000${h.service}`))];
            return (
              <div key={match.name} className="rounded-lg border" style={{ borderColor: 'var(--border-color)' }}>
                <div className="px-3 py-2 flex items-center gap-2 border-b" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
                  <Database size={13} style={{ color: 'var(--text-muted)' }} />
                  <span className="font-mono text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>{match.name}</span>
                  {match.score < 100 && (
                    <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      {match.score >= 85 ? 'contains your search' : 'close match'}
                    </span>
                  )}
                </div>
                <div className="overflow-x-auto">
                  <table className="w-full text-[12px]">
                    <thead style={{ color: 'var(--text-muted)' }}>
                      <tr>
                        <th className="text-left font-medium px-3 py-1.5">Service</th>
                        {envs.map((env) => (
                          <th key={env} className="text-left font-medium px-3 py-1.5 whitespace-nowrap"><EnvLabel env={env} /></th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {services.map((key) => {
                        const [p, s] = key.split('\u0000');
                        return (
                          <tr key={key} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                            <td className="px-3 py-2 whitespace-nowrap">
                              <span style={{ color: 'var(--text-muted)' }}>{p} · </span>{s}
                            </td>
                            {envs.map((env) => {
                              const hit = match.hits.find((h) => h.product === p && h.service === s && h.environment === env);
                              if (!hit) {
                                return (
                                  <td key={env} className="px-3 py-2 whitespace-nowrap" style={{ color: 'var(--text-muted)' }}
                                    title="No captured migration output mentions it here">
                                    not seen
                                  </td>
                                );
                              }
                              const st = migrationItemStyle(hit.status);
                              const when = hit.appliedAt ?? hit.seenAt;
                              return (
                                <td key={env} className="px-3 py-2 whitespace-nowrap">
                                  <Link
                                    to={migrationPath(hit.runId)}
                                    className="inline-flex items-center gap-1.5 hover:underline"
                                    title={`${st.label} in v${hit.version}${hit.error ? ` — ${hit.error}` : ''} · ${new Date(when).toLocaleString()}`}
                                  >
                                    <span className="font-semibold" style={{ color: st.fg }}>{st.mark} {st.label}</span>
                                    <span style={{ color: 'var(--text-muted)' }}>
                                      {formatDistanceToNow(new Date(when), { addSuffix: true })} · v{hit.version}
                                    </span>
                                  </Link>
                                </td>
                              );
                            })}
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </div>
            );
          })}
          {shown.truncated && (
            <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>More migrations match — type more of the name to narrow it down.</p>
          )}
          <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            “Not seen” means no captured migration output mentions it there. A migration applied before the portal
            started recording migrations may still have run.
          </p>
        </div>
      )}
    </section>
  );
}
