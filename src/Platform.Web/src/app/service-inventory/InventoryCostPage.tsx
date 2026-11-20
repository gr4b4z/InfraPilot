/**
 * PROTOTYPE — /inventory/cost: what a product's or team's services cost. Totals in separate lines
 * (direct, share of shared resources, total) per currency, services sorted by cost, the monthly
 * trend with the month to date drawn as incomplete.
 */
import { useMemo } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { Coins, PackageOpen, Users } from 'lucide-react';
import { useDocumentTitle } from '@/lib/pageTitle';
import { ListEmptyState } from '@/components/ui/ListEmptyState';
import {
  addMoney,
  catalog,
  costMonths,
  currencies,
  feedStates,
  monthLabel,
  primaryAmount,
  productName,
  type CostMonth,
  type CostService,
  type Money,
} from './inventoryData';
import { Freshness, InventoryLink, MoneyLines, PrototypeBar, SampleTag } from './InventoryUi';
import { useScenario } from './inventoryHooks';
import { MonthlyCostChart, type MonthBar } from './MonthlyCostChart';
import { Swatch } from './ServiceInventorySection';

type Scope = { kind: 'product' | 'team'; id: string };

function inScope(s: CostService, scope: Scope) {
  return scope.kind === 'product' ? s.product === scope.id : s.team === scope.id;
}

function sumScope(month: CostMonth, scope: Scope) {
  let direct: Money = {};
  let shared: Money = {};
  let total: Money = {};
  for (const s of month.services.filter((x) => inScope(x, scope))) {
    direct = addMoney(direct, s.direct);
    shared = addMoney(shared, s.shared);
    total = addMoney(total, s.total);
  }
  return { direct, shared, total };
}

export function InventoryCostPage() {
  const scenario = useScenario();
  const feeds = feedStates(scenario);
  const { search } = useLocation();
  const navigate = useNavigate();
  const params = new URLSearchParams(search);
  const scope: Scope | null = params.get('product')
    ? { kind: 'product', id: params.get('product')! }
    : params.get('team')
      ? { kind: 'team', id: params.get('team')! }
      : null;
  const period = params.get('month') ?? costMonths[costMonths.length - 1].period;
  const month = costMonths.find((m) => m.period === period) ?? costMonths[costMonths.length - 1];
  const scopeName = scope ? (scope.kind === 'product' ? productName(scope.id) : scope.id) : null;

  useDocumentTitle([scopeName, 'Service cost']);

  const setParam = (key: string, value: string | null) => {
    const p = new URLSearchParams(search);
    if (value === null) p.delete(key);
    else p.set(key, value);
    if (key === 'product') p.delete('team');
    if (key === 'team') p.delete('product');
    navigate(`/inventory/cost?${p.toString()}`);
  };

  return (
    <div className="space-y-5">
      <PrototypeBar note="3 earlier months are sample data" />
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-0">
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            {scopeName ? `Cost of ${scopeName}` : 'Service cost'}
          </h1>
          <p className="text-sm mt-0.5" style={{ color: 'var(--text-muted)' }}>
            {scope
              ? `${scope.kind === 'product' ? 'Product' : 'Team'} · cost of its services as published by the cost allocation`
              : 'What each product’s and team’s services cost, as published by the cost allocation'}
          </p>
        </div>
        <span className="flex-1" />
        <label className="flex flex-col gap-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
          Product or team
          <select
            value={scope ? `${scope.kind}:${scope.id}` : ''}
            onChange={(e) => {
              const [k, v] = e.target.value.split(':');
              if (!v) navigate('/inventory/cost' + (params.get('inv') ? `?inv=${params.get('inv')}` : ''));
              else setParam(k, v);
            }}
            className="rounded-lg border px-2.5 py-1.5 text-[13px] min-w-[200px]"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
          >
            <option value="">All products and teams</option>
            <optgroup label="Products">
              {catalog.products.map((p) => <option key={p.id} value={`product:${p.id}`}>{p.name}</option>)}
            </optgroup>
            <optgroup label="Teams">
              {catalog.teams.map((t) => <option key={t.id} value={`team:${t.id}`}>{t.id}</option>)}
            </optgroup>
          </select>
        </label>
        <label className="flex flex-col gap-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
          Month
          <select
            value={month.period}
            onChange={(e) => setParam('month', e.target.value)}
            className="rounded-lg border px-2.5 py-1.5 text-[13px]"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
          >
            {[...costMonths].reverse().map((m) => (
              <option key={m.period} value={m.period}>
                {monthLabel(m.period)}{m.complete ? '' : ' (to date)'}{m._sample ? ' — sample' : ''}
              </option>
            ))}
          </select>
        </label>
      </div>

      <Freshness feeds={feeds} only={['cost', 'catalog']} />
      <MonthNotice month={month} />

      {scope ? <ScopeView scope={scope} month={month} /> : <IndexView month={month} />}
    </div>
  );
}

/** "Everything is unallocated" is today's real situation — say so before showing zeros. */
function MonthNotice({ month }: { month: CostMonth }) {
  const attributed = currencies(month.totals.direct, month.totals.shared).length > 0;
  return (
    <div className="flex flex-wrap items-center gap-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
      <span>
        {monthLabel(month.period)}
        {month.complete ? ' · closed month' : ` · to date, updated daily until the month closes`}
        {' · '}attributed against catalog v{month.catalog_version}
      </span>
      {month._sample && <SampleTag />}
      {!attributed && (
        <div
          className="basis-full rounded-lg border px-3 py-2 text-[12px]"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
        >
          <b style={{ color: 'var(--text-primary)' }}>No cost is attributed to any service in {monthLabel(month.period)} yet.</b>{' '}
          All of the <MoneyLines money={month.totals.total} className="!inline font-medium" /> read so far is on resources without a
          service tag. <InventoryLink to="/inventory/coverage">See what is not attributed</InventoryLink>
        </div>
      )}
    </div>
  );
}

function MoneyTile({ label, money, color, zeroIn, hatched }: { label: string; money: Money; color?: string; zeroIn: string[]; hatched?: boolean }) {
  return (
    <div className="p-3.5 rounded-xl border flex flex-col gap-1" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <span className="text-[11px] inline-flex items-center gap-1.5" style={{ color: 'var(--text-muted)' }}>
        {color && <Swatch color={color} hatched={hatched} />}
        {label}
      </span>
      <MoneyLines money={money} zeroIn={zeroIn} className="!items-start text-lg font-semibold leading-tight" />
    </div>
  );
}

function ScopeView({ scope, month }: { scope: Scope; month: CostMonth }) {
  const services = month.services.filter((s) => inScope(s, scope));
  const sums = sumScope(month, scope);
  const viewCurrencies = currencies(month.totals.total);
  const sorted = [...services].sort((a, b) => primaryAmount(b.total) - primaryAmount(a.total) || a.id.localeCompare(b.id));

  const trend = useMemo(() => {
    const curs = currencies(...costMonths.map((m) => m.totals.total));
    return curs.map((cur) => ({
      cur,
      bars: costMonths.map<MonthBar>((m) => {
        const s = sumScope(m, scope);
        return { period: m.period, complete: m.complete, sample: !!m._sample, direct: s.direct[cur] ?? 0, shared: s.shared[cur] ?? 0 };
      }),
    }));
  }, [scope]);

  if (services.length === 0) {
    return (
      <ListEmptyState
        icon={scope.kind === 'team' ? Users : PackageOpen}
        title={`${scope.kind === 'team' ? 'This team owns' : 'This product has'} no services in the inventory`}
        body="Cost is attributed to services, so there is nothing to show until a service names it in the catalog."
      />
    );
  }

  return (
    <>
      <div className="grid gap-3 grid-cols-1 sm:grid-cols-3">
        <MoneyTile label="Direct" money={sums.direct} color="var(--series-direct)" zeroIn={viewCurrencies} />
        <MoneyTile label="Share of shared resources" money={sums.shared} color="var(--series-shared)" zeroIn={viewCurrencies} />
        <MoneyTile label="Total" money={sums.total} zeroIn={viewCurrencies} />
      </div>

      <section>
        <h2 className="text-sm font-semibold mb-2" style={{ color: 'var(--text-primary)' }}>
          Services by cost · {monthLabel(month.period)}
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
              {sorted.map((s) => {
                const none = currencies(s.total).length === 0;
                return (
                  <tr key={s.id} className="border-t align-top" style={{ borderColor: 'var(--border-color)' }}>
                    <td className="px-3 py-2">
                      <Link to={`/deployments/${s.product}/${s.id}`} className="font-medium hover:underline" style={{ color: 'var(--text-primary)' }}>
                        {s.id}
                      </Link>
                      {none && <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>no cost attributed this month</span>}
                      {s.shared_from.length > 0 && (
                        <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>
                          shares {s.shared_from.map((x) => x.shared).join(', ')}
                        </span>
                      )}
                    </td>
                    <td className="px-3 py-2" style={{ color: 'var(--text-secondary)' }}>
                      {scope.kind === 'product' ? s.team : productName(s.product)}
                    </td>
                    <td className="px-3 py-2 text-right"><MoneyLines money={s.direct} zeroIn={none ? [] : viewCurrencies} /></td>
                    <td className="px-3 py-2 text-right"><MoneyLines money={s.shared} zeroIn={none ? [] : viewCurrencies} /></td>
                    <td className="px-3 py-2 text-right font-semibold"><MoneyLines money={s.total} zeroIn={none ? [] : viewCurrencies} /></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      <div className={`grid gap-3 ${trend.length > 1 ? 'lg:grid-cols-2' : ''}`}>
        {trend.map((t) => (
          <MonthlyCostChart key={t.cur} title={`Per month · ${t.cur}`} currency={t.cur} bars={t.bars} />
        ))}
      </div>
    </>
  );
}

function IndexView({ month }: { month: CostMonth }) {
  const viewCurrencies = currencies(month.totals.total);
  const rows = (kind: 'product' | 'team') =>
    (kind === 'product' ? catalog.products.map((p) => p.id) : catalog.teams.map((t) => t.id))
      .map((id) => {
        const scope = { kind, id } as Scope;
        return { id, count: month.services.filter((s) => inScope(s, scope)).length, ...sumScope(month, scope) };
      })
      .sort((a, b) => primaryAmount(b.total) - primaryAmount(a.total) || b.count - a.count || a.id.localeCompare(b.id));

  return (
    <div className="grid gap-4 lg:grid-cols-2">
      {(['product', 'team'] as const).map((kind) => (
        <section key={kind}>
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
                {rows(kind).map((r) => (
                  <tr key={r.id} className="border-t align-top" style={{ borderColor: 'var(--border-color)' }}>
                    <td className="px-3 py-2">
                      <InventoryLink to={`/inventory/cost?${kind}=${r.id}`}>{kind === 'product' ? productName(r.id) : r.id}</InventoryLink>
                      <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>
                        {r.count === 0 ? 'no services yet' : `${r.count} service${r.count === 1 ? '' : 's'}`}
                      </span>
                    </td>
                    <td className="px-3 py-2 text-right"><MoneyLines money={r.direct} zeroIn={r.count ? viewCurrencies : []} /></td>
                    <td className="px-3 py-2 text-right"><MoneyLines money={r.shared} zeroIn={r.count ? viewCurrencies : []} /></td>
                    <td className="px-3 py-2 text-right font-semibold"><MoneyLines money={r.total} zeroIn={r.count ? viewCurrencies : []} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ))}
    </div>
  );
}
