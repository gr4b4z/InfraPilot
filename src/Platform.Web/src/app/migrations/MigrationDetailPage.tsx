import { useCallback, useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { format, formatDistanceToNow } from 'date-fns';
import {
  AlertTriangle,
  ArrowLeft,
  Clock,
  Database,
  ExternalLink,
  History,
  ListChecks,
  Loader2,
  PlayCircle,
  Rocket,
  ScrollText,
  SkipForward,
} from 'lucide-react';
import { api } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { providerLabel } from '@/lib/workItem';
import { useEntityRefresh, useIsBackgroundRefresh } from '@/hooks/useEntityEvents';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { DeploymentLogViewer } from '@/components/deployments/DeploymentLogViewer';
import { MigrationCounts } from '@/components/migrations/MigrationCounts';
import {
  KIND_LABEL,
  formatDurationMs,
  migrationItemStyle,
  migrationPath,
  migrationStatusStyle,
} from '@/components/migrations/migrationStatus';
import type { MigrationRunDetail, MigrationRunSummary } from '@/lib/types';

/**
 * One migration: the run, each migration inside it and how it went, what it printed, the deployment
 * it belongs to, and — for a data migration — the newer one that replaced it or the older ones it
 * replaced. Read-only; running or approving migrations is not something the portal does (yet).
 */
export function MigrationDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [detail, setDetail] = useState<MigrationRunDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const tick = useEntityRefresh(['migration'], { filter: (evt) => !evt.id || evt.id === id });
  const isBackgroundRefresh = useIsBackgroundRefresh();

  const load = useCallback(async (silent: boolean) => {
    if (!id) return;
    if (!silent) setLoading(true);
    try {
      setDetail(await api.getMigration(id));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load this migration');
    } finally {
      setLoading(false);
    }
  }, [id]);

  useEffect(() => { void load(isBackgroundRefresh(id ?? '')); }, [load, tick, id, isBackgroundRefresh]);

  const run = detail?.run;
  useDocumentTitle([run ? `${run.service} ${KIND_LABEL[run.kind].toLowerCase()} migration` : id, run?.environment, 'Migration']);

  if (loading) {
    return (
      <div className="flex items-center justify-center py-24">
        <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
      </div>
    );
  }
  if (error || !detail || !run) {
    return (
      <div className="space-y-4">
        <BackLink />
        <div className="flex items-center gap-3 p-4 rounded-xl border" style={{ backgroundColor: 'var(--danger-bg)', borderColor: 'var(--danger)', color: 'var(--danger)' }}>
          <AlertTriangle size={18} />
          <span className="text-[13px] font-medium">{error ?? 'Migration not found'}</span>
        </div>
      </div>
    );
  }

  const st = migrationStatusStyle(run.status);
  const ciHref = detail.ciRun?.jobUrl ?? detail.ciRun?.runUrl ?? run.runUrl;

  return (
    <div className="space-y-6">
      <BackLink />

      <div className="flex items-start justify-between gap-4 flex-wrap">
        <div className="min-w-0">
          <div className="flex items-center gap-2.5 flex-wrap">
            <Database size={18} style={{ color: 'var(--text-muted)' }} />
            <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
              {run.service}
            </h1>
            <EnvBadge env={run.environment} />
            <span className="badge" style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}>
              {KIND_LABEL[run.kind]} migration
            </span>
            <span className="badge" style={{ backgroundColor: st.bg, color: st.fg }}>
              <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ backgroundColor: st.fg }} />
              {st.label}
            </span>
          </div>
          <div className="flex items-center gap-2 flex-wrap mt-1.5 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
            <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>v{run.version}</span>
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <span className="inline-flex items-center gap-1" title={format(new Date(run.queuedAt), 'PPpp')}>
              <Clock size={12} />
              {run.kind === 'data' ? 'due ' : ''}{formatDistanceToNow(new Date(run.queuedAt), { addSuffix: true })}
            </span>
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <span>{run.product}</span>
            {run.attempts > 1 && (
              <>
                <span style={{ color: 'var(--text-muted)' }}>·</span>
                <span>{run.attempts} attempts</span>
              </>
            )}
          </div>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {ciHref && (
            <a
              href={ciHref}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
              style={{ backgroundColor: 'var(--accent)', color: 'var(--accent-fg)' }}
            >
              <PlayCircle size={13} />
              {detail.ciRun?.runNumber ? `Run #${detail.ciRun.runNumber}` : `View ${providerLabel(detail.ciRun?.provider, 'pipeline')} run`}
              <ExternalLink size={11} />
            </a>
          )}
          {detail.deployment && (
            <Link
              to={`/deployments/events/${detail.deployment.id}?from=${encodeURIComponent(migrationPath(run.id))}&fromLabel=${encodeURIComponent('Migration')}`}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
              style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}
            >
              <Rocket size={13} /> Deployment
            </Link>
          )}
        </div>
      </div>

      <StatusCallout run={run} supersededBy={detail.supersededBy ?? null} />

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-4">
          <Card title="Migrations" icon={ListChecks} aside={<MigrationCounts run={run} />}>
            {detail.migrations.length === 0 ? (
              <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                {run.status === 'pending' || run.status === 'superseded'
                  ? 'It has not run, so there is nothing to list yet.'
                  : run.status === 'succeeded'
                    ? 'Nothing was pending — every migration of this version had already been applied here.'
                    : 'The output names no individual migrations. The log below is the whole story.'}
              </p>
            ) : (
              <div className="overflow-x-auto -mx-2">
                <table className="w-full text-[12px]">
                  <thead style={{ color: 'var(--text-muted)' }}>
                    <tr>
                      <th className="text-left font-medium px-2 py-1.5">#</th>
                      <th className="text-left font-medium px-2 py-1.5">Migration</th>
                      <th className="text-left font-medium px-2 py-1.5">Result</th>
                      <th className="text-left font-medium px-2 py-1.5">Took</th>
                      <th className="text-left font-medium px-2 py-1.5">Applied</th>
                    </tr>
                  </thead>
                  <tbody>
                    {detail.migrations.map((m) => {
                      const ist = migrationItemStyle(m.status);
                      return (
                        <tr key={m.name} className="border-t align-top" style={{ borderColor: 'var(--border-color)' }}>
                          <td className="px-2 py-2 font-mono" style={{ color: 'var(--text-muted)' }}>{m.order ?? ''}</td>
                          <td className="px-2 py-2">
                            <Link
                              to={`/migrations?q=${encodeURIComponent(m.name)}`}
                              className="font-mono hover:underline"
                              style={{ color: 'var(--text-primary)' }}
                              title="Where else has this migration run?"
                            >
                              {m.name}
                            </Link>
                            {m.error && (
                              <div className="mt-1 font-mono text-[11.5px] whitespace-pre-wrap break-words" style={{ color: 'var(--danger)' }}>
                                {m.error}
                              </div>
                            )}
                          </td>
                          <td className="px-2 py-2 whitespace-nowrap font-medium" style={{ color: ist.fg }}>{ist.mark} {ist.label}</td>
                          <td className="px-2 py-2 whitespace-nowrap" style={{ color: 'var(--text-secondary)' }}>{formatDurationMs(m.durationMs) ?? ''}</td>
                          <td className="px-2 py-2 whitespace-nowrap" style={{ color: 'var(--text-secondary)' }}>
                            {m.appliedAt ? format(new Date(m.appliedAt), 'PP p') : ''}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </Card>

          <Card title="Output" icon={ScrollText}>
            <DeploymentLogViewer
              eventId={run.deployEventId ?? run.id}
              logs={detail.logs}
              defaultOpen={run.status === 'failed'}
              fetchLog={(logId) => api.getMigrationLog(run.id, logId)}
              emptyText={run.status === 'pending' || run.status === 'superseded'
                ? 'Nothing yet — it has not run.'
                : 'The pipeline sent no output for this migration.'}
            />
          </Card>
        </div>

        <div className="space-y-4">
          <Card title="Timeline" icon={Clock}>
            <dl className="text-[12px] space-y-1.5">
              <TimelineRow label={run.kind === 'data' ? 'Due' : 'Deployed'} at={run.queuedAt} />
              {run.startedAt && <TimelineRow label="Started" at={run.startedAt} />}
              {run.completedAt && <TimelineRow label="Finished" at={run.completedAt} />}
              {detail.deployment && (
                <div className="flex justify-between gap-2">
                  <dt style={{ color: 'var(--text-muted)' }}>Deployment</dt>
                  <dd>
                    <Link to={`/deployments/events/${detail.deployment.id}`} className="hover:underline" style={{ color: 'var(--accent)' }}>
                      v{detail.deployment.version} · {detail.deployment.status}
                    </Link>
                  </dd>
                </div>
              )}
              <div className="flex justify-between gap-2">
                <dt style={{ color: 'var(--text-muted)' }}>Reported by</dt>
                <dd style={{ color: 'var(--text-secondary)' }}>{run.source}</dd>
              </div>
            </dl>
          </Card>

          {detail.supersedes.length > 0 && (
            <Card title="Replaced" icon={SkipForward}>
              <p className="text-[12px] mb-2" style={{ color: 'var(--text-muted)' }}>
                Older data migrations this one superseded — they will never run.
              </p>
              <RunList runs={detail.supersedes} currentId={run.id} />
            </Card>
          )}

          <Card title={`${KIND_LABEL[run.kind]} migrations here`} icon={History}>
            <RunList runs={detail.history} currentId={run.id} />
          </Card>
        </div>
      </div>
    </div>
  );
}

/** Back to where the reader came from (a deployment passes `from`), else the migrations list. */
function BackLink() {
  const [params] = useSearchParams();
  const from = params.get('from');
  return (
    <Link to={from ?? '/migrations'} className="inline-flex items-center gap-1.5 text-[13px] transition-opacity hover:opacity-80" style={{ color: 'var(--text-muted)' }}>
      <ArrowLeft size={14} /> {from ? params.get('fromLabel') ?? 'Back' : 'Migrations'}
    </Link>
  );
}

function StatusCallout({ run, supersededBy }: { run: MigrationRunSummary; supersededBy: MigrationRunSummary | null }) {
  if (run.status === 'failed') {
    return (
      <div className="rounded-xl border p-4 space-y-2" style={{ backgroundColor: 'var(--danger-bg)', borderColor: 'var(--danger)' }}>
        <div className="flex items-center gap-2">
          <AlertTriangle size={16} style={{ color: 'var(--danger)' }} />
          <h2 className="text-[13px] font-semibold" style={{ color: 'var(--danger)' }}>This migration failed</h2>
        </div>
        {run.failureReason ? (
          <p className="font-mono text-[12.5px] whitespace-pre-wrap break-words" style={{ color: 'var(--text-primary)' }}>{run.failureReason}</p>
        ) : (
          <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>The pipeline didn't name a cause — the output below is the place to look.</p>
        )}
      </div>
    );
  }
  if (run.status === 'pending') {
    return (
      <Callout tone="warning">
        {run.kind === 'data'
          ? <>The service is deployed, but this data migration hasn't run yet. Until it does, data that v{run.version} expects may be missing.</>
          : <>Waiting to run.</>}
      </Callout>
    );
  }
  if (run.status === 'superseded') {
    return (
      <Callout tone="muted">
        Superseded — a newer version's data migration replaced this one, and only the newest ever runs.
        {supersededBy && (
          <> See <Link to={migrationPath(supersededBy.id)} className="underline">v{supersededBy.version}</Link> ({migrationStatusStyle(supersededBy.status).label.toLowerCase()}).</>
        )}
      </Callout>
    );
  }
  if (run.status === 'skipped' && run.skipReason) {
    return <Callout tone="muted">Skipped: {run.skipReason}</Callout>;
  }
  return null;
}

function Callout({ tone, children }: { tone: 'warning' | 'muted'; children: React.ReactNode }) {
  const style = tone === 'warning'
    ? { backgroundColor: 'var(--warning-bg)', borderColor: 'var(--warning)', color: 'var(--text-primary)' }
    : { backgroundColor: 'var(--bg-secondary)', borderColor: 'var(--border-color)', color: 'var(--text-secondary)' };
  return <div className="rounded-xl border p-4 text-[13px]" style={style}>{children}</div>;
}

function TimelineRow({ label, at }: { label: string; at: string }) {
  return (
    <div className="flex justify-between gap-2">
      <dt style={{ color: 'var(--text-muted)' }}>{label}</dt>
      <dd style={{ color: 'var(--text-secondary)' }} title={format(new Date(at), 'PPpp')}>
        {formatDistanceToNow(new Date(at), { addSuffix: true })}
      </dd>
    </div>
  );
}

function RunList({ runs, currentId }: { runs: MigrationRunSummary[]; currentId: string }) {
  if (runs.length === 0) return <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>None.</p>;
  return (
    <ul className="space-y-1">
      {runs.map((r) => {
        const st = migrationStatusStyle(r.status);
        const current = r.id === currentId;
        return (
          <li key={r.id}>
            <Link
              to={migrationPath(r.id)}
              className="flex items-center justify-between gap-2 px-2 py-1.5 rounded-lg text-[12px] transition-colors hover:bg-[var(--bg-secondary)]"
              style={{ backgroundColor: current ? 'var(--accent-muted)' : undefined }}
            >
              <span className="font-mono truncate" style={{ color: 'var(--text-primary)' }}>v{r.version}</span>
              <span className="flex items-center gap-2 shrink-0">
                <span style={{ color: 'var(--text-muted)' }}>{formatDistanceToNow(new Date(r.queuedAt), { addSuffix: true })}</span>
                <span className="font-medium" style={{ color: st.fg }}>{st.label}</span>
              </span>
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

function Card({ title, icon: Icon, aside, children }: {
  title: string;
  icon: React.ComponentType<{ size?: number; className?: string; style?: React.CSSProperties }>;
  aside?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <section className="rounded-xl border p-5" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex items-center justify-between gap-2 mb-3">
        <h2 className="text-[11px] font-semibold uppercase tracking-wider flex items-center gap-1.5" style={{ color: 'var(--text-muted)' }}>
          <Icon size={12} /> {title}
        </h2>
        {aside && <span className="text-[12px]" style={{ color: 'var(--text-secondary)' }}>{aside}</span>}
      </div>
      {children}
    </section>
  );
}
