import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  AlertTriangle,
  ArrowLeft,
  CalendarDays,
  ChevronDown,
  CircleCheck,
  CircleDashed,
  CircleDot,
  GitPullRequest,
  Loader2,
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
  ReleaseProgress,
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
import { Dialog } from '@/components/ui/Dialog';
import { DateField } from '@/components/ui/DateField';
import { inputClass, inputStyle, labelClass, labelStyle } from '@/app/settings/formStyles';
import {
  CHECK_LABEL,
  CHECK_STATE,
  STEP_STATUS,
  cleanLinks,
  describeLink,
  describeManagers,
  formatTargetDate,
  fromLocalInput,
  releaseStatusStyle,
  toLocalInput,
} from '@/components/releases/releaseStatus';
import { LinkList, LinksEditor } from '@/components/releases/ReleaseLinks';
import { useFeatureFlagsStore, FeatureFlag } from '@/stores/featureFlagsStore';
import { ReleaseForm } from './ReleaseForm';
import { ReleaseServicesDialog } from './ReleaseServicesDialog';
import { StepTable } from '@/components/releases/StepTable';

const fmt = (iso: string | null | undefined) =>
  iso
    ? new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
    : null;

/**
 * Which steps this viewer opened or folded on a release, kept in this browser. Steps they haven't
 * touched follow the default (see `isExpanded` in the page).
 */
function useStepExpansion(releaseId: string) {
  const key = `releases.expandedSteps.${releaseId}`;
  const [stored, setStored] = useState(() => ({ key, overrides: readExpansion(key) }));
  // Another release opened in the same page: read its own choices.
  if (stored.key !== key) setStored({ key, overrides: readExpansion(key) });
  const set = useCallback((changes: Record<string, boolean>) => {
    setStored((prev) => {
      const overrides = { ...prev.overrides, ...changes };
      try {
        window.localStorage.setItem(prev.key, JSON.stringify(overrides));
      } catch {
        // No storage (private window, blocked site data): the choice lasts for this visit.
      }
      return { key: prev.key, overrides };
    });
  }, []);
  return [stored.overrides, set] as const;
}

function readExpansion(key: string): Record<string, boolean> {
  try {
    const parsed: unknown = JSON.parse(window.localStorage.getItem(key) ?? 'null');
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, boolean>) : {};
  } catch {
    return {};
  }
}

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
 * Steps are moved on by a release manager — the checks inform that call, they don't make it — and
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
  const [choosingServices, setChoosingServices] = useState(false);
  // Bumped after a write that changes what the checks measure, so the evidence is read again.
  const [reread, setReread] = useState(0);
  const [managers, setManagers] = useState<string | null>(null);
  const [expanded, setExpanded] = useStepExpansion(id);

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
  }, [id, releaseTick, recordsTick, reread]);

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
  // An optional step nobody has started (a maintenance page the release doesn't need) holds nothing:
  // it isn't counted, and isn't the step the release is on.
  const counted = release.steps.filter((s) => !s.optional || s.status !== 'pending');
  const openSteps = counted.filter((s) => s.status === 'pending' || s.status === 'in_progress').length;
  // The step the release is on: the first one still open. It gets the prominent actions.
  const currentStepId = counted.find((s) => s.status === 'pending' || s.status === 'in_progress')?.id;
  // Open by default: the step the release is on and any step under way. Finished and upcoming steps
  // fold to their header, which still shows their check's progress. A viewer's own choice wins.
  const isExpanded = (s: ReleaseStep) => expanded[s.id] ?? (s.id === currentStepId || s.status === 'in_progress');
  const anyExpanded = release.steps.some(isExpanded);
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
              {managers && <span>Release managers: {managers}</span>}
            </p>
            <p className="text-[11px] mt-1" style={{ color: 'var(--text-muted)' }}>
              {release.startedAt && <>Started {fmt(release.startedAt)}. </>}
              {release.completedAt && <>Released {fmt(release.completedAt)}. </>}
              {release.cancelledAt && <>Cancelled {fmt(release.cancelledAt)}{release.cancelReason ? `: ${release.cancelReason}` : ''}. </>}
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

      <OverviewTiles progress={progress} release={release} onServices={progress ? () => setChoosingServices(true) : undefined} />
      {progressError && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{progressError}</p>}

      <LinksSection release={release} progress={progress} onEdit={canManage ? () => setEditing(true) : undefined} />

      <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
        <div className="flex items-center justify-between mb-3">
          <h2 className="text-[14px] font-semibold">Steps</h2>
          <span className="inline-flex items-center gap-3 text-[12px]">
            <span style={{ color: 'var(--text-muted)' }}>{counted.length - openSteps} of {counted.length} closed</span>
            <button
              className="hover:underline"
              style={{ color: 'var(--accent)' }}
              onClick={() => setExpanded(Object.fromEntries(release.steps.map((s) => [s.id, !anyExpanded])))}
            >
              {anyExpanded ? 'Collapse all' : 'Expand all'}
            </button>
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
              expanded={isExpanded(step)}
              onToggle={() => setExpanded({ [step.id]: !isExpanded(step) })}
              canManage={canManage}
              productionEnv={release.productionEnvironment}
              products={release.products}
              progress={progress}
              release={release}
              onUpdate={(update) => updateStep(step, update)}
              onDelete={step.custom ? async () => setRelease(await api.deleteReleaseStep(release.id, step.id)) : undefined}
            />
          ))}
        </ol>
        {canManage && <AddStep release={release} onAdded={setRelease} />}
      </section>

      {(release.notes || release.excludedServices.length > 0) && (
        <section className="rounded-xl border p-4 space-y-2 text-[13px]" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
          <h2 className="text-[14px] font-semibold">Notes and scope</h2>
          {release.notes && <p className="whitespace-pre-wrap" style={{ color: 'var(--text-secondary)' }}>{release.notes}</p>}
          {release.excludedServices.length > 0 && (
            <p style={{ color: 'var(--text-secondary)' }}>
              <b>Not shipping:</b> {release.excludedServices.map((k) => k.split('/').slice(1).join('/')).join(', ')}{' '}
              <button onClick={() => setChoosingServices(true)} className="hover:underline" style={{ color: 'var(--accent)' }}>Change</button>
            </p>
          )}
        </section>
      )}

      {choosingServices && progress && (
        <ReleaseServicesDialog
          release={release}
          progress={progress}
          onClose={() => setChoosingServices(false)}
          onSaved={(saved) => { setRelease(saved); setChoosingServices(false); setReread((n) => n + 1); }}
        />
      )}

      {editing && (
        <ReleaseForm release={release} onClose={() => setEditing(false)} onSaved={(saved) => { setRelease(saved); setEditing(false); }} />
      )}

      {confirm === 'complete' && (
        <CompleteReleaseDialog
          release={release}
          openSteps={openSteps}
          busy={busy}
          error={actionError}
          onConfirm={(completedAt) => void runAction(() => api.completeRelease(release.id, completedAt))}
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

/** Mark the release released, at a time that defaults to now — or the day it really went out. */
function CompleteReleaseDialog({
  release,
  openSteps,
  busy,
  error,
  onConfirm,
  onCancel,
}: {
  release: Release;
  openSteps: number;
  busy: boolean;
  error: string | null;
  onConfirm: (completedAt: string | undefined) => void;
  onCancel: () => void;
}) {
  const [when, setWhen] = useState(nowLocal());
  return (
    <Dialog onClose={onCancel} ariaLabel={`Mark ${release.title} as released`} width={460}>
      <div className="px-5 py-4 space-y-3">
        <h2 className="text-[15px] font-semibold">Mark {release.title} as released?</h2>
        <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
          {openSteps > 0
            ? `${openSteps} ${openSteps === 1 ? 'step is' : 'steps are'} still open. The release is closed as it stands; you can reopen it later.`
            : 'Every step is closed. Remember to mark the version released in the issue tracker.'}
        </p>
        <label className="flex flex-col gap-1">
          <span className={labelClass} style={labelStyle}>Released</span>
          <DateField type="datetime-local" value={when} onChange={setWhen} className={inputClass} style={inputStyle} ariaLabel="Released" />
        </label>
        {error && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{error}</p>}
        <div className="flex justify-end gap-2">
          <button onClick={onCancel} className="px-3 py-1.5 rounded-lg text-[13px]" style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}>
            Cancel
          </button>
          <button
            onClick={() => onConfirm(fromLocalInput(when) ?? undefined)}
            disabled={busy}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium text-white disabled:opacity-50"
            style={{ backgroundColor: 'var(--success-solid)' }}
          >
            {busy && <Loader2 size={13} className="animate-spin" />} Mark released
          </button>
        </div>
      </div>
    </Dialog>
  );
}

function BackLink() {
  return (
    <Link to="/releases" className="inline-flex items-center gap-1 text-[12px] hover:underline" style={{ color: 'var(--text-muted)' }}>
      <ArrowLeft size={13} /> Releases
    </Link>
  );
}

function OverviewTiles({ progress, release, onServices }: { progress: ReleaseProgress | null; release: Release; onServices?: () => void }) {
  const get = (key: string) => progress?.checks.find((c) => c.key === key);
  const shipping = progress?.services.filter((s) => !s.excluded).length ?? 0;
  const all = progress?.services.length ?? 0;
  const tiles: { label: string; value: string; sub?: string; check?: ReleaseCheck; onClick?: () => void }[] = [
    {
      label: 'Services',
      value: progress ? String(shipping) : '…',
      sub: !progress ? undefined
        : shipping === all ? `all of ${release.products.join(', ')} · ${release.canManage ? 'choose' : 'see the list'}`
        : `of ${all} · ${all - shipping} not shipping · ${release.canManage ? 'choose' : 'see the list'}`,
      onClick: onServices,
    },
    tile('Approved', get('production-promotions')),
    tile('Deployed', get('production-deploys')),
    tile('Migrations finished', get('production-migrations')),
  ];
  return (
    <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
      {tiles.map((t) => {
        const tone = t.check ? CHECK_STATE[t.check.state] : null;
        const body = (
          <>
            <div className={labelClass} style={labelStyle}>{t.label}</div>
            <div className="text-[20px] font-semibold mt-0.5" style={{ color: tone?.fg ?? 'var(--text-primary)' }}>{t.value}</div>
            {t.sub && <div className="text-[11px]" style={{ color: t.onClick ? 'var(--accent)' : 'var(--text-muted)' }}>{t.sub}</div>}
          </>
        );
        const box = 'rounded-xl border px-4 py-3 text-left';
        const style = { borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' };
        return t.onClick ? (
          <button key={t.label} onClick={t.onClick} className={`${box} transition-colors hover:border-[var(--accent)]`} style={style}
            data-guide-anchor="release-services">
            {body}
          </button>
        ) : (
          <div key={t.label} className={box} style={style}>{body}</div>
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
  expanded,
  onToggle,
  canManage,
  productionEnv,
  products,
  progress,
  release,
  onUpdate,
  onDelete,
}: {
  step: ReleaseStep;
  last: boolean;
  check: ReleaseCheck | undefined;
  current: boolean;
  expanded: boolean;
  onToggle: () => void;
  canManage: boolean;
  productionEnv: string;
  products: string[];
  progress: ReleaseProgress | null;
  release: Release;
  onUpdate: (update: ReleaseStepUpdate) => Promise<void>;
  onDelete?: () => Promise<void>;
}) {
  const [mode, setMode] = useState<null | EditorMode>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const Icon = STEP_ICON[step.status];
  const status = STEP_STATUS[step.status];
  // A folded step opens with a click anywhere on it, not just its title.
  const opensOnClick = !expanded && mode === null;
  // Clicks on blank space toggle; the step's buttons and links keep doing their own thing, and
  // selecting text isn't a click.
  const toggleOnBlank = (e: { target: EventTarget }) => {
    if ((e.target as HTMLElement).closest('button, a, input, textarea, select, label')) return;
    if (window.getSelection()?.toString()) return;
    onToggle();
  };

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
    <li
      className={`relative isolate pl-8 pb-5 ${opensOnClick ? 'group/step cursor-pointer' : ''}`}
      data-step-key={step.key}
      onClick={opensOnClick ? toggleOnBlank : undefined}
    >
      {opensOnClick && (
        <span
          aria-hidden
          className="pointer-events-none absolute -left-2 -right-2 -top-1.5 bottom-3 -z-10 rounded-lg opacity-0 transition-opacity group-hover/step:opacity-100"
          style={{ backgroundColor: 'var(--bg-secondary)' }}
        />
      )}
      {!last && <span className="absolute left-[9px] top-6 bottom-0 w-px" style={{ backgroundColor: 'var(--border-color)' }} />}
      <Icon size={19} className="absolute left-0 top-0.5" style={{ color: status.fg }} aria-label={status.label} />

      {/* An open step folds from its header: the title, or any blank space around it. */}
      <div
        className={`flex items-start justify-between gap-3 flex-wrap ${opensOnClick ? '' : 'relative group/head cursor-pointer'}`}
        onClick={opensOnClick ? undefined : toggleOnBlank}
      >
        {!opensOnClick && (
          <span
            aria-hidden
            className="pointer-events-none absolute -left-10 -right-2 -top-1.5 -bottom-1.5 -z-10 rounded-lg opacity-0 transition-opacity group-hover/head:opacity-100"
            style={{ backgroundColor: 'var(--bg-secondary)' }}
          />
        )}
        <div className="min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <button
              type="button"
              onClick={onToggle}
              aria-expanded={expanded}
              title={expanded ? 'Collapse' : 'Expand'}
              className="inline-flex items-center gap-1 text-left text-[14px] font-medium hover:underline"
              style={{ color: step.status === 'skipped' ? 'var(--text-muted)' : 'var(--text-primary)', textDecoration: step.status === 'skipped' ? 'line-through' : undefined }}
            >
              {step.title}
              <ChevronDown size={14}
                className="shrink-0 transition-transform text-[var(--text-muted)] group-hover/step:text-[var(--accent)] group-hover/head:text-[var(--accent)]"
                style={{ transform: expanded ? undefined : 'rotate(-90deg)' }} />
            </button>
            {step.owner && (
              <span className="text-[11px] px-1.5 py-0.5 rounded" style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}>{step.owner}</span>
            )}
            {step.optional && (
              <span className="text-[11px] px-1.5 py-0.5 rounded" style={{ border: '1px dashed var(--border-color)', color: 'var(--text-muted)' }}
                title="Only for a release that needs it. Skip it otherwise: while it's still to do it doesn't count.">
                Optional
              </span>
            )}
            <span className="text-[11px] font-medium" style={{ color: status.fg }}>{status.label}</span>
            {check && <CheckProgress check={check} />}
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
                <button className={small} style={outline} disabled={busy} onClick={() => setMode('skip')}>Skip</button>
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

      {/* Shown folded too: a warning is what someone scanning the steps has to see. */}
      {check && <CheckWarnings check={check} />}

      {expanded && <>

      {step.key === 'production-promotions' && step.status !== 'done' && step.status !== 'skipped' && (
        <Link to={promotionsLink} className="inline-flex items-center gap-1 text-[12px] mt-1.5 hover:underline" style={{ color: 'var(--accent)' }}>
          <GitPullRequest size={12} /> Open the pending promotions (Mass approve is there)
        </Link>
      )}

      {check && <CheckSummary check={check} />}
      {check && progress && <StepTable check={check} progress={progress} release={release} />}

      {(step.note || step.links.length > 0) && mode !== 'edit' && (
        <div className="mt-2 text-[12px] rounded-lg px-3 py-2 space-y-1.5" style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}>
          {step.note && <p className="whitespace-pre-wrap">{step.note}</p>}
          <LinkList links={step.links} />
        </div>
      )}
      </>}

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

/**
 * A check's progress in its step's header: a bar, done of total, and the state — so the step reads at a
 * glance without a box of its own.
 */
function CheckProgress({ check }: { check: ReleaseCheck }) {
  const tone = CHECK_STATE[check.state];
  const pct = check.total === 0 ? 0 : Math.round((check.done / check.total) * 100);
  return (
    <span className="inline-flex items-center gap-1.5" title={`Portal check · ${CHECK_LABEL[check.key]}`}>
      {check.total > 0 && (
        <span className="inline-block w-24 h-1.5 rounded-full overflow-hidden" style={{ backgroundColor: 'var(--bg-secondary)' }}>
          <span className="block h-full rounded-full" style={{ width: `${pct}%`, backgroundColor: check.state === 'complete' ? 'var(--success)' : 'var(--accent)' }} />
        </span>
      )}
      {check.total > 0 && <span className="text-[11px] tabular-nums" style={{ color: 'var(--text-secondary)' }}>{check.done}/{check.total}</span>}
      <span className="badge" style={{ backgroundColor: tone.bg, color: tone.fg }}>{tone.label}</span>
    </span>
  );
}

/** What the check found, in one line under the step's description: the summary and when it happened. */
function CheckSummary({ check }: { check: ReleaseCheck }) {
  const range = check.firstAt && check.lastAt && check.firstAt !== check.lastAt
    ? `${fmt(check.firstAt)} – ${fmt(check.lastAt)}`
    : fmt(check.lastAt ?? check.firstAt);
  return (
    <p className="text-[12px] mt-1" style={{ color: 'var(--text-secondary)' }} data-check={check.key}>
      {check.summary}
      {range && <span style={{ color: 'var(--text-muted)' }}> · {range}</span>}
    </p>
  );
}

/** What the check wants a person to know beyond its numbers — e.g. data migrations held back by a failed deploy. */
function CheckWarnings({ check }: { check: ReleaseCheck }) {
  if (!check.warnings?.length) return null;
  return (
    <div className="mt-1.5 space-y-1" data-check-warnings={check.key}>
      {check.warnings.map((w) => (
        <p key={w} role="status" className="flex items-start gap-1.5 text-[12px] rounded-md px-2 py-1.5"
          style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--text-primary)' }}>
          <AlertTriangle size={13} className="shrink-0 mt-px" style={{ color: 'var(--warning)' }} />
          <span>{w}</span>
        </p>
      ))}
    </div>
  );
}

/** What the step editor is for: closing the step (with when it happened), or editing it. Start needs no form: it starts the step now. */
type EditorMode = 'done' | 'skip' | 'edit';

const EDITOR_ACTION: Record<EditorMode, { label: string; color: string }> = {
  done: { label: 'Mark done', color: 'var(--success-solid)' },
  skip: { label: 'Skip', color: 'var(--accent)' },
  edit: { label: 'Save', color: 'var(--accent)' },
};

const nowLocal = () => toLocalInput(new Date().toISOString());

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
  mode: EditorMode;
  check: ReleaseCheck | undefined;
  busy: boolean;
  error: string | null;
  onCancel: () => void;
  onSave: (update: ReleaseStepUpdate) => void;
  onDelete?: () => void;
}) {
  const [note, setNote] = useState(step.note ?? '');
  const [links, setLinks] = useState<ReleaseLink[]>(step.links.length ? step.links : [{ label: null, url: '' }]);
  // Closing a step asks when it happened, now by default — a release can be written down after
  // the fact. Editing shows the dates the step already has.
  const [startedAt, setStartedAt] = useState(toLocalInput(step.startedAt));
  const [completedAt, setCompletedAt] = useState(
    toLocalInput(step.completedAt) || (mode === 'done' || mode === 'skip' ? nowLocal() : ''));
  const [title, setTitle] = useState(step.title);
  const showStarted = mode === 'done' || (mode === 'edit' && step.status !== 'pending' && step.status !== 'skipped');
  const showCompleted = mode === 'done' || mode === 'skip' || (mode === 'edit' && (step.status === 'done' || step.status === 'skipped'));
  const completedLabel = mode === 'skip' || (mode === 'edit' && step.status === 'skipped') ? 'Skipped' : 'Done';
  const action = EDITOR_ACTION[mode];

  const save = () => {
    const update: ReleaseStepUpdate = { note, links: cleanLinks(links) };
    if (mode === 'done') update.status = 'done';
    if (mode === 'skip') update.status = 'skipped';
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
            <DateField type="datetime-local" value={startedAt} onChange={setStartedAt} className={inputClass} style={inputStyle} ariaLabel="Started" />
          </label>
        )}
        {showCompleted && (
          <label className="flex flex-col gap-1">
            <span className={labelClass} style={labelStyle}>{completedLabel}</span>
            <DateField type="datetime-local" value={completedAt} onChange={setCompletedAt} className={inputClass} style={inputStyle} ariaLabel={completedLabel} />
          </label>
        )}
      </div>
      {mode === 'edit' && step.status === 'pending' && (
        <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>Start it or mark it done to record when it happened.</p>
      )}
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
            style={{ backgroundColor: action.color }}
          >
            {busy && <Loader2 size={12} className="animate-spin" />}
            {action.label}
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

function runHost(url: string): string | null {
  try { return new URL(url).hostname.toLowerCase(); } catch { return null; }
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
    // A producer that sent only a link still says where it ran: read it off the parsed address.
    const kind = describeLink(r.url);
    const github = r.provider === 'github-actions' || (kind.kind === 'run' && runHost(r.url) === 'github.com');
    const azure = r.provider === 'azure-devops' || (kind.kind === 'run' && !github);
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
              behind this release. In the step tables each service links its own build, deployment and migration runs.
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
