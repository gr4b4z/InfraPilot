import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { Coins, Loader2, PackageOpen, SearchX, Users } from 'lucide-react';
import { api } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { ListEmptyState } from '@/components/ui/ListEmptyState';
import type { CostGroupRow, InventoryCostResponse, Money } from './inventoryTypes';
import { SERIES_DIRECT, SERIES_SHARED, currencies, isZero, monthLabel } from './inventoryFormat';
import { Freshness, InventoryLink, MoneyLines, MonthSelect, Swatch } from './InventoryUi';
import { MonthlyCostChart, type MonthBar } from './MonthlyCostChart';

/**
 * /inventory/cost — what a product's or team's services cost, as published by the cost allocation:
 * direct cost and the share of shared resources in separate lines, per currency; the services sorted
 * by cost; and the monthly trend with the month to date drawn as incomplete. Read-only.
 */
export function InventoryCostPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const product = params.get('product') ?? undefined;
  const team = params.get('team') ?? undefined;
  const period = params.get('month') ?? undefined;

  const [data, setData] = useState<InventoryCostResponse | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    api.getInventoryCost({ period, product, team })
      .then((d) => { if (!cancelled) { setData(d); setFailed(false); } })
      .catch(() => { if (!cancelled) { setData(null); setFailed(true); } });
    return () => { cancelled = true; };
  }, [period, product, team]);

  const scope = data?.scope ?? null;
  useDocumentTitle([scope?.name, 'Service cost']);

  const go = (next: { product?: string; team?: string; month?: string }) => {
    const p = new URLSearchParams();
    for (const [k, v] of Object.entries(next)) if (v) p.set(k, v);
    const q = p.toString();
    navigate(`/inventory/cost${q ? `?${q}` : ''}`);
  };

  if (failed) {
    return (
      <ListEmptyState icon={SearchX} title="Service cost could not be loaded" body="Try again in a moment. If it keeps failing, an administrator can check the feeds on the Cost coverage page." />
    );
  }
  if (!data) {
    return (
      <div className="flex items-center justify-center py-20">
        <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
      </div>
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-0">
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            {scope ? `Cost of ${scope.name}` : 'Service cost'}
          </h1>
          <p className="text-sm mt-0.5" style={{ color: 'var(--text-muted)' }}>
            {scope
              ? `${scope.kind === 'product' ? 'Product' : 'Team'} · cost of its services, as published by the cost allocation`
              : 'What each product’s and team’s services cost, as published by the cost allocation'}
          </p>
        </div>
        <span className="flex-1" />
        <label className="flex flex-col gap-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
          Product or team
          <select
            value={scope ? `${scope.kind}:${scope.id}` : ''}
            onChange={(e) => {
              const [kind, id] = e.target.value.split(/:(.*)/s);
              go({ month: period, ...(id ? { [kind]: id } : {}) });
            }}
            className="rounded-lg border px-2.5 py-1.5 text-[13px] min-w-[200px]"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
          >
            <option value="">All products and teams</option>
            <optgroup label="Products">
              {data.options.products.map((p) => <option key={p.id} value={`product:${p.id}`}>{p.name}</option>)}
            </optgroup>
            <optgroup label="Teams">
              {data.options.teams.map((t) => <option key={t} value={`team:${t}`}>{t}</option>)}
            </optgroup>
          </select>
        </label>
        {data.period && (
          <MonthSelect months={data.months} value={data.period} onChange={(m) => go({ product, team, month: m })} />
        )}
      </div>

      <Freshness feeds={[data.freshness]} />

      {!data.period ? (
        <ListEmptyState
          icon={Coins}
          title="No cost has been read yet"
          body="The portal shows cost once the first cost file has been fetched. An administrator can check the feeds on the Cost coverage page."
        />
      ) : (
        <>
          <MonthNotice data={data} />
          {scope ? <ScopeView data={data} /> : <IndexView data={data} month={period} />}
        </>
      )}
    </div>
  );
}

/** Says what the month is, and — today's usual situation — that nothing is attributed yet. */
function MonthNotice({ data }: { data: InventoryCostResponse }) {
  const attributed = !isZero(data.month?.direct) || !isZero(data.month?.shared);
  return (
    <div className="space-y-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
      <p>
        {monthLabel(data.period!)}
        {data.complete ? ' · closed month' : ' · to date, updated daily until the month closes'}
        {data.catalogVersion != null && ` · attributed against catalog v${data.catalogVersion}`}
      </p>
      {!attributed && (
        <div
          className="rounded-lg border px-3 py-2"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
        >
          <b style={{ color: 'var(--text-primary)' }}>No cost is attributed to any service in {monthLabel(data.period!)} yet.</b>{' '}
          Everything read so far (<MoneyLines money={data.month?.total} className="!inline-flex !flex-row gap-2 font-medium" />)
          is on resources without a service tag. <InventoryLink to="/inventory/coverage">See what is not attributed</InventoryLink>
        </div>
      )}
    </div>
  );
}

function MoneyTile({ label, money, color }: { label: string; money: Money | undefined; color?: string }) {
  return (
    <div className="p-3.5 rounded-xl border flex flex-col gap-1" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <span className="text-[11px] inline-flex items-center gap-1.5" style={{ color: 'var(--text-muted)' }}>
        {color && <Swatch color={color} />}
        {label}
      </span>
      <MoneyLines money={money} className="!items-start text-lg font-semibold leading-tight" />
    </div>
  );
}

function ScopeView({ data }: { data: InventoryCostResponse }) {
  const scope = data.scope!;
  const services = data.services ?? [];

  const charts = useMemo(() => {
    const trend = data.trend ?? [];
    return currencies(...trend.flatMap((t) => [t.direct, t.shared])).map((currency) => ({
      currency,
      bars: trend.map<MonthBar>((t) => ({
        period: t.period,
        complete: t.complete,
        direct: t.direct[currency] ?? 0,
        shared: t.shared[currency] ?? 0,
      })),
    }));
  }, [data.trend]);

  if (services.length === 0) {
    return (
      <ListEmptyState
        icon={scope.kind === 'team' ? Users : PackageOpen}
        title={
          scope.known
            ? `${scope.kind === 'team' ? 'This team owns' : 'This product has'} no services in the inventory`
            : `The inventory has no ${scope.kind} called ${scope.id}`
        }
        body={
          scope.known
            ? 'Cost is attributed to services, so there is nothing to show until a service names it in the catalog.'
            : 'Pick a product or team from the list above.'
        }
      />
    );
  }

  return (
    <>
      <div className="grid gap-3 grid-cols-1 sm:grid-cols-3">
        <MoneyTile label="Direct" money={data.totals?.direct} color={SERIES_DIRECT} />
        <MoneyTile label="Share of shared resources" money={data.totals?.shared} color={SERIES_SHARED} />
        <MoneyTile label="Total" money={data.totals?.total} />
      </div>

      <section>
        <h2 className="text-sm font-semibold mb-2" style={{ color: 'var(--text-primary)' }}>
          Services by cost · {monthLabel(data.period!)}
        </h2>
        <div className="rounded-xl border overflow-x-auto" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
          <table className="w-full text-[13px] min-w-[560px]">
            <thead>
              <tr className="text-left text-[11px] uppercase tracking-wide" style={{ color: 'var(--text-muted)' }}>
                <th className="font-medium px-3 py-2">Service</th>
                <th className="font-medium px-3 py-2">{scope.kind === 'product' ? 'Team' : 'Product'}</th>
                <th className="font-medium px-3 py-2 text-right">Direct</th>
                <th className="font-medium px-3 py-2 text-right">Shared</th>
                <th className="font-medium px-3 py-2 text-right">Total</th>
              </tr>
            </thead>
            <tbody>
              {services.map((s) => (
                <tr key={s.id} className="border-t align-top" style={{ borderColor: 'var(--border-color)' }}>
                  <td className="px-3 py-2">
                    {/* The catalog id is the portal's service name; the catalog's product is where its page lives. */}
                    <Link
                      to={`/deployments/${encodeURIComponent(s.product)}/${encodeURIComponent(s.id)}`}
                      className="font-medium hover:underline"
                      style={{ color: 'var(--text-primary)' }}
                    >
                      {s.id}
                    </Link>
                    {isZero(s.total) && (
                      <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>no cost attributed this month</span>
                    )}
                    {s.sharedFrom.length > 0 && (
                      <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>shares {s.sharedFrom.join(', ')}</span>
                    )}
                  </td>
                  <td className="px-3 py-2" style={{ color: 'var(--text-secondary)' }}>
                    {scope.kind === 'product' ? s.team : s.productName}
                  </td>
                  <td className="px-3 py-2 text-right"><MoneyLines money={s.direct} /></td>
                  <td className="px-3 py-2 text-right"><MoneyLines money={s.shared} /></td>
                  <td className="px-3 py-2 text-right font-semibold"><MoneyLines money={s.total} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <div className={`grid gap-3 ${charts.length > 1 ? 'lg:grid-cols-2' : ''}`}>
        {charts.map((c) => (
          <MonthlyCostChart key={c.currency} title={`Per month · ${c.currency}`} currency={c.currency} bars={c.bars} />
        ))}
      </div>
    </>
  );
}

function IndexView({ data, month }: { data: InventoryCostResponse; month?: string }) {
  const suffix = month ? `&month=${encodeURIComponent(month)}` : '';
  const table = (kind: 'product' | 'team', rows: CostGroupRow[]) => (
    <section>
      <h2 className="text-sm font-semibold mb-2 inline-flex items-center gap-1.5" style={{ color: 'var(--text-primary)' }}>
        {kind === 'product' ? <Coins size={14} /> : <Users size={14} />}
        {kind === 'product' ? 'Products' : 'Teams'}
      </h2>
      <div className="rounded-xl border overflow-x-auto" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
        <table className="w-full text-[13px]">
          <thead>
            <tr className="text-left text-[11px] uppercase tracking-wide" style={{ color: 'var(--text-muted)' }}>
              <th className="font-medium px-3 py-2">{kind === 'product' ? 'Product' : 'Team'}</th>
              <th className="font-medium px-3 py-2 text-right">Direct</th>
              <th className="font-medium px-3 py-2 text-right">Shared</th>
              <th className="font-medium px-3 py-2 text-right">Total</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} className="border-t align-top" style={{ borderColor: 'var(--border-color)' }}>
                <td className="px-3 py-2">
                  <InventoryLink to={`/inventory/cost?${kind}=${encodeURIComponent(r.id)}${suffix}`}>{r.name}</InventoryLink>
                  <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>
                    {r.services === 0 ? 'no services yet' : `${r.services} service${r.services === 1 ? '' : 's'}`}
                  </span>
                </td>
                <td className="px-3 py-2 text-right"><MoneyLines money={r.direct} /></td>
                <td className="px-3 py-2 text-right"><MoneyLines money={r.shared} /></td>
                <td className="px-3 py-2 text-right font-semibold"><MoneyLines money={r.total} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      {table('product', data.products ?? [])}
      {table('team', data.teams ?? [])}
    </div>
  );
}
