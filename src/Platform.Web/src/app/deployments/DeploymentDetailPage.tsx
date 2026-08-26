import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { format, formatDistanceToNow } from 'date-fns';
import {
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  Clock,
  ExternalLink,
  FileCode2,
  GitBranch,
  GitCommitHorizontal,
  GitPullRequest,
  History,
  Loader2,
  PlayCircle,
  PlusCircle,
  ScrollText,
  Ticket,
  Undo2,
  User,
  Users,
  Workflow,
} from 'lucide-react';
import { api } from '@/lib/api';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { CopyEmailButton } from '@/components/deployments/CopyEmailButton';
import { DeploymentLogViewer } from '@/components/deployments/DeploymentLogViewer';
import { useSettingsStore } from '@/stores/settingsStore';
import { useFeatureFlagsStore, FeatureFlag } from '@/stores/featureFlagsStore';
import { useAuthStore } from '@/stores/authStore';
import { deploymentHistoryPath } from '@/lib/deploymentPath';
import { resolveReferenceHref } from '@/lib/refUrl';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { useDocumentTitle } from '@/lib/pageTitle';
import { ROW_ACTION_ATTR } from '@/lib/keys';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import { providerLabel, workItemDetailPath } from '@/lib/workItem';
import { roleDisplay } from '@/lib/roleLabel';
import { collectParticipants } from '@/lib/types';
import type {
  DeployParticipant,
  DeployReference,
  DeployRun,
  DeploymentDetail,
  RelatedPromotion,
  RelatedWorkItem,
} from '@/lib/types';

/**
 * Deployment detail page — everything about one deploy event in one place.
 *
 * This exists because the drawer on the deployments matrix answers "what version is live" and
 * nothing else. The questions that actually arrive when a deployment goes wrong — what created it,
 * what did it print, which line failed, what was in it, where does it go next — each needed a
 * different tool, or the pipeline log, or a person who remembered. They're all here now, ordered by
 * how urgently they're asked: the failure and its logs first, then provenance, then what the
 * deployment carried and what it feeds.
 *
 * Everything renders from a single `GET /api/deployments/events/{id}`; log text is the one exception,
 * fetched per block on expand because a Helm printout is too large to send speculatively.
 */

const STATUS_STYLES: Record<string, { bg: string; fg: string; label: string }> = {
  succeeded: { bg: 'var(--success-bg)', fg: 'var(--success)', label: 'Succeeded' },
  failed: { bg: 'var(--danger-bg)', fg: 'var(--danger)', label: 'Failed' },
  in_progress: { bg: 'var(--warning-bg)', fg: 'var(--warning)', label: 'In Progress' },
};

const PROMOTION_STATUS_COLOR: Record<string, string> = {
  Pending: 'var(--warning)',
  Approved: 'var(--info)',
  Deploying: 'var(--accent)',
  Deployed: 'var(--success)',
  Superseded: 'var(--text-muted)',
  Rejected: 'var(--danger)',
};

const REFERENCE_ICONS: Record<string, typeof ExternalLink> = {
  pipeline: Workflow,
  repository: GitBranch,
  'pull-request': GitPullRequest,
  'work-item': Ticket,
  commit: GitCommitHorizontal,
  'build-manifest': FileCode2,
};

export function DeploymentDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [searchParams] = useSearchParams();
  const [detail, setDetail] = useState<DeploymentDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showManualForm, setShowManualForm] = useState(false);

  const promotionsEnabled = useFeatureFlagsStore((s) => s.isEnabled(FeatureFlag.Promotions));
  const rollbacksEnabled = useFeatureFlagsStore((s) => s.isEnabled(FeatureFlag.Rollbacks));
  const isAdmin = useAuthStore((s) => s.user?.isAdmin ?? false);

  const load = useCallback(async () => {
    if (!id) return;
    setLoading(true);
    setError(null);
    try {
      setDetail(await api.getDeploymentEvent(id));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load this deployment');
    } finally {
      setLoading(false);
    }
  }, [id]);

  // Enrichment fills in work-item titles minutes after ingest, and linked promotions/rollbacks
  // move while this page is open — follow this event's own updates.
  const realtimeTick = useEntityRefresh(['deployment'], {
    filter: (evt) => !evt.id || evt.id === id,
  });

  useEffect(() => { void load(); }, [load, realtimeTick]);

  // Before the early returns below, so the hook order is the same on every render. A deploy event is
  // the most-pasted link in the app ("this is the one that failed"), and until it loads the id is
  // still a truthful — if unlovely — thing to show.
  const titled = detail?.event;
  useDocumentTitle([
    titled ? `${titled.service} ${titled.version}` : id,
    titled?.environment,
    'Deployment',
  ]);

  if (loading) {
    return (
      <div className="flex items-center justify-center py-24">
        <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
      </div>
    );
  }

  if (error || !detail) {
    return (
      <div className="space-y-4">
        <BackLink to="/deployments" label="Deployments" />
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{ backgroundColor: 'var(--danger-bg)', borderColor: 'var(--danger)', color: 'var(--danger)' }}
        >
          <AlertTriangle size={18} />
          <span className="text-[13px] font-medium">{error ?? 'Deployment not found'}</span>
        </div>
      </div>
    );
  }

  const { event: evt, logs, history, promotions, workItems } = detail;
  const failed = evt.status === 'failed';
  const status = STATUS_STYLES[evt.status] ?? STATUS_STYLES.succeeded;

  // The release repository's record of what this version is built from. The version number hangs off
  // this link, so "which chart and images is v6.0.14 actually made of" is one click from the header.
  const manifest = evt.references.find((r) => r.type === 'build-manifest');

  // `from` lets a reader who arrived from a promotion or work item get back where they were instead
  // of into the deployments matrix. It rides in the URL so a refresh keeps the trail.
  const backTo = searchParams.get('from') ?? `/deployments/${encodeURIComponent(evt.product)}`;
  const backLabel = searchParams.get('fromLabel') ?? evt.product;

  return (
    <div className="space-y-6">
      <BackLink to={backTo} label={backLabel} />

      {/* Header */}
      <div className="flex items-start justify-between gap-4 flex-wrap">
        <div className="min-w-0">
          <div className="flex items-center gap-2.5 flex-wrap">
            <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
              {evt.service}
            </h1>
            <EnvBadge env={evt.environment} />
            <span className="badge" style={{ backgroundColor: status.bg, color: status.fg }}>
              <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ backgroundColor: status.fg }} />
              {status.label}
            </span>
            {evt.isRollback && (
              <span className="badge" style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}>
                <Undo2 size={10} /> Rollback
              </span>
            )}
          </div>
          <div className="flex items-center gap-2 flex-wrap mt-1.5 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
            <VersionLink version={evt.version} manifest={manifest} />
            {evt.previousVersion && (
              <>
                <span style={{ color: 'var(--text-muted)' }}>from</span>
                <span className="font-mono" style={{ color: 'var(--text-muted)' }}>v{evt.previousVersion}</span>
              </>
            )}
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <span className="inline-flex items-center gap-1" title={format(new Date(evt.deployedAt), 'PPpp')}>
              <Clock size={12} />
              {formatDistanceToNow(new Date(evt.deployedAt), { addSuffix: true })}
            </span>
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <span>{evt.product}</span>
          </div>
        </div>

        <div className="flex items-center gap-2 shrink-0">
          {/* The run that created this deployment — the first thing anyone asks for on a failure, so
              it's a button rather than a line in a details list. */}
          <RunLink run={evt.run} />
          <Link
            to={deploymentHistoryPath(evt.product, evt.service, evt.environment)}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
            style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}
          >
            <History size={13} /> Full history
          </Link>
          {rollbacksEnabled && (
            <Link
              to={`/rollbacks?new=1&product=${encodeURIComponent(evt.product)}&targetEnv=${encodeURIComponent(evt.environment)}&service=${encodeURIComponent(evt.service)}`}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
              style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}
            >
              <Undo2 size={13} /> Roll back
            </Link>
          )}
          {/* Admin-only. Records a NEW deployment based on this one, attributed to the signed-in user
              rather than to CI — the only way to reflect something done by hand. It used to live in the
              deployments drawer; this page replaced that drawer, so the action moved with it. */}
          {isAdmin && (
            <button
              onClick={() => setShowManualForm((v) => !v)}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
              style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}
            >
              <PlusCircle size={13} /> Manual deploy
            </button>
          )}
        </div>
      </div>

      {isAdmin && showManualForm && (
        <ManualDeployCard
          event={evt}
          onDone={() => { setShowManualForm(false); void load(); }}
          onCancel={() => setShowManualForm(false)}
        />
      )}

      {/* The specific error, as the pipeline itself named it — above everything else, because on a
          failed deployment this one sentence is what the page is for. */}
      {failed && <FailureCallout run={evt.run} hasLogs={logs.length > 0} />}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-4">
          {/* Logs sit at the top of the main column on a failure and just below the fold otherwise;
              rather than reorder the DOM, failed deployments open their blocks on arrival. */}
          <Card title="Pipeline output" icon={ScrollText}>
            <DeploymentLogViewer eventId={evt.id} logs={logs} defaultOpen={failed} />
          </Card>

          <CreationCard event={evt} />

          {evt.references.length > 0 && <ReferencesCard references={evt.references} />}

          <PeopleCard participants={collectParticipants(evt)} />
        </div>

        <div className="space-y-4">
          {promotionsEnabled && (
            <PromotionsCard promotions={promotions} environment={evt.environment} />
          )}
          {promotionsEnabled && (
            <WorkItemsCard
              workItems={workItems}
              product={evt.product}
              environment={evt.environment}
            />
          )}
          <HistoryCard
            history={history}
            currentId={evt.id}
            product={evt.product}
            service={evt.service}
          />
        </div>
      </div>
    </div>
  );
}

// ── Header pieces ─────────────────────────────────────────────────────

function BackLink({ to, label }: { to: string; label: string }) {
  return (
    <Link
      to={to}
      className="inline-flex items-center gap-1.5 text-[13px] transition-opacity hover:opacity-80"
      style={{ color: 'var(--text-muted)' }}
    >
      <ArrowLeft size={14} /> {label}
    </Link>
  );
}

/**
 * The deployed version, linked to the release repository's build manifest for it when the producer
 * sent one. That file is the authoritative answer to "what is this version made of" — chart version,
 * container images, source revision — and it's pinned to a commit, so the link keeps showing the
 * manifest as it was when this deployment ran rather than whatever it says today.
 */
function VersionLink({ version, manifest }: { version: string; manifest: DeployReference | undefined }) {
  if (!manifest?.url) {
    return <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>v{version}</span>;
  }
  return (
    <a
      href={manifest.url}
      target="_blank"
      rel="noopener noreferrer"
      className="inline-flex items-center gap-1 font-mono font-medium hover:underline"
      style={{ color: 'var(--accent)' }}
      title={`Open the build manifest for v${version}${manifest.revision ? ` (pinned to ${manifest.revision.slice(0, 8)})` : ''}`}
    >
      v{version}
      <FileCode2 size={12} />
    </a>
  );
}

/** Prefers the job deep link — in a fan-out run, only one leg deployed this component. */
function runHref(run: DeployRun | null | undefined): string | null {
  return run?.jobUrl ?? run?.runUrl ?? null;
}

function RunLink({ run }: { run: DeployRun | null | undefined }) {
  const href = runHref(run);
  if (!href) return null;
  const label = providerLabel(run?.provider, 'pipeline');
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
      style={{ backgroundColor: 'var(--accent)', color: '#fff' }}
      // `o` opens the run. The one unambiguous "go and look at the source" action on this page, which
      // is what makes the page-level fallback in invokeRowAction safe to rely on here.
      {...{ [ROW_ACTION_ATTR]: 'open-external' }}
      title={run?.jobUrl ? `Open this component's job in ${label}` : `Open the run in ${label}`}
    >
      <PlayCircle size={13} />
      {run?.runNumber ? `Run #${run.runNumber}` : `View ${label} run`}
      <ExternalLink size={11} />
    </a>
  );
}

/**
 * The failure, stated once, prominently. `failureReason` is the line the deploying pipeline itself
 * identified as the cause — not something inferred here — so it's quoted verbatim. When the producer
 * didn't send one, the callout says so and points at the logs rather than inventing a reason.
 */
function FailureCallout({ run, hasLogs }: { run: DeployRun | null | undefined; hasLogs: boolean }) {
  const reason = run?.failureReason?.trim();
  const href = runHref(run);

  return (
    <div
      className="rounded-xl border p-4 space-y-2"
      style={{ backgroundColor: 'var(--danger-bg)', borderColor: 'var(--danger)' }}
    >
      <div className="flex items-center gap-2">
        <AlertTriangle size={16} style={{ color: 'var(--danger)' }} />
        <h2 className="text-[13px] font-semibold" style={{ color: 'var(--danger)' }}>
          This deployment failed
        </h2>
      </div>
      {reason ? (
        <p
          className="font-mono text-[12.5px] leading-relaxed whitespace-pre-wrap break-words"
          style={{ color: 'var(--text-primary)' }}
        >
          {reason}
        </p>
      ) : (
        <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
          The pipeline didn't report a specific cause.
          {hasLogs ? ' The highlighted lines in the output below are the place to start.' : ''}
        </p>
      )}
      {href && (
        <a
          href={href}
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center gap-1.5 text-[12px] font-medium hover:underline"
          style={{ color: 'var(--danger)' }}
        >
          Open the failing run <ExternalLink size={11} />
        </a>
      )}
    </div>
  );
}

// ── Cards ─────────────────────────────────────────────────────────────

function Card({ title, icon: Icon, children, action }: {
  title: string;
  icon: typeof ExternalLink;
  children: React.ReactNode;
  action?: React.ReactNode;
}) {
  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-center justify-between gap-2 mb-3">
        <h2
          className="text-[11px] font-semibold uppercase tracking-wider flex items-center gap-1.5"
          style={{ color: 'var(--text-muted)' }}
        >
          <Icon size={12} /> {title}
        </h2>
        {action}
      </div>
      {children}
    </div>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-4 text-[13px] py-1">
      <span className="shrink-0" style={{ color: 'var(--text-muted)' }}>{label}</span>
      <span className="text-right min-w-0 break-words" style={{ color: 'var(--text-secondary)' }}>{children}</span>
    </div>
  );
}

/**
 * Records a new deployment by hand, based on this one — changing only version and status, with the
 * server stamping `source="manual"` and the caller as `triggered-by` so it can never be mistaken for a
 * CI report. The note is required: a manual entry without a reason is a mystery to whoever reads the
 * history next.
 *
 * Admin-only, and gated again server-side; this form is a convenience, not the authorisation.
 */
function ManualDeployCard({ event: evt, onDone, onCancel }: {
  event: DeploymentDetail['event'];
  onDone: () => void;
  onCancel: () => void;
}) {
  const [version, setVersion] = useState(evt.version);
  const [status, setStatus] = useState(evt.status ?? 'succeeded');
  const [note, setNote] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const canSubmit = version.trim().length > 0 && note.trim().length > 0 && !saving;

  const submit = async () => {
    setSaving(true);
    setError(null);
    try {
      await api.createManualDeploy({
        product: evt.product,
        service: evt.service,
        environment: evt.environment,
        version: version.trim(),
        status: status.trim() || undefined,
        note: note.trim(),
      });
      onDone();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create manual deployment');
    } finally {
      setSaving(false);
    }
  };

  const fieldStyle = {
    borderColor: 'var(--border-color)',
    backgroundColor: 'var(--bg-secondary)',
    color: 'var(--text-primary)',
  };

  return (
    <Card title="Manual deployment" icon={PlusCircle}>
      <p className="text-[12px] mb-3" style={{ color: 'var(--text-muted)' }}>
        Records a <b>new</b> deployment of {evt.service} to <EnvLabelInline env={evt.environment} />,
        attributed to you rather than to CI. A note is required.
      </p>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <label className="block text-[12px]" style={{ color: 'var(--text-muted)' }}>
          Version
          <input
            value={version}
            onChange={(e) => setVersion(e.target.value)}
            className="mt-1 w-full rounded-lg border px-3 py-1.5 text-[13px] font-mono"
            style={fieldStyle}
          />
        </label>
        <label className="block text-[12px]" style={{ color: 'var(--text-muted)' }}>
          Status
          <select
            value={status}
            onChange={(e) => setStatus(e.target.value)}
            className="mt-1 w-full rounded-lg border px-3 py-1.5 text-[13px]"
            style={fieldStyle}
          >
            {/* The three the server accepts, so a typo can't produce a 400. */}
            <option value="succeeded">Succeeded</option>
            <option value="failed">Failed</option>
            <option value="in_progress">In progress</option>
          </select>
        </label>
      </div>
      <label className="block text-[12px] mt-3" style={{ color: 'var(--text-muted)' }}>
        Note (required)
        <textarea
          value={note}
          onChange={(e) => setNote(e.target.value)}
          rows={2}
          placeholder="Why are you recording this by hand?"
          className="mt-1 w-full rounded-lg border px-3 py-1.5 text-[13px] resize-none"
          style={fieldStyle}
        />
      </label>
      {error && (
        <p className="text-[12px] mt-2" style={{ color: 'var(--danger)' }}>{error}</p>
      )}
      <div className="flex items-center gap-2 mt-3">
        <button
          onClick={submit}
          disabled={!canSubmit}
          title={note.trim().length === 0 ? 'A note is required' : undefined}
          className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity"
          style={{
            backgroundColor: 'var(--accent)',
            color: '#fff',
            opacity: canSubmit ? 1 : 0.5,
            cursor: canSubmit ? 'pointer' : 'not-allowed',
          }}
        >
          <PlusCircle size={13} />
          {saving ? 'Creating…' : 'Create deployment'}
        </button>
        <button
          onClick={onCancel}
          className="text-[13px] transition-opacity hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          Cancel
        </button>
      </div>
    </Card>
  );
}

/** The environment name as configured, inline in a sentence. */
function EnvLabelInline({ env }: { env: string }) {
  const { getDisplayName } = useSettingsStore();
  return <b>{getDisplayName(env)}</b>;
}

/**
 * How long the run took, or null when the producer didn't send both ends of it. A negative span means
 * the clocks disagreed; better to show nothing than a run that finished before it started.
 */
function formatRunDuration(run: DeployRun | null | undefined): string | null {
  if (!run?.startedAt || !run?.completedAt) return null;
  const seconds = Math.round((new Date(run.completedAt).getTime() - new Date(run.startedAt).getTime()) / 1000);
  if (seconds < 0) return null;
  if (seconds < 60) return `${seconds}s`;
  return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

/**
 * How this deployment came to exist: which workflow and job ran it, who set it off, how long it took.
 * A manual entry has no run behind it, and this card says as much rather than showing empty rows —
 * "recorded by hand" is itself the answer to how it was created.
 */
function CreationCard({ event: evt }: { event: DeploymentDetail['event'] }) {
  const run = evt.run;
  const duration = formatRunDuration(run);

  // Manual entries carry the author here and the reason in metadata.
  const note = typeof evt.metadata?.note === 'string' ? evt.metadata.note : null;
  const triggeredBy = collectParticipants(evt).find((p) => p.role === 'triggered-by');

  return (
    <Card title="Created by" icon={PlayCircle}>
      <div className="divide-y" style={{ borderColor: 'var(--border-color)' }}>
        <Row label="Source">
          <span className="badge" style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}>
            {evt.source}
          </span>
        </Row>
        {run?.workflowName && <Row label="Workflow">{run.workflowName}</Row>}
        {run?.jobName && <Row label="Job">{run.jobName}</Row>}
        {run && (run.runNumber || run.runId) && (
          <Row label="Run">
            {runHref(run) ? (
              <a
                href={runHref(run)!}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1 hover:underline"
                style={{ color: 'var(--accent)' }}
              >
                {run.runNumber ? `#${run.runNumber}` : run.runId}
                {run.attempt != null && run.attempt > 1 && ` (attempt ${run.attempt})`}
                <ExternalLink size={11} />
              </a>
            ) : (
              <>{run.runNumber ? `#${run.runNumber}` : run.runId}</>
            )}
          </Row>
        )}
        <Row label="Triggered by">
          {triggeredBy?.displayName ?? triggeredBy?.email ?? run?.triggeredBy ?? '—'}
        </Row>
        <Row label="Deployed at">{format(new Date(evt.deployedAt), 'PPpp')}</Row>
        {duration && <Row label="Ran for">{duration}</Row>}
        {note && <Row label="Note">{note}</Row>}
        {!run && (
          <div className="pt-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
            No CI run is recorded against this deployment — it was either entered by hand or reported
            by a producer that doesn't send run details.
          </div>
        )}
      </div>
    </Card>
  );
}

/**
 * The references the producer attached: repository, commits, PRs, tickets, the build manifest, the
 * build pipeline. Work items appear in their own card too — there they're links into the sign-off
 * flow, here they're part of the raw record of what was attached.
 */
function ReferencesCard({ references }: { references: DeployReference[] }) {
  return (
    <Card title="References" icon={GitBranch}>
      <div className="space-y-2">
        {references.map((ref, i) => {
          const Icon = REFERENCE_ICONS[ref.type] ?? ExternalLink;
          const href = resolveReferenceHref(ref);
          const label = referenceLabel(ref);
          return (
            <div key={i} className="flex items-start gap-2 text-[13px] min-w-0">
              <Icon size={13} style={{ color: 'var(--text-muted)', flexShrink: 0, marginTop: 2 }} />
              <div className="min-w-0">
                {href ? (
                  <a
                    href={href}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="hover:underline break-words"
                    style={{ color: 'var(--accent)' }}
                  >
                    {label}
                  </a>
                ) : (
                  <span className="break-words" style={{ color: 'var(--text-secondary)' }}>{label}</span>
                )}
                <span className="ml-2 text-[11px]" style={{ color: 'var(--text-muted)' }}>{ref.type}</span>
              </div>
            </div>
          );
        })}
      </div>
    </Card>
  );
}

function referenceLabel(ref: DeployReference): string {
  if (ref.title) return ref.title;
  switch (ref.type) {
    case 'pull-request':
      return ref.key ? `#${ref.key}` : 'Pull request';
    case 'repository':
      return ref.key ?? (ref.revision ? ref.revision.slice(0, 8) : 'Repository');
    case 'commit':
      return ref.revision?.slice(0, 8) ?? ref.key ?? 'Commit';
    case 'build-manifest':
      return ref.key ?? 'Build manifest';
    default:
      return ref.key ?? ref.type;
  }
}

function PeopleCard({ participants }: { participants: DeployParticipant[] }) {
  if (participants.length === 0) {
    return (
      <Card title="People" icon={Users}>
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          Nobody was attached to this deployment.
        </p>
      </Card>
    );
  }
  return (
    <Card title="People" icon={Users}>
      <div className="flex flex-wrap gap-x-4 gap-y-1.5">
        {participants.map((p, i) => (
          <span key={i} className="inline-flex items-center gap-1.5 text-[13px]">
            <User size={12} style={{ color: 'var(--text-muted)' }} />
            <span style={{ color: 'var(--text-muted)' }}>{roleDisplay(p)}:</span>
            <span style={{ color: 'var(--text-secondary)' }}>{p.displayName ?? p.email ?? '—'}</span>
            <CopyEmailButton email={p.email} />
          </span>
        ))}
      </div>
    </Card>
  );
}

/**
 * The promotions this deployment sits between. Outbound edges are what it may feed next; inbound
 * edges are what delivered it. Split under headings because the two read as different questions
 * ("where does this go" vs "how did this get here") even though they're the same table.
 */
function PromotionsCard({ promotions, environment }: { promotions: RelatedPromotion[]; environment: string }) {
  const outbound = promotions.filter((p) => p.direction === 'outbound');
  const inbound = promotions.filter((p) => p.direction === 'inbound');

  return (
    <Card title="Promotions" icon={ArrowRight}>
      {promotions.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          No promotion carries this version. One appears once this build is put forward for the next
          environment.
        </p>
      ) : (
        <div className="space-y-3">
          {outbound.length > 0 && (
            <PromotionGroup label={`Onward from ${environment}`} promotions={outbound} />
          )}
          {inbound.length > 0 && (
            <PromotionGroup label={`Delivered into ${environment}`} promotions={inbound} />
          )}
        </div>
      )}
    </Card>
  );
}

function PromotionGroup({ label, promotions }: { label: string; promotions: RelatedPromotion[] }) {
  return (
    <div className="space-y-1.5">
      <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>{label}</p>
      <KeyboardList className="space-y-1.5" count={promotions.length} ariaLabel={label} autoFocus={false}>
        {promotions.map((p, index) => (
          <PromotionLink key={p.id} index={index} promotion={p} />
        ))}
      </KeyboardList>
    </div>
  );
}

/** One related promotion. Already a link, so it activates itself; this adds the arrow navigation. */
function PromotionLink({ index, promotion: p }: { index: number; promotion: RelatedPromotion }) {
  const rowProps = useKeyboardListRow(index, () => {}, {
    role: null,
    selfActivating: true,
    label: `${p.sourceEnv} to ${p.targetEnv}, ${p.status}. Open promotion.`,
  });
  return (
        <Link
          {...rowProps}
          to={`/promotions/${p.id}`}
          className="flex items-center gap-2 px-2.5 py-2 rounded-lg border transition-colors hover:opacity-90"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
        >
          <EnvBadge env={p.sourceEnv} />
          <ArrowRight size={12} style={{ color: 'var(--text-muted)' }} />
          <EnvBadge env={p.targetEnv} />
          <span className="flex-1" />
          <span
            className="text-[11px] font-semibold"
            style={{ color: PROMOTION_STATUS_COLOR[p.status] ?? 'var(--text-muted)' }}
          >
            {p.status}
          </span>
          <ExternalLink size={11} style={{ color: 'var(--text-muted)' }} />
        </Link>
  );
}

/**
 * One work item line in the deployment's list.
 *
 * A wrapper rather than props on the link itself, because the line's content varies: a gated item is a
 * `<Link>` to its sign-off page, an ungated one is an external anchor or plain text. The row is the
 * arrow-navigation unit either way, and `hasGate` is what decides whether Enter has anywhere to go.
 */
function WorkItemLine({
  index,
  hasGate,
  children,
}: {
  index: number;
  hasGate: boolean;
  children: React.ReactNode;
}) {
  // The row is a wrapper, not the link, so `selfActivating` would leave Enter doing nothing: the sweep
  // takes the nested anchor out of the tab order, and there would be no handler to replace it. Enter
  // clicks that anchor instead, which keeps one implementation of "where does this line go".
  const line = useRef<HTMLDivElement | null>(null);
  const rowProps = useKeyboardListRow(index, () => line.current?.querySelector('a')?.click(), {
    role: null,
    // Ungated items have no sign-off page to open — navigable, but nothing to activate.
    disabled: !hasGate,
  });
  const registerRow = rowProps.ref;
  return (
    <div
      {...rowProps}
      ref={(el) => {
        line.current = el;
        registerRow(el);
      }}
      className="min-w-0"
    >
      {children}
    </div>
  );
}

/**
 * The work items this deployment carries, linked into their sign-off pages. Sign-off is keyed on
 * (key, product, targetEnv), so a link needs a target environment — the server supplies the ones the
 * ticket is actually gated for. With none (no promotion yet) the ticket still shows, linked to its
 * source system instead of to a sign-off page that wouldn't have a gate to display.
 */
function WorkItemsCard({ workItems, product, environment }: {
  workItems: RelatedWorkItem[];
  product: string;
  environment: string;
}) {
  const { getDisplayName } = useSettingsStore();

  return (
    <Card title="Work items" icon={Ticket}>
      {workItems.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          No work items were attached to this deployment.
        </p>
      ) : (
        <KeyboardList
          className="space-y-1.5"
          count={workItems.length}
          ariaLabel="Work items on this deployment"
          autoFocus={false}
        >
          {workItems.map((wi, index) => {
            const targetEnv = wi.signOffTargetEnvs[0];
            return (
              <WorkItemLine key={wi.key} index={index} hasGate={!!targetEnv}>
                {targetEnv ? (
                  <Link
                    to={workItemDetailPath(wi.key, product, targetEnv)}
                    className="flex items-baseline gap-2 text-[13px] hover:underline"
                    style={{ color: 'var(--accent)' }}
                  >
                    <span className="font-mono font-medium shrink-0">{wi.key}</span>
                    {wi.title && (
                      <span
                        className="truncate"
                        style={{ color: 'var(--text-secondary)' }}
                        title={wi.subTitle ? `${wi.title}\n${wi.subTitle}` : wi.title}
                      >
                        {wi.title}
                      </span>
                    )}
                  </Link>
                ) : (
                  <div className="flex items-baseline gap-2 text-[13px]">
                    {wi.url ? (
                      <a
                        href={wi.url}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="font-mono font-medium shrink-0 hover:underline"
                        style={{ color: 'var(--accent)' }}
                        title={`Open ${wi.key} in ${providerLabel(wi.provider)}`}
                      >
                        {wi.key}
                      </a>
                    ) : (
                      <span className="font-mono font-medium shrink-0" style={{ color: 'var(--text-primary)' }}>
                        {wi.key}
                      </span>
                    )}
                    {wi.title && (
                      <span
                        className="truncate"
                        style={{ color: 'var(--text-secondary)' }}
                        title={wi.subTitle ? `${wi.title}\n${wi.subTitle}` : wi.title}
                      >
                        {wi.title}
                      </span>
                    )}
                  </div>
                )}
                {wi.signOffTargetEnvs.length > 1 && (
                  <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
                    Also gated for {wi.signOffTargetEnvs.slice(1).map(getDisplayName).join(', ')}
                  </p>
                )}
              </WorkItemLine>
            );
          })}
          {workItems.some((wi) => wi.signOffTargetEnvs.length === 0) && (
            <p className="text-[11px] pt-1" style={{ color: 'var(--text-muted)' }}>
              Tickets without a promotion out of {getDisplayName(environment)} link to their source
              system — there's no sign-off gate to open yet.
            </p>
          )}
        </KeyboardList>
      )}
    </Card>
  );
}

/**
 * The same service's other deployments in this environment — the timeline this one sits in, and the
 * fastest way to answer "was the version before it fine". Each row links to its own detail page, and
 * carries `from` so going back lands here rather than in the matrix.
 */
function HistoryCard({ history, currentId, product, service }: {
  history: DeploymentDetail['history'];
  currentId: string;
  product: string;
  service: string;
}) {
  const fromCurrent = `?from=${encodeURIComponent(`/deployments/events/${currentId}`)}&fromLabel=${encodeURIComponent(service)}`;

  return (
    <Card
      title="Recent deployments"
      icon={History}
      action={
        <Link
          to={`/deployments/${encodeURIComponent(product)}/${encodeURIComponent(service)}/history`}
          className="text-[11px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--accent)' }}
        >
          All
        </Link>
      }
    >
      <div className="space-y-0.5">
        {history.map((h) => {
          const isCurrent = h.id === currentId;
          const style = STATUS_STYLES[h.status] ?? STATUS_STYLES.succeeded;
          const row = (
            <>
              <span className="inline-block w-1.5 h-1.5 rounded-full shrink-0" style={{ backgroundColor: style.fg }} />
              <span className="font-mono text-[12px] font-medium" style={{ color: isCurrent ? 'var(--text-primary)' : 'var(--accent)' }}>
                v{h.version}
              </span>
              {h.isRollback && <Undo2 size={10} style={{ color: 'var(--text-muted)' }} />}
              <span className="flex-1" />
              <span className="text-[11px] whitespace-nowrap" style={{ color: 'var(--text-muted)' }}>
                {formatDistanceToNow(new Date(h.deployedAt), { addSuffix: true })}
              </span>
            </>
          );
          return (
            <div key={h.id}>
              {isCurrent ? (
                <div
                  className="flex items-center gap-2 px-2 py-1.5 rounded-lg"
                  style={{ backgroundColor: 'var(--accent-muted)' }}
                  title="You're looking at this one"
                >
                  {row}
                </div>
              ) : (
                <Link
                  to={`/deployments/events/${h.id}${fromCurrent}`}
                  className="flex items-center gap-2 px-2 py-1.5 rounded-lg transition-colors hover:bg-[var(--bg-secondary)]"
                >
                  {row}
                </Link>
              )}
              {h.failureReason && (
                <p
                  className="pl-6 pr-2 pb-1 text-[11px] font-mono break-words"
                  style={{ color: 'var(--danger)' }}
                  title={h.failureReason}
                >
                  {h.failureReason}
                </p>
              )}
            </div>
          );
        })}
      </div>
    </Card>
  );
}
