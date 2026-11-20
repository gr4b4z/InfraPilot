import { useEffect, useState } from 'react';
import { BookDashed, ExternalLink, Info } from 'lucide-react';
import { api } from '@/lib/api';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { useSettingsStore } from '@/stores/settingsStore';
import { FeatureFlag, useFeatureFlag } from '@/stores/featureFlagsStore';
import { resolveEnvKey } from '@/lib/envAlias';
import type {
  ApiError,
  Freshness as FreshnessInfo,
  InventoryObservedResponse,
  InventoryServiceCostResponse,
  InventoryServiceResponse,
  OnboardingStep,
} from './inventoryTypes';
import { SERIES_DIRECT, SERIES_SHARED, declaredIn, findBinding, isPlaceholder, isZero, monthLabel, repoLink } from './inventoryFormat';
import { Freshness, InventoryLink, MoneyLines, SectionCard, StateChip, Swatch } from './InventoryUi';

const ONBOARDING_STEPS: [string, string][] = [
  ['repo', 'Repository'],
  ['component_file', 'Component file'],
  ['namespace', 'Namespace'],
  ['identity', 'Identity'],
  ['contract', 'Contract'],
  ['resources', 'Resources'],
  ['dev_deploy', 'First dev deploy'],
];

const LIFECYCLE_TONE: Record<string, string> = {
  planned: 'var(--info)',
  production: 'var(--success)',
  deprecated: 'var(--warning)',
  retiring: 'var(--warning)',
  retired: 'var(--text-muted)',
};

/** One of the three reads: the data, "not there" (with how old the feed is), or a failure. */
type Loaded<T> = { data: T } | { missing: true; freshness?: FreshnessInfo } | { failed: true };

async function load<T>(call: Promise<T>): Promise<Loaded<T>> {
  try {
    return { data: await call };
  } catch (err) {
    const e = err as ApiError;
    return e.status === 404 ? { missing: true, freshness: e.body?.freshness } : { failed: true };
  }
}

/**
 * The service inventory on the service page: who owns the service, where it runs, its declared
 * databases with their observed state, its resources and what it cost this month. Read-only, from
 * the published catalog, status and cost files; rendered only while the feature is on.
 */
export function ServiceInventorySection({ product, service }: { product: string; service: string }) {
  const enabled = useFeatureFlag(FeatureFlag.ServiceInventory);
  const environments = useSettingsStore((s) => s.environments);

  const [entry, setEntry] = useState<Loaded<InventoryServiceResponse> | null>(null);
  const [observed, setObserved] = useState<Loaded<InventoryObservedResponse> | null>(null);
  const [cost, setCost] = useState<Loaded<InventoryServiceCostResponse> | null>(null);

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    Promise.all([
      load(api.getInventoryService(service, product)),
      load(api.getInventoryObserved(service)),
      load(api.getInventoryServiceCost(service)),
    ]).then(([e, o, c]) => {
      if (cancelled) return;
      setEntry(e);
      setObserved(o);
      setCost(c);
    });
    return () => {
      cancelled = true;
    };
  }, [enabled, product, service]);

  if (!enabled || !entry) return null;

  const heading = (
    <h2 className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>
      Service inventory
    </h2>
  );

  if ('failed' in entry) {
    return (
      <section data-guide-anchor="service-inventory" className="space-y-2">
        {heading}
        <p className="text-sm" style={{ color: 'var(--text-muted)' }}>
          The service inventory could not be loaded. Try again in a moment.
        </p>
      </section>
    );
  }

  if ('missing' in entry) {
    return (
      <section data-guide-anchor="service-inventory" className="space-y-3">
        {heading}
        <div
          className="rounded-xl border px-4 py-5 flex items-start gap-3"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <div
            className="w-9 h-9 rounded-lg flex items-center justify-center shrink-0"
            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
          >
            <BookDashed size={18} />
          </div>
          <div className="text-[13px] min-w-0">
            <p className="font-medium" style={{ color: 'var(--text-primary)' }}>
              {service} is not in the service inventory yet
            </p>
            <p className="mt-0.5 max-w-[70ch]" style={{ color: 'var(--text-muted)' }}>
              No inventory entry has the id <span className="font-mono">{service.toLowerCase()}</span>, so the portal
              can’t show its owner, resources or cost. Services are added by a pull request to the catalog repository.
            </p>
            <div className="mt-2">
              <Freshness feeds={[entry.freshness]} />
            </div>
          </div>
        </div>
      </section>
    );
  }

  const { service: svc, team, product: catalogProduct, portal } = entry.data;
  const state = observed && 'data' in observed ? observed.data : null;
  const month = cost && 'data' in cost ? cost.data : null;
  const envKeys = svc.environments.map((e) => e.environment);
  const repo = repoLink(svc.repo);
  // Already narrowed to this service by the API (bound servers, and databases that name it).
  const sharedUsed = state?.azure?.shared ?? [];
  const tagged = (state?.azure?.taggedResources ?? []).flatMap((t) => t.resources ?? []);

  return (
    <section data-guide-anchor="service-inventory" className="space-y-3">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        {heading}
        {svc.description && (
          <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>{svc.description}</span>
        )}
      </div>

      <Freshness
        feeds={[
          entry.data.freshness,
          observed && ('data' in observed ? observed.data.freshness : 'missing' in observed ? observed.freshness : null),
          cost && ('data' in cost ? cost.data.freshness : 'missing' in cost ? cost.freshness : null),
        ]}
      />

      {state && (state.unverified || state.stale || state.warnings.length > 0) && (
        <div
          className="rounded-lg border px-3 py-2 flex items-start gap-2 text-[12px]"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
        >
          <Info size={14} className="mt-0.5 shrink-0" style={{ color: 'var(--info)' }} />
          <span>
            {state.unverified
              ? 'Only the central catalog entry is known: the service’s own file has never been readable, so its workloads and bindings are missing. '
              : state.stale
                ? 'The service’s own file could not be read this time; older details are shown. '
                : ''}
            {state.warnings.map((w) => w.message).filter(Boolean).join(' ')}
          </span>
        </div>
      )}

      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
        {/* ── Owner ── */}
        <SectionCard
          title="Owner"
          aside={svc.lifecycle && (
            <span
              className="text-[10px] font-semibold uppercase tracking-wide"
              style={{ color: LIFECYCLE_TONE[svc.lifecycle] ?? 'var(--text-muted)' }}
            >
              {svc.lifecycle}
            </span>
          )}
        >
          <dl className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1.5 text-[13px]">
            <dt style={{ color: 'var(--text-muted)' }}>Team</dt>
            <dd className="min-w-0">
              {svc.team
                ? <InventoryLink to={`/inventory/cost?team=${encodeURIComponent(svc.team)}`}>{svc.team}</InventoryLink>
                : <NotSet />}
            </dd>
            <dt style={{ color: 'var(--text-muted)' }}>Lead</dt>
            <dd><Person value={team?.lead} /></dd>
            <dt style={{ color: 'var(--text-muted)' }}>Deputy</dt>
            <dd><Person value={team?.deputy} /></dd>
            <dt style={{ color: 'var(--text-muted)' }}>Product</dt>
            <dd>
              {svc.product
                ? (
                  <InventoryLink to={`/inventory/cost?product=${encodeURIComponent(svc.product)}`}>
                    {catalogProduct?.name ?? svc.product}
                  </InventoryLink>
                )
                : <NotSet />}
              {portal?.productDiffers && (
                <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  Deploys report it under {portal.product}
                </span>
              )}
            </dd>
            {svc.repo && (
              <>
                <dt style={{ color: 'var(--text-muted)' }}>Repository</dt>
                <dd className="min-w-0 truncate">
                  {repo ? (
                    <a
                      href={repo.href}
                      target="_blank"
                      rel="noreferrer noopener"
                      className="inline-flex items-center gap-1 hover:underline max-w-full"
                      style={{ color: 'var(--accent)' }}
                    >
                      <span className="truncate">{repo.label}</span>
                      <ExternalLink size={11} className="shrink-0" />
                    </a>
                  ) : (
                    <span style={{ color: 'var(--text-secondary)' }}>{svc.repo}</span>
                  )}
                </dd>
              </>
            )}
            {(svc.type || svc.exposure) && (
              <>
                <dt style={{ color: 'var(--text-muted)' }}>Kind</dt>
                <dd style={{ color: 'var(--text-secondary)' }}>{[svc.type, svc.exposure].filter(Boolean).join(' · ')}</dd>
              </>
            )}
          </dl>
        </SectionCard>

        {/* ── Where it runs ── */}
        <SectionCard title="Runs in">
          {svc.environments.length === 0 ? (
            <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>No environments declared.</p>
          ) : (
            <table className="w-full text-[12px]">
              <tbody>
                {svc.environments.map((env) => (
                  <tr key={env.environment}>
                    <td className="py-1 pr-2 align-top">
                      <EnvBadge env={resolveEnvKey(env.environment, environments)} size="xs" />
                    </td>
                    <td className="py-1 font-mono break-all" style={{ color: 'var(--text-secondary)' }}>
                      {env.cluster ?? '—'}
                      <span style={{ color: 'var(--text-muted)' }}> / </span>
                      {env.namespace ?? '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {svc.workloads && svc.workloads.length > 0 && (
            <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
              Workloads:{' '}
              {svc.workloads.map((w, i) => (
                <span key={w.name}>
                  {i > 0 && ', '}
                  <span className="font-mono" style={{ color: 'var(--text-secondary)' }}>{w.name}</span>
                  {w.kind && ` (${w.kind})`}
                </span>
              ))}
            </p>
          )}
        </SectionCard>

        {/* ── Cost of the month ── direct and shared apart, never one number without both. */}
        <SectionCard
          title={month ? `Cost · ${monthLabel(month.period)}` : 'Cost'}
          aside={month && !month.complete && <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>to date</span>}
          className="md:col-span-2 xl:col-span-1"
        >
          {!month ? (
            <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
              {cost && 'failed' in cost ? 'The cost could not be loaded.' : 'No cost has been read for this service yet.'}
            </p>
          ) : (
            <>
              <div className="grid grid-cols-[1fr_auto] gap-y-1.5 text-[13px]">
                <span className="inline-flex items-center gap-1.5" style={{ color: 'var(--text-secondary)' }}>
                  <Swatch color={SERIES_DIRECT} /> Direct
                </span>
                <MoneyLines money={month.direct} className="font-medium" />
                <span className="inline-flex items-center gap-1.5" style={{ color: 'var(--text-secondary)' }}>
                  <Swatch color={SERIES_SHARED} /> Share of shared resources
                </span>
                <MoneyLines money={month.shared} className="font-medium" />
                <span className="pt-1.5 border-t font-semibold" style={{ borderColor: 'var(--border-color)' }}>Total</span>
                <MoneyLines money={month.total} className="pt-1.5 border-t font-semibold" />
              </div>
              {month.sharedFrom.length > 0 && (
                <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  Shared from {month.sharedFrom.map((s) => s.shared).join(', ')}
                </p>
              )}
              {isZero(month.total) ? (
                <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                  No cost is attributed to this service this month. Cost only counts for a service when its
                  resources carry the service’s tag. <InventoryLink to="/inventory/coverage">See coverage</InventoryLink>
                </p>
              ) : svc.product && (
                <InventoryLink to={`/inventory/cost?product=${encodeURIComponent(svc.product)}`}>
                  Cost of {catalogProduct?.name ?? svc.product}
                </InventoryLink>
              )}
            </>
          )}
        </SectionCard>

        {/* ── Databases ── declared in the catalog, observed in Azure. */}
        <SectionCard title="Databases" className="md:col-span-2">
          {svc.databases.length === 0 ? (
            <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
              {state?.unverified ? 'Unknown until the service’s own file is readable.' : 'No databases declared.'}
            </p>
          ) : (
            <div className="overflow-x-auto -mx-1">
              <table className="w-full text-[12px] min-w-[420px]">
                <thead>
                  <tr className="text-left" style={{ color: 'var(--text-muted)' }}>
                    <th className="font-medium px-1 pb-1.5">Database</th>
                    <th className="font-medium px-1 pb-1.5">Server</th>
                    {envKeys.map((env) => (
                      <th key={env} className="font-medium px-1 pb-1.5">
                        <EnvBadge env={resolveEnvKey(env, environments)} size="xs" />
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {svc.databases.map((db) => (
                    <tr key={`${db.kind}:${db.server ?? ''}:${db.database}`} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                      <td className="px-1 py-1.5">
                        <span className="font-mono" style={{ color: 'var(--text-primary)' }}>{db.database}</span>
                        <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>
                          {db.kind}{db.owner ? ' · owner' : ''}
                        </span>
                      </td>
                      <td className="px-1 py-1.5 font-mono" style={{ color: 'var(--text-secondary)' }}>
                        {db.server ?? 'default'}
                      </td>
                      {envKeys.map((env) => {
                        // No observation for this environment is "not observed", never "absent".
                        const seen = findBinding(state?.azure?.bindings, env, db);
                        // Not declared for this environment (and nothing seen): not a question to answer.
                        if (!seen && !declaredIn(db, env)) {
                          return (
                            <td key={env} className="px-1 py-1.5" style={{ color: 'var(--text-muted)' }}>
                              <span title="Not declared for this environment">—</span>
                            </td>
                          );
                        }
                        return (
                          <td key={env} className="px-1 py-1.5">
                            <StateChip state={seen?.state} detail={seen?.resource} />
                          </td>
                        );
                      })}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {state?.azure?.observedAt && (
            <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
              Observed in Azure {new Date(state.azure.observedAt).toLocaleString()}
            </p>
          )}
        </SectionCard>

        {/* ── Resources ── tagged with this service, plus the shared ones it uses. */}
        <SectionCard title="Resources">
          <div className="space-y-2 text-[12px]">
            {tagged.length === 0 ? (
              <p style={{ color: 'var(--text-muted)' }}>
                {state?.azure
                  ? <>No Azure resource carries the tag <span className="font-mono">service={svc.id}</span> yet.</>
                  : 'Azure has not been observed yet.'}
              </p>
            ) : (
              tagged.map((r, i) => (
                <div key={r.id ?? i} title={r.id}>
                  <span className="font-mono break-all" style={{ color: 'var(--text-primary)' }}>{r.id?.split('/').pop()}</span>
                  {r.type && <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>{r.type}</span>}
                </div>
              ))
            )}
            {sharedUsed.map((sh) => (
              <div key={sh.id} className="flex items-start gap-2">
                <div className="min-w-0 flex-1">
                  <span className="font-mono" style={{ color: 'var(--text-primary)' }}>{sh.id}</span>
                  <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>
                    shared · {sh.resources.length} resource{sh.resources.length === 1 ? '' : 's'}
                  </span>
                </div>
                <StateChip state={sh.state} />
              </div>
            ))}
          </div>
        </SectionCard>

        {/* ── Onboarding ── the steps from the status file; unknown is not a failure. */}
        {state?.onboarding && (
          <SectionCard title="Onboarding" className="md:col-span-2 xl:col-span-3">
            <ul className="grid gap-x-4 gap-y-1.5 sm:grid-cols-2 lg:grid-cols-4 text-[12px]">
              {ONBOARDING_STEPS.map(([key, label]) => {
                const step = state.onboarding?.[key];
                if (!step || typeof step === 'string') return null;
                return (
                  <li key={key} className="flex items-center gap-2">
                    <span className="flex-1" style={{ color: 'var(--text-secondary)' }}>{label}</span>
                    <StateChip state={(step as OnboardingStep).status} detail={(step as OnboardingStep).detail} />
                  </li>
                );
              })}
            </ul>
          </SectionCard>
        )}
      </div>
    </section>
  );
}

function NotSet() {
  return (
    <span className="text-[12px] italic" style={{ color: 'var(--text-muted)' }} title="Not filled in the catalog yet">
      not set
    </span>
  );
}

function Person({ value }: { value?: string | null }) {
  return isPlaceholder(value) ? <NotSet /> : <span>{value}</span>;
}
