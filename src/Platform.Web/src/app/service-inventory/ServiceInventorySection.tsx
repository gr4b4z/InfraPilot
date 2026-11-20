/**
 * PROTOTYPE — the service inventory section on the service page (/deployments/:product/:service).
 * Owner, where it runs, declared databases with their observed state, resources, cost of the month.
 */
import { useMemo } from 'react';
import { BookDashed, ExternalLink, Info } from 'lucide-react';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { useSettingsStore } from '@/stores/settingsStore';
import { resolveEnvKey } from '@/lib/envAlias';
import {
  catalog,
  currentMonth,
  feedStates,
  findCatalogService,
  getStatus,
  isPlaceholder,
  monthLabel,
  productName,
  type DbBinding,
} from './inventoryData';
import {
  Freshness,
  InventoryLink,
  MoneyLines,
  PrototypeBar,
  SectionCard,
  StateChip,
} from './InventoryUi';
import { useScenario } from './inventoryHooks';

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

export function ServiceInventorySection({ product, service }: { product: string; service: string }) {
  const scenario = useScenario();
  const feeds = feedStates(scenario);
  const status = getStatus(scenario);
  const entry = findCatalogService(service);
  const environments = useSettingsStore((s) => s.environments);

  const observed = status.services.find((s) => s.id === entry?.id);
  const cost = currentMonth.services.find((s) => s.id === entry?.id);
  const team = catalog.teams.find((t) => t.id === entry?.team);

  const databases = useMemo(() => {
    if (!entry?.bindings) return [];
    const rows: { kind: string; binding: DbBinding }[] = [];
    for (const kind of ['postgres', 'mssql']) {
      const list = (entry.bindings as Record<string, DbBinding[] | undefined>)[kind] ?? [];
      for (const b of list) rows.push({ kind, binding: b });
    }
    return rows;
  }, [entry]);

  const envKeys = Object.keys(entry?.environments ?? {});
  const monthEmpty = Object.keys(currentMonth.totals.direct).length === 0 && Object.keys(currentMonth.totals.shared).length === 0;
  const viewCurrencies = Object.keys(currentMonth.totals.total);

  return (
    <section data-guide-anchor="service-inventory" className="space-y-3">
      <PrototypeBar note={`join rule: portal service name = catalog id`} />
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h2 className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>
          Service inventory
        </h2>
        {entry && (
          <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {entry.description}
          </span>
        )}
      </div>

      {!entry ? (
        <div
          className="rounded-xl border px-4 py-5 flex items-start gap-3"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <div className="w-9 h-9 rounded-lg flex items-center justify-center shrink-0" style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}>
            <BookDashed size={18} />
          </div>
          <div className="text-[13px]">
            <p className="font-medium" style={{ color: 'var(--text-primary)' }}>
              {service} is not in the service inventory yet
            </p>
            <p className="mt-0.5 max-w-[70ch]" style={{ color: 'var(--text-muted)' }}>
              No inventory entry has the id <span className="font-mono">{service.toLowerCase()}</span>, so the portal
              can't show its owner, resources or cost. Services are added by a pull request to the catalog repository.
            </p>
            <div className="mt-2">
              <Freshness feeds={feeds} only={['catalog']} />
            </div>
          </div>
        </div>
      ) : (
        <>
          <Freshness feeds={feeds} only={['catalog', 'status', 'cost']} />

          {(observed?.unverified || observed?.stale || (observed?.warnings.length ?? 0) > 0) && (
            <div
              className="rounded-lg border px-3 py-2 flex items-start gap-2 text-[12px]"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
            >
              <Info size={14} className="mt-0.5 shrink-0" style={{ color: 'var(--info)' }} />
              <span>
                {observed?.unverified
                  ? 'Only the central catalog entry is known: the service’s own file has never been readable, so its workloads and bindings are missing. '
                  : observed?.stale
                    ? 'The service’s own file could not be read this time; older details are shown. '
                    : ''}
                {observed?.warnings.map((w) => w.message).join(' ')}
              </span>
            </div>
          )}

          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            {/* ── Owner ── */}
            <SectionCard
              title="Owner"
              aside={
                <span className="text-[10px] font-semibold uppercase tracking-wide" style={{ color: LIFECYCLE_TONE[entry.lifecycle] ?? 'var(--text-muted)' }}>
                  {entry.lifecycle}
                </span>
              }
            >
              <dl className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1.5 text-[13px]">
                <dt style={{ color: 'var(--text-muted)' }}>Team</dt>
                <dd className="min-w-0">
                  <InventoryLink to={`/inventory/cost?team=${entry.team}`}>{entry.team}</InventoryLink>
                </dd>
                <dt style={{ color: 'var(--text-muted)' }}>Lead</dt>
                <dd><Person value={team?.lead} /></dd>
                <dt style={{ color: 'var(--text-muted)' }}>Deputy</dt>
                <dd><Person value={team?.deputy} /></dd>
                <dt style={{ color: 'var(--text-muted)' }}>Product</dt>
                <dd>
                  <InventoryLink to={`/inventory/cost?product=${entry.product}`}>{productName(entry.product)}</InventoryLink>
                  {entry.product !== product.toLowerCase() && (
                    <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      Deploys report it under {product}
                    </span>
                  )}
                </dd>
                {entry.repo && (
                  <>
                    <dt style={{ color: 'var(--text-muted)' }}>Repository</dt>
                    <dd className="min-w-0 truncate">
                      <RepoLink repo={entry.repo} />
                    </dd>
                  </>
                )}
                {(entry.type || entry.exposure) && (
                  <>
                    <dt style={{ color: 'var(--text-muted)' }}>Kind</dt>
                    <dd style={{ color: 'var(--text-secondary)' }}>
                      {[entry.type, entry.exposure].filter(Boolean).join(' · ')}
                    </dd>
                  </>
                )}
              </dl>
            </SectionCard>

            {/* ── Where it runs ── */}
            <SectionCard title="Runs in">
              {envKeys.length === 0 ? (
                <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>No environments declared.</p>
              ) : (
                <table className="w-full text-[12px]">
                  <tbody>
                    {envKeys.map((env) => (
                      <tr key={env}>
                        <td className="py-1 pr-2 align-top">
                          <EnvBadge env={resolveEnvKey(env, environments)} size="xs" />
                        </td>
                        <td className="py-1 font-mono break-all" style={{ color: 'var(--text-secondary)' }}>
                          {entry.environments[env].aks?.cluster}
                          <span style={{ color: 'var(--text-muted)' }}> / </span>
                          {entry.environments[env].aks?.namespace}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              {entry.workloads && (
                <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  Workloads:{' '}
                  {Object.entries(entry.workloads).map(([name, w], i) => (
                    <span key={name}>
                      {i > 0 && ', '}
                      <span className="font-mono" style={{ color: 'var(--text-secondary)' }}>{name}</span> ({w.kind})
                    </span>
                  ))}
                </p>
              )}
            </SectionCard>

            {/* ── Cost of the month ── */}
            <SectionCard
              title={`Cost · ${monthLabel(currentMonth.period)}`}
              aside={!currentMonth.complete && <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>to date</span>}
              className="md:col-span-2 xl:col-span-1"
            >
              {cost && (
                <div className="grid grid-cols-[1fr_auto] gap-y-1.5 text-[13px]">
                  <span className="inline-flex items-center gap-1.5" style={{ color: 'var(--text-secondary)' }}>
                    <Swatch color="var(--series-direct)" /> Direct
                  </span>
                  <MoneyLines money={cost.direct} zeroIn={viewCurrencies} className="font-medium" />
                  <span className="inline-flex items-center gap-1.5" style={{ color: 'var(--text-secondary)' }}>
                    <Swatch color="var(--series-shared)" /> Share of shared resources
                  </span>
                  <MoneyLines money={cost.shared} zeroIn={viewCurrencies} className="font-medium" />
                  <span className="pt-1.5 border-t font-semibold" style={{ borderColor: 'var(--border-color)' }}>Total</span>
                  <MoneyLines money={cost.total} zeroIn={viewCurrencies} className="pt-1.5 border-t font-semibold" />
                </div>
              )}
              {cost && cost.shared_from.length > 0 && (
                <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  Shared from {cost.shared_from.map((s) => s.shared).join(', ')}
                </p>
              )}
              {monthEmpty ? (
                <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                  Nothing this month is attributed to any service yet — the resources read so far carry no
                  service tag. <InventoryLink to="/inventory/coverage">See coverage</InventoryLink>
                </p>
              ) : (
                <InventoryLink to={`/inventory/cost?product=${entry.product}`}>Cost of {productName(entry.product)}</InventoryLink>
              )}
            </SectionCard>

            {/* ── Databases ── declared in the catalog, observed in Azure. */}
            <SectionCard title="Databases" className="md:col-span-2">
              {databases.length === 0 ? (
                <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                  {observed?.unverified ? 'Unknown until the service’s own file is readable.' : 'No databases declared.'}
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
                      {databases.map(({ kind, binding }) => {
                        const server = catalog.shared.find((s) => s.id === binding.server);
                        return (
                          <tr key={`${kind}:${binding.database}`} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                            <td className="px-1 py-1.5">
                              <span className="font-mono" style={{ color: 'var(--text-primary)' }}>{binding.database}</span>
                              <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>
                                {kind}{binding.owner ? ' · owner' : ''}
                              </span>
                            </td>
                            <td className="px-1 py-1.5">
                              <span className="font-mono" style={{ color: 'var(--text-secondary)' }}>{binding.server ?? 'default'}</span>
                              {server && (
                                <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>shared · {server.owner}</span>
                              )}
                            </td>
                            {envKeys.map((env) => {
                              const ob = status.azure?.bindings.find(
                                (b) => b.service === entry.id && b.environment === env && b.database === binding.database,
                              );
                              return (
                                <td key={env} className="px-1 py-1.5">
                                  <StateChip state={ob?.state ?? 'unknown'} detail={ob?.resource} />
                                </td>
                              );
                            })}
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
              {status.azure && (
                <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  Observed in Azure {status.azure.observed_at === status.generated_at ? 'in the latest scan' : `at ${status.azure.observed_at}`}
                </p>
              )}
            </SectionCard>

            {/* ── Resources ── tagged with this service, plus the shared ones it uses. */}
            <SectionCard title="Resources">
              {(() => {
                const tagged = status.azure?.services.find((s) => s.id === entry.id)?.resources ?? [];
                const sharedUsed = status.azure?.shared.filter((sh) => databases.some((d) => d.binding.server === sh.id)) ?? [];
                return (
                  <div className="space-y-2 text-[12px]">
                    {tagged.length === 0 ? (
                      <p style={{ color: 'var(--text-muted)' }}>
                        No Azure resource carries the tag <span className="font-mono">service={entry.id}</span> yet.
                      </p>
                    ) : (
                      tagged.map((r) => <ResourceRow key={r.id} id={r.id} type={r.type} />)
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
                );
              })()}
            </SectionCard>

            {/* ── Onboarding ── the steps from the status feed; unknown is not a failure. */}
            {observed && (
              <SectionCard title="Onboarding" className="md:col-span-2 xl:col-span-3">
                <ul className="grid gap-x-4 gap-y-1.5 sm:grid-cols-2 lg:grid-cols-4 text-[12px]">
                  {ONBOARDING_STEPS.map(([key, label]) => {
                    const step = observed.onboarding[key];
                    if (!step || typeof step === 'string') return null;
                    return (
                      <li key={key} className="flex items-center gap-2">
                        <span className="flex-1" style={{ color: 'var(--text-secondary)' }}>{label}</span>
                        <StateChip state={step.status} detail={step.detail} />
                      </li>
                    );
                  })}
                </ul>
              </SectionCard>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function Person({ value }: { value?: string }) {
  if (isPlaceholder(value)) {
    return (
      <span className="text-[12px] italic" style={{ color: 'var(--text-muted)' }} title="Not filled in the catalog yet">
        not set
      </span>
    );
  }
  return <span>{value}</span>;
}

function RepoLink({ repo }: { repo: string }) {
  const href = repo.startsWith('http') ? repo : `https://github.com/${repo}`;
  const label = repo.replace(/^https:\/\/(dev\.azure\.com|github\.com)\//, '');
  return (
    <a href={href} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 hover:underline" style={{ color: 'var(--accent)' }}>
      <span className="truncate">{label}</span>
      <ExternalLink size={11} className="shrink-0" />
    </a>
  );
}

function ResourceRow({ id, type }: { id: string; type?: string }) {
  const name = id.split('/').pop();
  return (
    <div title={id}>
      <span className="font-mono" style={{ color: 'var(--text-primary)' }}>{name}</span>
      {type && <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>{type}</span>}
    </div>
  );
}

export function Swatch({ color, hatched }: { color: string; hatched?: boolean }) {
  return (
    <span
      aria-hidden
      className="inline-block w-2.5 h-2.5 rounded-sm shrink-0"
      style={
        hatched
          ? { border: `1.5px solid ${color}`, background: `repeating-linear-gradient(135deg, ${color} 0 2px, transparent 2px 4px)` }
          : { backgroundColor: color }
      }
    />
  );
}
