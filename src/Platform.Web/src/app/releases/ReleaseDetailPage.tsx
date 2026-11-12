import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  ArrowLeft,
  CalendarDays,
  CircleCheck,
  CircleDashed,
  CircleDot,
  GitPullRequest,
  Loader2,
  Workflow,
  Milestone,
  Pencil,
  Plus,
  SkipForward,
  Trash2,
} from 'lucide-react';
import { api } from '@/lib/api';
import type {
  Release,
  ReleaseCheck,
  ReleaseLink,
  ReleaseRunRef,
  ReleaseProgress,
  ReleaseService,
  ReleaseStep,
  ReleaseStepStatus,
  ReleaseStepUpdate,
} from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { useAuthStore } from '@/stores/authStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { inputClass, inputStyle, labelClass, labelStyle } from '@/app/settings/formStyles';
import { migrationPath, migrationStatusStyle } from '@/components/migrations/migrationStatus';
import {
  CHECK_LABEL,
  CHECK_STATE,
  STEP_STATUS,
  cleanLinks,
  describeManagers,
  formatTargetDate,
  fromLocalInput,
  releaseStatusStyle,
  toLocalInput,
} from '@/components/releases/releaseStatus';
import { LinkList, LinksEditor, RunList } from '@/components/releases/ReleaseLinks';
import { useFeatureFlagsStore, FeatureFlag } from '@/stores/featureFlagsStore';
import { ReleaseForm } from './ReleaseForm';

const fmt = (iso: string | null | undefined) =>
  iso
    ? new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
    : null;

const STEP_ICON: Record<ReleaseStepStatus, typeof CircleCheck> = {
  pending: CircleDashed,
  in_progress: CircleDot,
  done: CircleCheck,
  skipped: SkipForward,
};

/**
 * One release: its checklist, what the portal sees happening for each step that it can check, and
 * every service of the release from staging to production.
 *
 * Steps are moved on by the release manager — the checks inform that call, they don't make it — and
 * can be backdated, so a release can be written down after it happened. The checks and the service
 * table are recomputed by the API on every read and refreshed live as promotions, deployments and
 * migrations change.
 */
export function ReleaseDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const user = useAuthStore((s) => s.user);
  const { getDisplayName } = useSettingsStore();

  const [release, setRelease] = useState<Release | null>(null);
  const [progress, setProgress] = useState<ReleaseProgress | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [progressError, setProgressError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [confirm, setConfirm] = useState<'complete' | 'cancel' | 'delete' | null>(null);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [managers, setManagers] = useState<string | null>(null);

  useDocumentTitle([release?.title ?? 'Release', 'Releases']);

  const releaseTick = useEntityRefresh(['release'], { filter: (e) => !e.id || e.id === id });
  const recordsTick = useEntityRefresh(['promotion', 'deployment', 'migration']);

  useEffect(() => {
    let cancelled = false;
    api.getRelease(id)
      .then((r) => { if (!cancelled) { setRelease(r); setError(null); } })
      .catch((e) => { if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load the release'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [id, releaseTick]);

  useEffect(() => {
    let cancelled = false;
    api.getReleaseProgress(id)
      .then((p) => { if (!cancelled) { setProgress(p); setProgressError(null); } })
      .catch((e) => { if (!cancelled) setProgressError(e instanceof Error ? e.message : 'Failed to load the checks'); });
    return () => { cancelled = true; };
  }, [id, releaseTick, recordsTick]);

  // Who may run it besides admins: the approvers of the release-manager requirement on its products'
  // production gates.
  const productsKey = release ? `${release.productionEnvironment}|${release.products.join(',')}` : '';
  useEffect(() => {
    if (!productsKey) return;
    const [environment, list] = productsKey.split('|');
    let cancelled = false;
    api.getReleaseManagers({ environment })
      .then((m) => { if (!cancelled) setManagers(describeManagers(m.gates, list.split(',')) || `nobody on a “${m.requirement}” gate`); })
      .catch(() => { if (!cancelled) setManagers(null); });
    return () => { cancelled = true; };
  }, [productsKey]);

  const checks = useMemo(
    () => new Map((progress?.checks ?? []).map((c) => [c.key, c])),
    [progress],
  );

  const updateStep = useCallback(async (step: ReleaseStep, update: ReleaseStepUpdate) => {
    const saved = await api.updateReleaseStep(id, step.id, update);
    setRelease(saved);
  }, [id]);

  if (loading) return <div className="skeleton h-60" />;
  if (error || !release) {
    return (
      <div className="space-y-3">
        <BackLink />
        <div className="px-4 py-3 rounded-xl border text-[13px]" style={{ borderColor: 'var(--danger)', backgroundColor: 'var(--danger-bg)', color: 'var(--danger)' }}>
          {error ?? 'Release not found'}
        </div>
      </div>
    );
  }

  const st = releaseStatusStyle(release.status);
  const canManage = release.canManage;
  const closed = release.status === 'completed' || release.status === 'cancelled';
  const openSteps = release.steps.filter((s) => s.status === 'pending' || s.status === 'in_progress').length;
  // The step the release is on: the first one still open. It gets the prominent actions.
  const currentStepId = release.steps.find((s) => s.status === 'pending' || s.status === 'in_progress')?.id;
  const target = formatTargetDate(release.targetDate);

  const runAction = async (action: () => Promise<Release | void>, after?: (r: Release | void) => void) => {
    setBusy(true);
    setActionError(null);
    try {
      const result = await action();
      if (result) setRelease(result);
      setConfirm(null);
      after?.(result);
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Something went wrong');
    } finally {
      setBusy(false);
    }
  };

  const btn = 'inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80';
  const btnOutline = { border: '1px solid var(--border-color)', color: 'var(--text-secondary)' };

  return (
    <div className="space-y-4">
      <BackLink />

      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div className="flex items-start gap-2">
          <Milestone size={22} className="mt-0.5" style={{ color: 'var(--accent)' }} />
          <div>
            <div className="flex items-center gap-2 flex-wrap">
              <h1 className="text-xl font-semibold">{release.title}</h1>
              <span className="badge" style={{ backgroundColor: st.bg, color: st.fg }}>{st.label}</span>
            </div>
            <p className="text-[12px] mt-1 flex items-center gap-x-3 gap-y-1 flex-wrap" style={{ color: 'var(--text-secondary)' }}>
              <span>{release.products.join(' · ')}</span>
              <span>
                <EnvLabel env={release.stagingEnvironment} /> → <EnvLabel env={release.productionEnvironment} />
              </span>
              {target && <span className="inline-flex items-center gap-1"><CalendarDays size={12} /> {target}</span>}
              <span>Release manager: {release.releaseManagerName ?? release.releaseManager ?? '—'}</span>
            </p>
            <p className="text-[11px] mt-1" style={{ color: 'var(--text-muted)' }}>
              {release.startedAt && <>Started {fmt(release.startedAt)}. </>}
              {release.completedAt && <>Released {fmt(release.completedAt)}. </>}
              {release.cancelledAt && <>Cancelled {fmt(release.cancelledAt)}{release.cancelReason ? `: ${release.cancelReason}` : ''}. </>}
              {managers && <>Run by admins and the release managers ({managers}).</>}
            </p>
          </div>
        </div>

        {canManage && (
          <div className="flex items-center gap-2 flex-wrap">
            <button className={btn} style={btnOutline} onClick={() => setEditing(true)}>
              <Pencil size={13} /> Edit
            </button>
            {closed ? (
              <button className={btn} style={btnOutline} disabled={busy} onClick={() => void runAction(() => api.reopenRelease(release.id))}>
                Reopen
              </button>
            ) : (
              <>
                <button className={btn} style={btnOutline} onClick={() => setConfirm('cancel')}>Cancel release</button>
                <button className={`${btn} text-white`} style={{ backgroundColor: 'var(--success-solid)' }} onClick={() => setConfirm('complete')}>
                  <CircleCheck size={14} /> Mark released
                </button>
              </>
            )}
            {user?.isAdmin && (
              <button className={btn} style={{ ...btnOutline, color: 'var(--danger)' }} onClick={() => setConfirm('delete')} aria-label="Delete release">
                <Trash2 size={13} />
              </button>
            )}
          </div>
        )}
      </div>

      {actionError && !confirm && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{actionError}</p>}

      <OverviewTiles progress={progress} release={release} />
      {progressError && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{progressError}</p>}

      <LinksSection release={release} progress={progress} onEdit={canManage ? () => setEditing(true) : undefined} />

      <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
        <div className="flex items-center justify-between mb-3">
          <h2 className="text-[14px] font-semibold">Steps</h2>
          <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {release.steps.length - openSteps} of {release.steps.length} closed
          </span>
        </div>
        <ol className="relative">
          {release.steps.map((step, i) => (
            <StepItem
              key={step.id}
              step={step}
              last={i === release.steps.length - 1}
              check={step.check ? checks.get(step.check) : undefined}
              current={step.id === currentStepId}
              canManage={canManage}
              productionEnv={release.productionEnvironment}
              products={release.products}
              onUpdate={(update) => updateStep(step, update)}
              onDelete={step.custom ? async () => setRelease(await api.deleteReleaseStep(release.id, step.id)) : undefined}
            />
          ))}
        </ol>
        {canManage && <AddStep release={release} onAdded={setRelease} />}
      </section>

      <ServicesTable progress={progress} release={release} />

      {(release.notes || release.includedServices.length > 0 || release.excludedServices.length > 0) && (
        <section className="rounded-xl border p-4 space-y-2 text-[13px]" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
          <h2 className="text-[14px] font-semibold">Notes and scope</h2>
          {release.notes && <p className="whitespace-pre-wrap" style={{ color: 'var(--text-secondary)' }}>{release.notes}</p>}
          {release.includedServices.length > 0 && (
            <p style={{ color: 'var(--text-secondary)' }}><b>Also ships:</b> {release.includedServices.join(', ')}</p>
          )}
          {release.excludedServices.length > 0 && (
            <p style={{ color: 'var(--text-secondary)' }}><b>Not shipping:</b> {release.excludedServices.join(', ')}</p>
          )}
        </section>
      )}

      {editing && (
        <ReleaseForm release={release} onClose={() => setEditing(false)} onSaved={(saved) => { setRelease(saved); setEditing(false); }} />
      )}

      {confirm === 'complete' && (
        <ConfirmDialog
          title={`Mark ${release.title} as released?`}
          body={openSteps > 0
            ? `${openSteps} ${openSteps === 1 ? 'step is' : 'steps are'} still open. The release is closed as it stands; you can reopen it later.`
            : 'Every step is closed. Remember to mark the version released in the issue tracker.'}
          confirmLabel="Mark released"
          confirmTone="success"
          busy={busy}
          error={actionError}
          onConfirm={() => void runAction(() => api.completeRelease(release.id))}
          onCancel={() => { setConfirm(null); setActionError(null); }}
        />
      )}
      {confirm === 'cancel' && (
        <ConfirmDialog
          title={`Cancel ${release.title}?`}
          body="A cancelled release stays on record, and frees its version for its products."
          confirmLabel="Cancel release"
          confirmTone="danger"
          commentLabel="Why is it cancelled?"
          commentRequired
          busy={busy}
          error={actionError}
          onConfirm={(reason) => void runAction(() => api.cancelRelease(release.id, reason))}
          onCancel={() => { setConfirm(null); setActionError(null); }}
        />
      )}
      {confirm === 'delete' && (
        <ConfirmDialog
          title={`Delete ${release.title}?`}
          body="The release and its steps are removed for good. Deployments, promotions and migrations are not touched."
          confirmLabel="Delete"
          confirmTone="danger"
          busy={busy}
          error={actionError}
          onConfirm={() => void runAction(async () => { await api.deleteRelease(release.id); }, () => navigate('/releases'))}
          onCancel={() => { setConfirm(null); setActionError(null); }}
        />
      )}

      <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
        Checks read the {release.version} versions of {release.products.join(', ')} on {getDisplayName(release.stagingEnvironment)} and{' '}
        {getDisplayName(release.productionEnvironment)}
        {progress ? `, from ${fmt(progress.from)} to ${release.completedAt ? fmt(progress.to) : 'now'}` : ''}.
      </p>
    </div>
  );
}

function BackLink() {
  return (
    <Link to="/releases" className="inline-flex items-center gap-1 text-[12px] hover:underline" style={{ color: 'var(--text-muted)' }}>
      <ArrowLeft size={13} /> Releases
    </Link>
  );
}

function OverviewTiles({ progress, release }: { progress: ReleaseProgress | null; release: Release }) {
  const get = (key: string) => progress?.checks.find((c) => c.key === key);
  const tiles: { label: string; value: string; sub?: string; check?: ReleaseCheck }[] = [
    { label: 'Services', value: progress ? String(progress.services.length) : '…', sub: `on the ${release.version} line` },
    tile('Approved', get('production-promotions')),
    tile('Deployed', get('production-deploys')),
    tile('Migrations finished', get('production-migrations')),
  ];
  return (
    <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
      {tiles.map((t) => {
        const tone = t.check ? CHECK_STATE[t.check.state] : null;
        return (
          <div key={t.label} className="rounded-xl border px-4 py-3" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
            <div className={labelClass} style={labelStyle}>{t.label}</div>
            <div className="text-[20px] font-semibold mt-0.5" style={{ color: tone?.fg ?? 'var(--text-primary)' }}>{t.value}</div>
            {t.sub && <div className="text-[11px]" style={{ color: 'var(--text-muted)' }}>{t.sub}</div>}
          </div>
        );
      })}
    </div>
  );

  function tile(label: string, check: ReleaseCheck | undefined) {
    if (!check) return { label, value: '…' };
    return {
      label,
      value: check.total === 0 ? '—' : `${check.done}/${check.total}`,
      sub: check.lastAt ? `last ${fmt(check.lastAt)}` : CHECK_STATE[check.state].label,
      check,
    };
  }
}

function StepItem({
  step,
  last,
  check,
  current,
  canManage,
  productionEnv,
  products,
  onUpdate,
  onDelete,
}: {
  step: ReleaseStep;
  last: boolean;
  check: ReleaseCheck | undefined;
  current: boolean;
  canManage: boolean;
  productionEnv: string;
  products: string[];
  onUpdate: (update: ReleaseStepUpdate) => Promise<void>;
  onDelete?: () => Promise<void>;
}) {
  const [mode, setMode] = useState<null | 'done' | 'edit'>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const Icon = STEP_ICON[step.status];
  const status = STEP_STATUS[step.status];

  const run = async (update: ReleaseStepUpdate) => {
    setBusy(true);
    setError(null);
    try {
      await onUpdate(update);
      setMode(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not update the step');
    } finally {
      setBusy(false);
    }
  };

  const small = 'inline-flex items-center gap-1 px-2 py-1 rounded-md text-[12px] transition-opacity hover:opacity-80 disabled:opacity-50';
  const outline = { border: '1px solid var(--border-color)', color: 'var(--text-secondary)' };

  const promotionsLink = `/promotions?tab=pending&targetEnv=${encodeURIComponent(productionEnv)}`
    + (products.length === 1 ? `&product=${encodeURIComponent(products[0])}` : '');

  return (
    <li className="relative pl-8 pb-5" data-step-key={step.key}>
      {!last && <span className="absolute left-[9px] top-6 bottom-0 w-px" style={{ backgroundColor: 'var(--border-color)' }} />}
      <Icon size={19} className="absolute left-0 top-0.5" style={{ color: status.fg }} aria-label={status.label} />

      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div className="min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <span
              className="text-[14px] font-medium"
              style={{ color: step.status === 'skipped' ? 'var(--text-muted)' : 'var(--text-primary)', textDecoration: step.status === 'skipped' ? 'line-through' : undefined }}
            >
              {step.title}
            </span>
            {step.owner && (
              <span className="text-[11px] px-1.5 py-0.5 rounded" style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}>{step.owner}</span>
            )}
            <span className="text-[11px] font-medium" style={{ color: status.fg }}>{status.label}</span>
          </div>
          <p className="text-[12px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
            {/* A step marked done in one go started when it finished — say it once. */}
            {step.startedAt && step.startedAt !== step.completedAt && <>Started {fmt(step.startedAt)}</>}
            {step.startedAt && step.completedAt && step.startedAt !== step.completedAt && ' · '}
            {step.completedAt && <>{step.status === 'skipped' ? 'Skipped' : 'Done'} {fmt(step.completedAt)}</>}
            {/* Who moved it on, which for a backdated step is whoever recorded it — not necessarily who did it. */}
            {step.completedAt && (step.completedByName || step.completedBy) && <> · marked by {step.completedByName ?? step.completedBy}</>}
          </p>
        </div>

        {canManage && mode === null && (
          <div className="flex items-center gap-1.5 flex-wrap">
            {step.status === 'pending' && (
              <button className={small} style={outline} disabled={busy} onClick={() => void run({ status: 'in_progress' })}>Start</button>
            )}
            {(step.status === 'pending' || step.status === 'in_progress') && (
              <>
                <button
                  className={current || step.status === 'in_progress' ? `${small} text-white` : small}
                  style={current || step.status === 'in_progress' ? { backgroundColor: 'var(--success-solid)' } : outline}
                  disabled={busy}
                  onClick={() => setMode('done')}
                >
                  <CircleCheck size={12} /> Mark done
                </button>
                <button className={small} style={outline} disabled={busy} onClick={() => void run({ status: 'skipped' })}>Skip</button>
              </>
            )}
            {step.status === 'in_progress' && (
              <button className={small} style={outline} disabled={busy} onClick={() => void run({ status: 'pending' })}>Back to to-do</button>
            )}
            {(step.status === 'done' || step.status === 'skipped') && (
              <button className={small} style={outline} disabled={busy} onClick={() => void run({ status: 'in_progress' })}>Reopen</button>
            )}
            <button className={small} style={outline} disabled={busy} onClick={() => setMode('edit')} aria-label={`Edit ${step.title}`}>
              <Pencil size={12} />
            </button>
          </div>
        )}
      </div>

      {step.description && <p className="text-[12px] mt-1" style={{ color: 'var(--text-secondary)' }}>{step.description}</p>}

      {step.key === 'production-promotions' && step.status !== 'done' && step.status !== 'skipped' && (
        <Link to={promotionsLink} className="inline-flex items-center gap-1 text-[12px] mt-1.5 hover:underline" style={{ color: 'var(--accent)' }}>
          <GitPullRequest size={12} /> Open the pending promotions (Mass approve is there)
        </Link>
      )}

      {check && <CheckPanel check={check} />}

      {(step.note || step.links.length > 0) && mode !== 'edit' && (
        <div className="mt-2 text-[12px] rounded-lg px-3 py-2 space-y-1.5" style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}>
          {step.note && <p className="whitespace-pre-wrap">{step.note}</p>}
          <LinkList links={step.links} />
        </div>
      )}

      {mode && (
        <StepEditor
          step={step}
          mode={mode}
          check={check}
          busy={busy}
          error={error}
          onCancel={() => { setMode(null); setError(null); }}
          onSave={(update) => void run(update)}
          onDelete={onDelete ? async () => { setBusy(true); try { await onDelete(); } catch (e) { setError(e instanceof Error ? e.message : 'Could not remove the step'); setBusy(false); } } : undefined}
        />
      )}
      {error && !mode && <p className="text-[12px] mt-1" style={{ color: 'var(--danger)' }}>{error}</p>}
    </li>
  );
}

function CheckPanel({ check }: { check: ReleaseCheck }) {
  const [showAll, setShowAll] = useState(false);
  const tone = CHECK_STATE[check.state];
  const pct = check.total === 0 ? 0 : Math.round((check.done / check.total) * 100);
  const issues = showAll ? check.issues : check.issues.slice(0, 5);
  const range = check.firstAt && check.lastAt && check.firstAt !== check.lastAt
    ? `${fmt(check.firstAt)} – ${fmt(check.lastAt)}`
    : fmt(check.lastAt ?? check.firstAt);
  return (
    <div className="mt-2 rounded-lg border px-3 py-2" style={{ borderColor: 'var(--border-color)' }} data-check={check.key}>
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <span className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
          Portal check · {CHECK_LABEL[check.key]}
        </span>
        <span className="badge" style={{ backgroundColor: tone.bg, color: tone.fg }}>{tone.label}</span>
      </div>
      {check.total > 0 && (
        <div className="h-1.5 rounded-full overflow-hidden mt-2" style={{ backgroundColor: 'var(--bg-secondary)' }}>
          <div className="h-full rounded-full" style={{ width: `${pct}%`, backgroundColor: check.state === 'complete' ? 'var(--success)' : 'var(--accent)' }} />
        </div>
      )}
      <p className="text-[12px] mt-1.5" style={{ color: 'var(--text-primary)' }}>{check.summary}</p>
      {range && <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>{range}</p>}
      {issues.length > 0 && (
        <ul className="mt-1.5 space-y-0.5 text-[12px]">
          {issues.map((i) => (
            <li key={`${i.product}/${i.service}/${i.problem}`} style={{ color: 'var(--text-secondary)' }}>
              <Link to={`/deployments/${encodeURIComponent(i.product)}/${encodeURIComponent(i.service)}`} className="font-medium hover:underline" style={{ color: 'var(--text-primary)' }}>
                {i.service}
              </Link>{' '}
              — {i.problem}
            </li>
          ))}
        </ul>
      )}
      {check.issues.length > 5 && (
        <button className="text-[12px] mt-1 hover:underline" style={{ color: 'var(--accent)' }} onClick={() => setShowAll((v) => !v)}>
          {showAll ? 'Show fewer' : `Show all ${check.issues.length}`}
        </button>
      )}
      <RunList runs={check.runs} noun={check.key === 'production-migrations' ? 'migration' : 'service'} />
    </div>
  );
}

function StepEditor({
  step,
  mode,
  check,
  busy,
  error,
  onCancel,
  onSave,
  onDelete,
}: {
  step: ReleaseStep;
  mode: 'done' | 'edit';
  check: ReleaseCheck | undefined;
  busy: boolean;
  error: string | null;
  onCancel: () => void;
  onSave: (update: ReleaseStepUpdate) => void;
  onDelete?: () => void;
}) {
  const [note, setNote] = useState(step.note ?? '');
  const [links, setLinks] = useState<ReleaseLink[]>(step.links.length ? step.links : [{ label: null, url: '' }]);
  const [startedAt, setStartedAt] = useState(toLocalInput(step.startedAt));
  const [completedAt, setCompletedAt] = useState(toLocalInput(step.completedAt) || (mode === 'done' ? toLocalInput(new Date().toISOString()) : ''));
  const [title, setTitle] = useState(step.title);
  const closedStep = step.status === 'done' || step.status === 'skipped';
  const showCompleted = mode === 'done' || closedStep;
  const showStarted = mode === 'edit' ? step.status !== 'pending' : true;

  const save = () => {
    const update: ReleaseStepUpdate = { note, links: cleanLinks(links) };
    if (mode === 'done') update.status = 'done';
    if (mode === 'edit' && step.custom && title.trim() && title.trim() !== step.title) update.title = title.trim();
    const s = fromLocalInput(startedAt);
    const c = fromLocalInput(completedAt);
    if (showStarted && s && s !== step.startedAt) update.startedAt = s;
    if (showCompleted && c && c !== step.completedAt) update.completedAt = c;
    onSave(update);
  };

  return (
    <div className="mt-2 rounded-lg border p-3 space-y-2" style={{ borderColor: 'var(--accent)', backgroundColor: 'var(--bg-primary)' }}>
      {mode === 'done' && check && check.state !== 'complete' && (
        <p className="text-[12px]" style={{ color: 'var(--warning)' }}>
          The portal's check isn't complete yet: {check.summary}
        </p>
      )}
      {mode === 'edit' && step.custom && (
        <label className="flex flex-col gap-1">
          <span className={labelClass} style={labelStyle}>Step</span>
          <input value={title} onChange={(e) => setTitle(e.target.value)} className={inputClass} style={inputStyle} />
        </label>
      )}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
        {showStarted && (
          <label className="flex flex-col gap-1">
            <span className={labelClass} style={labelStyle}>Started</span>
            <input type="datetime-local" value={startedAt} onChange={(e) => setStartedAt(e.target.value)} className={inputClass} style={inputStyle} />
          </label>
        )}
        {showCompleted && (
          <label className="flex flex-col gap-1">
            <span className={labelClass} style={labelStyle}>{step.status === 'skipped' ? 'Skipped' : 'Done'}</span>
            <input type="datetime-local" value={completedAt} onChange={(e) => setCompletedAt(e.target.value)} className={inputClass} style={inputStyle} />
          </label>
        )}
      </div>
      <label className="flex flex-col gap-1">
        <span className={labelClass} style={labelStyle}>Note</span>
        <textarea value={note} onChange={(e) => setNote(e.target.value)} rows={2} placeholder="What happened" className={inputClass} style={inputStyle} />
      </label>
      <div className="flex flex-col gap-1">
        <span className={labelClass} style={labelStyle}>Links</span>
        <LinksEditor links={links} onChange={setLinks} />
      </div>
      {error && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{error}</p>}
      <div className="flex items-center justify-between gap-2">
        <div>
          {mode === 'edit' && onDelete && (
            <button onClick={onDelete} disabled={busy} className="inline-flex items-center gap-1 text-[12px] hover:underline" style={{ color: 'var(--danger)' }}>
              <Trash2 size={12} /> Remove step
            </button>
          )}
        </div>
        <div className="flex gap-2">
          <button onClick={onCancel} className="px-2.5 py-1 rounded-md text-[12px]" style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}>
            Cancel
          </button>
          <button
            onClick={save}
            disabled={busy}
            className="inline-flex items-center gap-1 px-2.5 py-1 rounded-md text-[12px] font-medium text-white disabled:opacity-50"
            style={{ backgroundColor: mode === 'done' ? 'var(--success-solid)' : 'var(--accent)' }}
          >
            {busy && <Loader2 size={12} className="animate-spin" />}
            {mode === 'done' ? 'Mark done' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}

function AddStep({ release, onAdded }: { release: Release; onAdded: (r: Release) => void }) {
  const [open, setOpen] = useState(false);
  const [title, setTitle] = useState('');
  const [owner, setOwner] = useState('');
  const [description, setDescription] = useState('');
  const [after, setAfter] = useState(release.steps[release.steps.length - 1]?.id ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!open) {
    return (
      <button onClick={() => setOpen(true)} className="inline-flex items-center gap-1 text-[12px] hover:underline" style={{ color: 'var(--accent)' }}>
        <Plus size={13} /> Add a step
      </button>
    );
  }

  const add = async () => {
    setBusy(true);
    setError(null);
    try {
      const saved = await api.addReleaseStep(release.id, {
        title: title.trim(), owner: owner.trim() || undefined, description: description.trim() || undefined, afterStepId: after || undefined,
      });
      onAdded(saved);
      setOpen(false);
      setTitle(''); setOwner(''); setDescription('');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not add the step');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="rounded-lg border p-3 space-y-2" style={{ borderColor: 'var(--border-color)' }}>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
        <label className="flex flex-col gap-1 sm:col-span-2">
          <span className={labelClass} style={labelStyle}>New step</span>
          <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Export data before the deploy" className={inputClass} style={inputStyle} />
        </label>
        <label className="flex flex-col gap-1">
          <span className={labelClass} style={labelStyle}>Owner</span>
          <input value={owner} onChange={(e) => setOwner(e.target.value)} placeholder="DevOps" className={inputClass} style={inputStyle} />
        </label>
      </div>
      <label className="flex flex-col gap-1">
        <span className={labelClass} style={labelStyle}>Description</span>
        <input value={description} onChange={(e) => setDescription(e.target.value)} className={inputClass} style={inputStyle} />
      </label>
      <label className="flex flex-col gap-1">
        <span className={labelClass} style={labelStyle}>After</span>
        <select value={after} onChange={(e) => setAfter(e.target.value)} className={inputClass} style={inputStyle}>
          {release.steps.map((s) => <option key={s.id} value={s.id}>{s.title}</option>)}
        </select>
      </label>
      {error && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{error}</p>}
      <div className="flex justify-end gap-2">
        <button onClick={() => setOpen(false)} className="px-2.5 py-1 rounded-md text-[12px]" style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}>Cancel</button>
        <button
          onClick={() => void add()}
          disabled={busy || !title.trim()}
          className="inline-flex items-center gap-1 px-2.5 py-1 rounded-md text-[12px] font-medium text-white disabled:opacity-50"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          {busy && <Loader2 size={12} className="animate-spin" />} Add step
        </button>
      </div>
    </div>
  );
}

/** Whether a service row still needs someone: not deployed, a failed staging deploy, or a migration still owed. */
function needsAttention(s: ReleaseService): boolean {
  const deployed = s.production?.status === 'succeeded';
  const migrationsOpen = s.migrations.some((m) => !['succeeded', 'skipped', 'superseded'].includes(m.status));
  return !deployed || migrationsOpen || s.staging?.status === 'failed';
}

function ServicesTable({ progress, release }: { progress: ReleaseProgress | null; release: Release }) {
  const [onlyOpen, setOnlyOpen] = useState(false);
  if (!progress) return <div className="skeleton h-40" />;
  const rows = onlyOpen ? progress.services.filter(needsAttention) : progress.services;
  const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap';
  const td = 'px-2 py-2 whitespace-nowrap align-top';
  const muted = { color: 'var(--text-muted)' };
  const deployCell = (d: ReleaseService['staging']) => {
    if (!d) return <span style={muted}>—</span>;
    const ok = d.status === 'succeeded';
    return (
      <span className="inline-flex items-center gap-1">
        <Link to={`/deployments/events/${d.eventId}`} className="hover:underline" title={new Date(d.deployedAt).toLocaleString()}>
          <span className="font-mono">{d.version}</span>{' '}
          <span className="text-[11px]" style={{ color: ok ? 'var(--text-muted)' : d.status === 'failed' ? 'var(--danger)' : 'var(--info)' }}>
            {ok ? fmt(d.deployedAt) : d.status.replace('_', ' ')}
          </span>
        </Link>
        <RunIcon run={d.run} />
      </span>
    );
  };

  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex items-center justify-between mb-2 gap-2 flex-wrap">
        <h2 className="text-[14px] font-semibold">Services <span className="font-normal text-[12px]" style={muted}>({progress.services.length})</span></h2>
        <label className="inline-flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          <input type="checkbox" checked={onlyOpen} onChange={(e) => setOnlyOpen(e.target.checked)} /> Only the ones still open
        </label>
      </div>
      {rows.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
          {progress.services.length === 0
            ? `No service of ${release.products.join(', ')} has a ${release.version} build on ${release.stagingEnvironment} or ${release.productionEnvironment} yet.`
            : 'Every service is approved, deployed and migrated.'}
        </p>
      ) : (
        <div className="overflow-x-auto -mx-2">
          <table className="w-full text-[12px]">
            <thead style={muted}>
              <tr>
                <th className={th}>Service</th>
                <th className={th}><EnvLabel env={release.stagingEnvironment} /></th>
                <th className={th}>Promotion</th>
                <th className={th}><EnvLabel env={release.productionEnvironment} /></th>
                <th className={th}>Migrations</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((s) => (
                <tr key={`${s.product}/${s.service}`} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                  <td className={td}>
                    <Link to={`/deployments/${encodeURIComponent(s.product)}/${encodeURIComponent(s.service)}`} className="hover:underline">
                      <span style={muted}>{s.product} · </span>{s.service}
                    </Link>
                    {s.included && <span className="ml-1.5 text-[10px] px-1 py-0.5 rounded" style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}>also ships</span>}
                  </td>
                  <td className={td}>{deployCell(s.staging)}</td>
                  <td className={td}>
                    {s.promotion ? (
                      <span className="inline-flex items-center gap-1">
                        <Link to={`/promotions/${s.promotion.id}`} className="hover:underline" title={s.promotion.approvedAt ? `Approved ${new Date(s.promotion.approvedAt).toLocaleString()}` : undefined}>
                          <span className="font-mono">{s.promotion.version}</span>{' '}
                          <span className="text-[11px]" style={{ color: promotionColor(s.promotion.status) }}>{s.promotion.status}</span>
                        </Link>
                        {s.promotion.externalRunUrl && (
                          <RunIcon run={{ provider: null, label: 'The run this approval started', url: s.promotion.externalRunUrl }} />
                        )}
                      </span>
                    ) : <span style={muted}>—</span>}
                  </td>
                  <td className={td}>
                    {deployCell(s.production)}
                    {s.productionFailures > 0 && (
                      <span className="ml-1 text-[11px]" style={{ color: 'var(--danger)' }}>
                        {s.productionFailures} failed {s.productionFailures === 1 ? 'attempt' : 'attempts'}
                      </span>
                    )}
                  </td>
                  <td className={td}>
                    {s.migrations.length === 0 ? (
                      <span style={muted}>{s.production?.status === 'succeeded' ? 'none reported' : '—'}</span>
                    ) : (
                      <span className="inline-flex gap-1.5">
                        {s.migrations.map((m) => {
                          const ms = migrationStatusStyle(m.status);
                          return (
                            <span key={m.id} className="inline-flex items-center gap-1">
                              <Link to={migrationPath(m.id)} className="badge hover:opacity-80" style={{ backgroundColor: ms.bg, color: ms.fg }}
                                title={m.failureReason ?? `${m.appliedCount} applied`}>
                                {m.kind === 'schema' ? 'Schema' : 'Data'}: {ms.label}
                              </Link>
                              <RunIcon run={m.run} />
                            </span>
                          );
                        })}
                      </span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/** A small icon link to the pipeline run (or the service's job in it) behind a deploy or migration. */
function RunIcon({ run }: { run: ReleaseRunRef | null }) {
  if (!run) return null;
  return (
    <a
      href={run.url}
      target="_blank"
      rel="noopener noreferrer"
      title={`Open ${run.label}`}
      aria-label={`Open ${run.label}`}
      className="hover:opacity-70"
      style={{ color: 'var(--text-muted)' }}
    >
      <Workflow size={12} />
    </a>
  );
}

/**
 * Everything the release links to, in one place: what the release manager added (the issue-tracker
 * version, the plan, the retro), the pipeline runs the portal read off the deploys and migrations,
 * and the portal's own pages filtered to this release's products and environments.
 */
function LinksSection({ release, progress, onEdit }: { release: Release; progress: ReleaseProgress | null; onEdit?: () => void }) {
  const flags = useFeatureFlagsStore((s) => s.flags);
  const prod = release.productionEnvironment;
  const single = release.products.length === 1 ? release.products[0] : null;
  const productParam = single ? `&product=${encodeURIComponent(single)}` : '';
  const portal: { to: string; label: string }[] = [
    { to: `/promotions?tab=pending&targetEnv=${encodeURIComponent(prod)}${productParam}`, label: 'Pending promotions (Mass approve)' },
    { to: `/promotions?tab=all&targetEnv=${encodeURIComponent(prod)}${productParam}`, label: 'All promotions' },
    { to: `/migrations?env=${encodeURIComponent(prod)}${productParam}&status=all`, label: 'Migrations' },
    ...release.products.map((p) => ({ to: `/deployments/${encodeURIComponent(p)}`, label: `Deployments · ${p}` })),
    ...(flags[FeatureFlag.ReleaseNotes] === false ? [] : release.products.map((p) => ({ to: `/release-notes/${encodeURIComponent(p)}`, label: `Release notes · ${p}` }))),
  ];

  // Every pipeline run any check read, once each: SDP runs, mpt-release workflow runs.
  const runs = [...new Map((progress?.checks ?? []).flatMap((c) => c.runs).map((r) => [r.url, r])).values()];
  const byProvider = runs.reduce<Record<string, number>>((acc, r) => {
    // A producer that sent only a link still says where it ran.
    const azure = r.provider === 'azure-devops' || /[?&]buildId=\d+/.test(r.url);
    const github = r.provider === 'github-actions' || /github\.com\/[^/]+\/[^/]+\/actions\/runs\//.test(r.url);
    const key = azure ? 'Azure DevOps' : github ? 'GitHub Actions' : 'other';
    acc[key] = (acc[key] ?? 0) + 1;
    return acc;
  }, {});

  const chip = 'inline-flex items-center gap-1 px-2 py-0.5 rounded-md text-[12px] border transition-colors hover:border-[var(--accent)]';
  return (
    <section className="rounded-xl border p-4 space-y-3" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }} data-guide-anchor="release-links">
      <div className="flex items-center justify-between">
        <h2 className="text-[14px] font-semibold">Links</h2>
        {onEdit && release.links.length > 0 && (
          <button onClick={onEdit} className="text-[12px] hover:underline" style={{ color: 'var(--accent)' }}>Edit links</button>
        )}
      </div>

      <div className="grid gap-3 lg:grid-cols-3">
        <div className="space-y-1.5">
          <div className={labelClass} style={labelStyle}>Release</div>
          {release.links.length > 0 ? (
            <LinkList links={release.links} />
          ) : (
            <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              No links yet.{' '}
              {onEdit && (
                <button onClick={onEdit} className="hover:underline" style={{ color: 'var(--accent)' }}>
                  Add the Jira version, the release plan and the retro
                </button>
              )}
            </p>
          )}
        </div>

        <div className="space-y-1.5">
          <div className={labelClass} style={labelStyle}>Pipeline runs</div>
          {runs.length === 0 ? (
            <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>None reported yet.</p>
          ) : (
            <p className="text-[12px]" style={{ color: 'var(--text-secondary)' }}>
              {runs.length} {runs.length === 1 ? 'run' : 'runs'} ({Object.entries(byProvider).map(([k, n]) => `${n} ${k}`).join(', ')}),
              listed under the steps they belong to. Each service below links its own staging and production job.
            </p>
          )}
        </div>

        <div className="space-y-1.5">
          <div className={labelClass} style={labelStyle}>In the portal</div>
          <div className="flex flex-wrap gap-1.5">
            {portal.map((l) => (
              <Link key={l.to} to={l.to} className={chip} style={{ borderColor: 'var(--border-color)', color: 'var(--text-primary)' }}>
                {l.label}
              </Link>
            ))}
          </div>
        </div>
      </div>
    </section>
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
