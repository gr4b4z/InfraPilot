import { useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { ArrowDown, ArrowUp, ArrowUpDown, Workflow } from 'lucide-react';
import type {
  Release,
  ReleaseCheck,
  ReleaseDeploy,
  ReleaseMigration,
  ReleaseProgress,
  ReleasePromotion,
  ReleaseRunRef,
  ReleaseService,
  ReleaseWorkItems,
} from '@/lib/api';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { migrationPath, migrationStatusStyle } from '@/components/migrations/migrationStatus';

const fmt = (iso: string | null | undefined) =>
  iso
    ? new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
    : null;

const muted = { color: 'var(--text-muted)' };
// The column headers stay put while a long table scrolls under them.
const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap sticky top-0 z-[1]';
const thStyle = { backgroundColor: 'var(--bg-secondary)', boxShadow: 'inset 0 -1px 0 var(--border-color)' };
const td = 'px-2 py-1.5 whitespace-nowrap align-top';

/** What a row sorts by in a column: a value, or several compared in turn. Blanks sort last either way. */
type SortValue = string | number | null | undefined;
type SortKey = SortValue | SortValue[];

interface Column {
  label: ReactNode;
  cell: (s: ReleaseService) => ReactNode;
  sort: (s: ReleaseService) => SortKey;
  wrap?: boolean;
}

/** Versions compare by their numbers (5.11.4 before 5.11.10); text ignores case. */
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

function compareKeys(a: SortKey, b: SortKey, dir: 1 | -1): number {
  const xs = Array.isArray(a) ? a : [a];
  const ys = Array.isArray(b) ? b : [b];
  for (let i = 0; i < Math.max(xs.length, ys.length); i++) {
    const x = xs[i];
    const y = ys[i];
    if (x == null || y == null) {
      if (x == null && y == null) continue;
      return x == null ? 1 : -1;
    }
    const c = typeof x === 'number' && typeof y === 'number' ? x - y : collator.compare(String(x), String(y));
    if (c !== 0) return c * dir;
  }
  return 0;
}

// Status columns sort the least-finished first, so ascending reads as "what's left".
const rank = (order: string[], value: string) => {
  const i = order.indexOf(value);
  return i < 0 ? order.length : i;
};
const PROMOTION_ORDER = ['Pending', 'Approved', 'Deploying', 'Deployed', 'Rejected', 'Superseded'];
const QA_ORDER = ['pending', 'rejected', 'approved', 'not-required'];
const MIGRATION_ORDER = ['failed', 'running', 'pending', 'succeeded', 'skipped', 'superseded'];

// A deploy that isn't a success or still running (failed, cancelled, rolled back) is the one to look at.
const deployKey = (d: ReleaseDeploy | null): SortKey =>
  d ? [d.status === 'succeeded' ? 2 : d.status === 'in_progress' ? 1 : 0, d.version] : null;
const promotionKey = (p: ReleasePromotion | null): SortKey => (p ? [rank(PROMOTION_ORDER, p.status), p.version] : null);
const workItemsKey = (w: ReleaseWorkItems | null): SortKey =>
  w && w.total > 0 ? [(w.approved + w.issues) / w.total, -w.blocked, w.total] : null;
const migrationKey = (m: ReleaseMigration | undefined): SortKey => (m ? [rank(MIGRATION_ORDER, m.status), m.appliedCount] : null);
function approvalKey(p: ReleasePromotion | null): SortKey {
  if (!p) return null;
  if (p.status === 'Approved' || p.status === 'Deploying' || p.status === 'Deployed') return [4, p.approvedAt];
  if (p.status === 'Rejected') return [0, p.version];
  if (p.workItemsOutstanding) return [1, p.version];
  return [p.outstandingSteps.length > 0 ? 2 : 3, p.outstandingSteps.join(', ')];
}

/**
 * The services behind one step's check, one row each: the build, staging deploy and promotion for
 * Build and Staging; the promotion, its work items and the QA gate for Test on staging; the
 * promotion and its gates for the approval round; the production deploy; the migrations. Every
 * shipping service is listed; the ones the check still waits for say why, and a toggle narrows the
 * table to them. A long table scrolls inside its own frame, so the steps after it stay in reach.
 * Clicking a column header sorts by it, then reverses, then goes back to the portal's order.
 */
export function StepTable({ check, progress, release }: { check: ReleaseCheck; progress: ReleaseProgress; release: Release }) {
  const shipping = progress.services.filter((s) => !s.excluded);
  const problems = new Map(check.issues.map((i) => [`${i.product}/${i.service}`, i.problem]));
  const problemOf = (s: ReleaseService) => problems.get(`${s.product}/${s.service}`);
  const open = shipping.filter((s) => problemOf(s) !== undefined);
  const [onlyOpen, setOnlyOpen] = useState(false);
  const [sort, setSort] = useState<{ col: number; dir: 1 | -1 } | null>(null);
  const checkColumns = columnsFor(check.key, progress, release);
  if (shipping.length === 0 || checkColumns.length === 0) return null;

  const columns: Column[] = [
    {
      label: 'Service',
      cell: (s) => (
        <Link to={`/deployments/${encodeURIComponent(s.product)}/${encodeURIComponent(s.service)}`} className="hover:underline">
          {release.products.length > 1 && <span style={muted}>{s.product} · </span>}{s.service}
        </Link>
      ),
      sort: (s) => (release.products.length > 1 ? [s.product, s.service] : s.service),
    },
    ...checkColumns,
    ...(open.length > 0
      ? [{
        label: 'Open because',
        cell: (s: ReleaseService) => {
          const problem = problemOf(s);
          return <span style={{ color: problem ? 'var(--text-secondary)' : 'var(--success)' }}>{problem ?? 'Done'}</span>;
        },
        sort: (s: ReleaseService): SortKey => {
          const problem = problemOf(s);
          return [problem ? 0 : 1, problem];
        },
        wrap: true,
      }]
      : []),
  ];

  const listed = onlyOpen && open.length > 0 ? open : shipping;
  const by = sort && columns[sort.col] ? { column: columns[sort.col], dir: sort.dir } : null;
  // Array sort is stable, so rows that tie keep the portal's order.
  const rows = by ? [...listed].sort((x, y) => compareKeys(by.column.sort(x), by.column.sort(y), by.dir)) : listed;
  const toggleSort = (col: number) =>
    setSort((prev) => (prev?.col !== col ? { col, dir: 1 } : prev.dir === 1 ? { col, dir: -1 } : null));

  return (
    <div className="mt-2">
      <div className="flex items-center justify-between gap-2 text-[12px]">
        <span style={muted}>
          {open.length > 0
            ? `${open.length} of ${shipping.length} ${shipping.length === 1 ? 'service' : 'services'} still open`
            : `All ${shipping.length} ${shipping.length === 1 ? 'service' : 'services'} done`}
        </span>
        {open.length > 0 && (
          <button className="hover:underline" style={{ color: 'var(--accent)' }} onClick={() => setOnlyOpen((v) => !v)}>
            {onlyOpen ? `Show all ${shipping.length}` : `Only the ${open.length} open`}
          </button>
        )}
      </div>
      {rows.length > 0 && (
        <div className="overflow-auto max-h-[min(400px,60vh)] mt-1 rounded-lg border" style={{ borderColor: 'var(--border-color)' }}
          data-step-table={check.key}>
          <table className="w-full text-[12px]">
            <thead style={muted}>
              <tr>
                {columns.map((c, i) => {
                  const active = by && sort?.col === i;
                  const Arrow = active ? (sort.dir === 1 ? ArrowUp : ArrowDown) : ArrowUpDown;
                  return (
                    <th key={i} className={th} style={thStyle}
                      aria-sort={active ? (sort.dir === 1 ? 'ascending' : 'descending') : undefined}>
                      <button
                        type="button"
                        onClick={() => toggleSort(i)}
                        className="group inline-flex items-center gap-1 font-medium hover:text-[var(--text-primary)]"
                        style={active ? { color: 'var(--text-primary)' } : undefined}
                        title={!active ? 'Sort' : sort.dir === 1 ? 'Sort the other way' : 'Back to the default order'}
                      >
                        {c.label}
                        <Arrow size={11} className={active ? undefined : 'opacity-0 group-hover:opacity-60 group-focus-visible:opacity-60'} />
                      </button>
                    </th>
                  );
                })}
              </tr>
            </thead>
            <tbody>
              {rows.map((s) => (
                <tr key={`${s.product}/${s.service}`} className="border-t first:border-t-0" style={{ borderColor: 'var(--border-color)' }}>
                  {columns.map((c, i) => <td key={i} className={c.wrap ? `${td} whitespace-normal` : td}>{c.cell(s)}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function columnsFor(key: ReleaseCheck['key'], progress: ReleaseProgress, release: Release): Column[] {
  const promotion: Column = {
    label: <>Promotion to <EnvLabel env={release.productionEnvironment} /></>,
    cell: (s) => <PromotionCell p={s.promotion} />,
    sort: (s) => promotionKey(s.promotion),
  };
  const workItems: Column = {
    label: 'Work items',
    cell: (s) => <WorkItemsCell w={s.promotion?.workItems ?? null} />,
    sort: (s) => workItemsKey(s.promotion?.workItems ?? null),
  };
  switch (key) {
    case 'staging-builds':
      return [
        { label: 'Build', cell: (s) => <BuildCell s={s} />, sort: (s) => s.build?.version },
        {
          label: <>Deploy to <EnvLabel env={release.stagingEnvironment} /></>,
          cell: (s) => <DeployCell d={s.staging} />,
          sort: (s) => deployKey(s.staging),
        },
        promotion,
      ];
    case 'staging-qa':
      return [
        promotion,
        workItems,
        {
          label: progress.qaStep,
          cell: (s) => <QaCell p={s.promotion} />,
          sort: (s) => (s.promotion ? [rank(QA_ORDER, s.promotion.qaReview), s.promotion.qaApprovedAt] : null),
        },
      ];
    case 'production-promotions':
      return [
        promotion,
        workItems,
        { label: 'Approval', cell: (s) => <ApprovalCell p={s.promotion} />, sort: (s) => approvalKey(s.promotion) },
      ];
    case 'production-deploys':
      return [
        { ...promotion, label: 'Approved version' },
        {
          label: <>Deployment to <EnvLabel env={release.productionEnvironment} /></>,
          cell: (s) => <DeployCell d={s.production} />,
          sort: (s) => deployKey(s.production),
        },
        {
          label: 'Failed attempts',
          cell: (s) => s.productionFailures > 0
            ? <span style={{ color: 'var(--danger)' }}>{s.productionFailures}</span>
            : <span style={muted}>—</span>,
          sort: (s) => s.productionFailures,
        },
      ];
    case 'production-migrations':
      return [
        {
          label: <><EnvLabel env={release.productionEnvironment} /> runs</>,
          cell: (s) => <DeployCell d={s.production} />,
          sort: (s) => deployKey(s.production),
        },
        {
          label: 'Schema',
          cell: (s) => <MigrationCell s={s} kind="schema" />,
          sort: (s) => migrationKey(s.migrations.find((m) => m.kind === 'schema')),
        },
        {
          label: 'Data',
          cell: (s) => <MigrationCell s={s} kind="data" />,
          sort: (s) => migrationKey(s.migrations.find((m) => m.kind === 'data')),
        },
      ];
    default:
      return [];
  }
}

function RunIcon({ run }: { run: ReleaseRunRef | null }) {
  if (!run) return null;
  return (
    <a href={run.url} target="_blank" rel="noopener noreferrer" title={`Open ${run.label}`} aria-label={`Open ${run.label}`}
      className="hover:opacity-70 ml-1 inline-flex align-middle" style={muted}>
      <Workflow size={12} />
    </a>
  );
}

function BuildCell({ s }: { s: ReleaseService }) {
  const b = s.build;
  if (!b) return <span style={muted}>—</span>;
  const branch = b.branch.replace(/^refs\/heads\//, '');
  const label = <><span className="font-mono">{b.version}</span> <span className="text-[11px]" style={muted}>{branch}</span></>;
  const title = `Built ${new Date(b.createdAt).toLocaleString()}${b.buildId ? ` · build ${b.buildId}` : ''}`;
  return b.buildUrl
    ? <a href={b.buildUrl} target="_blank" rel="noopener noreferrer" className="hover:underline" title={title}>{label}</a>
    : <span title={title}>{label}</span>;
}

function DeployCell({ d }: { d: ReleaseDeploy | null }) {
  if (!d) return <span style={muted}>—</span>;
  const ok = d.status === 'succeeded';
  return (
    <span className="inline-flex items-center">
      <Link to={`/deployments/events/${d.eventId}`} className="hover:underline" title={`Deployed ${new Date(d.deployedAt).toLocaleString()}`}>
        <span className="font-mono">{d.version}</span>
        {!ok && (
          <span className="text-[11px]" style={{ color: d.status === 'failed' ? 'var(--danger)' : 'var(--info)' }}>
            {' '}{d.status.replace('_', ' ')}
          </span>
        )}
      </Link>
      <RunIcon run={d.run} />
    </span>
  );
}

function promotionColor(status: string): string {
  switch (status) {
    case 'Deployed': return 'var(--success)';
    case 'Approved':
    case 'Deploying': return 'var(--info)';
    case 'Rejected': return 'var(--danger)';
    case 'Pending': return 'var(--warning)';
    default: return 'var(--text-muted)';
  }
}

function PromotionCell({ p }: { p: ReleasePromotion | null }) {
  if (!p) return <span style={muted}>not raised</span>;
  return (
    <Link to={`/promotions/${p.id}`} className="hover:underline" title={`Raised ${new Date(p.createdAt).toLocaleString()}`}>
      <span className="font-mono">{p.version}</span>{' '}
      <span className="text-[11px]" style={{ color: promotionColor(p.status) }}>{p.status}</span>
    </Link>
  );
}

function WorkItemsCell({ w }: { w: ReleaseWorkItems | null }) {
  if (!w || w.total === 0) return <span style={muted}>none</span>;
  const cleared = w.approved + w.issues;
  const pct = Math.round((cleared / w.total) * 100);
  return (
    <span className="inline-flex items-center gap-2" title={`${w.approved} approved, ${w.issues} with an issue, ${w.blocked} blocked, ${w.total - cleared - w.blocked} not ruled on`}>
      <span className="inline-block w-16 h-1.5 rounded-full overflow-hidden" style={{ backgroundColor: 'var(--bg-secondary)' }}>
        <span className="block h-full" style={{ width: `${pct}%`, backgroundColor: cleared === w.total ? 'var(--success)' : 'var(--accent)' }} />
      </span>
      <span>{cleared} of {w.total}</span>
      {w.issues > 0 && <span className="text-[11px]" style={{ color: 'var(--warning)' }}>{w.issues} issue{w.issues === 1 ? '' : 's'}</span>}
      {w.blocked > 0 && <span className="text-[11px]" style={{ color: 'var(--danger)' }}>{w.blocked} blocked</span>}
    </span>
  );
}

function QaCell({ p }: { p: ReleasePromotion | null }) {
  if (!p) return <span style={muted}>—</span>;
  switch (p.qaReview) {
    case 'approved':
      return (
        <span style={{ color: 'var(--success)' }}>
          Approved{p.qaApprovedAt && <span style={muted}> {fmt(p.qaApprovedAt)}</span>}{p.qaApprovedBy && <span style={muted}> · {p.qaApprovedBy}</span>}
        </span>
      );
    case 'not-required':
      return <span style={muted}>Not in this gate</span>;
    case 'rejected':
      return <span style={{ color: 'var(--danger)' }}>Rejected</span>;
    default:
      return <span style={{ color: 'var(--warning)' }}>{p.workItemsOutstanding ? 'Waiting for work items' : 'Waiting'}</span>;
  }
}

function ApprovalCell({ p }: { p: ReleasePromotion | null }) {
  if (!p) return <span style={muted}>—</span>;
  if (p.status === 'Approved' || p.status === 'Deploying' || p.status === 'Deployed')
    return <span style={{ color: 'var(--success)' }}>Approved{p.approvedAt && <span style={muted}> {fmt(p.approvedAt)}</span>}</span>;
  if (p.status === 'Rejected') return <span style={{ color: 'var(--danger)' }}>Rejected</span>;
  if (p.workItemsOutstanding) return <span style={{ color: 'var(--warning)' }}>Held by its work items</span>;
  if (p.outstandingSteps.length > 0) return <span style={{ color: 'var(--warning)' }}>Waiting for {p.outstandingSteps.join(', ')}</span>;
  return <span style={muted}>{p.status}</span>;
}

function MigrationCell({ s, kind }: { s: ReleaseService; kind: 'schema' | 'data' }) {
  const m = s.migrations.find((x) => x.kind === kind);
  if (!m) return <span style={muted}>{s.production?.status === 'succeeded' ? 'none reported' : '—'}</span>;
  const st = migrationStatusStyle(m.status);
  return (
    <span className="inline-flex items-center">
      <Link to={migrationPath(m.id)} className="badge hover:opacity-80" style={{ backgroundColor: st.bg, color: st.fg }}
        title={m.failureReason ?? `${m.appliedCount} applied`}>
        {st.label}{m.appliedCount > 0 ? ` · ${m.appliedCount}` : ''}
      </Link>
      <RunIcon run={m.run} />
    </span>
  );
}
