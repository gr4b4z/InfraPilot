import { useEffect, useId, useState, useMemo, createContext, useContext } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { api } from '@/lib/api';
import type {
  PromotionCandidate,
  PromotionApprovalEntry,
  PromotionStatus,
  PromotionSourceEvent,
  PromotionSourceEventReference,
  PromotionSourceEventParticipant,
  PromotionParticipant,
  PromotionComment,
  PromotionApprovalProgress,
  EligibleRequirement,
  WorkItemContext,
} from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { useDocumentTitle } from '@/lib/pageTitle';
import { roleDisplay, useConfiguredRoles } from '@/lib/roleLabel';
import { formatDistanceToNow, format } from 'date-fns';
import {
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  Clock,
  CheckCircle,
  XCircle,
  Rocket,
  ExternalLink,
  GitCompareArrows,
  GitPullRequest,
  GitBranch,
  Ticket,
  Workflow,
  Users,
  Plus,
  X,
  History,
  MessageSquare,
  Edit2,
  Trash2,
  Undo2,
} from 'lucide-react';
import { CopyEmailButton } from '@/components/deployments/CopyEmailButton';
import { PromotionRoute } from '@/components/promotions/PromotionRoute';
import { WorkItemParticipants } from '@/components/promotions/WorkItemParticipants';
import {
  MissingRolesBadge,
  MissingRolesNotice,
  WorkItemsNeedingAttentionBadge,
} from '@/components/promotions/MissingRoles';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { ROW_ACTION_ATTR } from '@/lib/keys';
import { resolveReferenceHref } from '@/lib/refUrl';
import {
  commitCompareUrl,
  commitMessageLines,
  decisionStyle,
  shortHash,
  workItemDetailPath,
} from '@/lib/workItem';
import { refreshMyTasks } from '@/stores/myTasksStore';

// Terminal statuses: no further mutations are allowed once one of these is reached.
const TERMINAL_STATUSES: PromotionStatus[] = ['Deployed', 'Rejected', 'Superseded'];

// Author email the API stamps on entries it writes itself (PromotionComment.SystemAuthor).
const SYSTEM_COMMENT_AUTHOR = 'system';

// How long the API holds a promotion.approved webhook before sending it — the window in which
// cancelling the approval also stops the delivery. Display only; the authority is
// PromotionService.ApprovedWebhookDelay, so keep the two in step.
const APPROVED_WEBHOOK_DELAY_MS = 10_000;

// Context that gates all interactive controls on the detail page.
// Set to true when the candidate is in a terminal state.
const PromoReadOnlyCtx = createContext(false);

// Distinct work-items in the candidate's bundle, built from the candidate's own references and
// deduped on key. Participants on these references are edited through the candidate itself — the
// candidate is self-contained, so there is no deploy event to override.
function buildBundleWorkItems(
  sourceEvent: PromotionSourceEvent | null,
): PromotionSourceEventReference[] {
  const out: PromotionSourceEventReference[] = [];
  const seen = new Set<string>();
  if (!sourceEvent) return out;
  for (const r of sourceEvent.references) {
    if (r.type !== 'work-item') continue;
    const k = (r.key ?? '').trim();
    if (!k || seen.has(k)) continue;
    seen.add(k);
    out.push(r);
  }
  return out;
}

const STATUS_CONFIG: Record<
  PromotionStatus,
  { icon: typeof Clock; color: string; bg: string }
> = {
  Pending: { icon: Clock, color: 'var(--warning)', bg: 'var(--warning-bg)' },
  Approved: { icon: CheckCircle, color: 'var(--info)', bg: 'var(--info-bg)' },
  Deploying: { icon: Rocket, color: 'var(--accent)', bg: 'var(--accent-bg)' },
  Deployed: { icon: CheckCircle, color: 'var(--success)', bg: 'var(--success-bg)' },
  Superseded: { icon: Clock, color: 'var(--text-muted)', bg: 'var(--bg-secondary)' },
  Rejected: { icon: XCircle, color: 'var(--danger)', bg: 'var(--danger-bg)' },
};

const REFERENCE_ICONS: Record<string, typeof ExternalLink> = {
  pipeline: Workflow,
  repository: GitBranch,
  'pull-request': GitPullRequest,
  'work-item': Ticket,
};

export function PromotionDetailPage() {
  const { id } = useParams<{ id: string }>();
  const currentUserEmail = useAuthStore((s) => s.user?.email ?? '');
  const isAdmin = useAuthStore((s) => s.user?.isAdmin ?? false);
  const [candidate, setCandidate] = useState<PromotionCandidate | null>(null);
  const [approvals, setApprovals] = useState<PromotionApprovalEntry[]>([]);
  const [sourceEvent, setSourceEvent] = useState<PromotionSourceEvent | null>(null);
  const [comments, setComments] = useState<PromotionComment[]>([]);
  const [approvalProgress, setApprovalProgress] = useState<PromotionApprovalProgress | null>(null);
  const [eligibleRequirements, setEligibleRequirements] = useState<EligibleRequirement[]>([]);
  const [bypass, setBypass] = useState<{ byName: string; at: string; reason: string | null } | null>(null);
  const [canCancelApproval, setCanCancelApproval] = useState(false);
  // The deploy event that put this version live in the target env — set only once the
  // promotion is Deployed, and the link the "Deployed" timestamp points at.
  const [deploymentEventId, setDeploymentEventId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [actionLoading, setActionLoading] = useState(false);
  const [actionDone, setActionDone] = useState<string | null>(null);
  // Set once an approval is cancelled on this page. `webhookStopped` says whether the
  // promotion.approved delivery was still in its hold window — two rather different pieces of news,
  // so the banner reports which one happened.
  const [cancelled, setCancelled] = useState<{ webhookStopped: boolean } | null>(null);

  const fetchData = () => {
    api
      .getPromotion(id!)
      .then((data) => {
        setCandidate(data.candidate);
        setApprovals(data.approvals || []);
        setSourceEvent(data.sourceEvent ?? null);
        setComments(data.comments || []);
        setApprovalProgress(data.approvalProgress ?? null);
        setEligibleRequirements(data.eligibleRequirements || []);
        setBypass(data.bypass ?? null);
        setCanCancelApproval(data.canCancelApproval ?? false);
        setDeploymentEventId(data.deploymentEventId ?? null);
      })
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
  };

  // Live refresh for this candidate: its own events by id, plus work-item sign-offs which
  // change the approval progress shown here (their events carry a key, not the candidate id).
  const realtimeTick = useEntityRefresh(['promotion', 'work-item'], {
    filter: (evt) => evt.entity === 'work-item' || !evt.id || evt.id === id,
  });

  useEffect(() => {
    fetchData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, realtimeTick]);

  // Above the early returns below, so the hook order holds on every render. The edge is in the title
  // as well as the service: "which promotion" is a (service, source → target) question, and two
  // pending candidates for the same service differ only by their edge.
  useDocumentTitle([
    candidate && `${candidate.product}/${candidate.service}`,
    candidate && `${candidate.sourceEnv} → ${candidate.targetEnv}`,
    'Promotion',
  ]);

  const handleAction = async (
    action: 'approve' | 'reject',
    actionComment: string,
    target?: EligibleRequirement,
  ) => {
    setActionLoading(true);
    setError(null);
    try {
      if (action === 'approve') {
        await api.approvePromotion(id!, actionComment || undefined, target);
      } else {
        await api.rejectPromotion(id!, actionComment || undefined);
      }
      setActionDone(action === 'approve' ? 'Approved' : 'Rejected');
      fetchData();
      // This candidate just left (or is about to leave) the user's awaiting-me list.
      refreshMyTasks();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Action failed');
    } finally {
      setActionLoading(false);
    }
  };

  // Undo an approval made by mistake: Approved back to Pending, sign-offs cleared. `actionDone` is
  // deliberately NOT set — unlike approve/reject this leaves the promotion open and actionable, and
  // latching the card into a "done" state would hide the approve controls the user now needs.
  const handleCancelApproval = async (cancelComment: string) => {
    setActionLoading(true);
    setError(null);
    try {
      const result = await api.cancelPromotionApproval(id!, cancelComment || undefined);
      setCancelled({ webhookStopped: result.approvedWebhookStopped });
      fetchData();
      // Back in the pending queue — it may be awaiting this user again.
      refreshMyTasks();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Cancelling the approval failed');
    } finally {
      setActionLoading(false);
    }
  };

  // Admin escape hatch: force-approve a Pending candidate without satisfying its gate. The reason is
  // required (the button that calls this is disabled until it's non-empty).
  const handleBypass = async (reason: string) => {
    setActionLoading(true);
    setError(null);
    try {
      await api.bypassPromotion(id!, reason);
      setActionDone('Bypassed');
      fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Bypass failed');
    } finally {
      setActionLoading(false);
    }
  };

  if (loading) {
    return (
      <div className="max-w-6xl mx-auto space-y-4">
        <div className="skeleton h-8 w-48" />
        <div className="skeleton h-64" />
      </div>
    );
  }

  if (error && !candidate) {
    return (
      <div className="flex flex-col items-center justify-center h-64 gap-2">
        <XCircle size={24} style={{ color: 'var(--danger)' }} />
        <p className="text-[14px] font-medium" style={{ color: 'var(--danger)' }}>{error}</p>
        <Link to="/promotions" className="text-[13px] font-medium" style={{ color: 'var(--accent)' }}>
          Back to promotions
        </Link>
      </div>
    );
  }

  if (!candidate) return null;

  const cfg = STATUS_CONFIG[candidate.status] ?? STATUS_CONFIG.Pending;
  const StatusIcon = cfg.icon;
  const bundleWorkItems = buildBundleWorkItems(sourceEvent);
  const isReadOnly = TERMINAL_STATUSES.includes(candidate.status);

  // Where every "Deployed" on this page points: the status badge, the read-only banner and the
  // timestamp all name the same fact, so they all link to the deploy that shipped it. Null while
  // the promotion is open, or when no matching deploy event can be found.
  const deploymentHref = deploymentEventId ? `/deployments/events/${deploymentEventId}` : null;

  // The provider's commit-diff view between what the target runs and what is being promoted — the
  // exact change set the work items below were derived from. Needs both revisions plus the
  // candidate's repository reference for the URL shape; absent any of them the row shows what it
  // can (bare revisions) or nothing.
  const repositoryRef = candidate.sourceEventReferences.find((r) => r.type === 'repository');
  const compareUrl = commitCompareUrl(
    repositoryRef?.url,
    repositoryRef?.provider,
    candidate.fromRevision,
    candidate.toRevision,
  );

  return (
    <PromoReadOnlyCtx.Provider value={isReadOnly}>
    {/* Wider than the usual reading width: the left column carries the work-item bundle, whose
        titles truncate on a single line. At a narrower cap most of them were cut off. */}
    <div className="max-w-6xl mx-auto space-y-6">
      {/* Breadcrumb */}
      <Link
        to="/promotions"
        className="inline-flex items-center gap-1.5 text-[12px] font-medium transition-colors hover:text-[var(--accent)]"
        style={{ color: 'var(--text-muted)' }}
      >
        <ArrowLeft size={14} /> Back to promotions
      </Link>

      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            {candidate.product} / {candidate.service}
          </h1>
          <div className="mt-1.5 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
            <PromotionRoute
              product={candidate.product}
              service={candidate.service}
              sourceEnv={candidate.sourceEnv}
              targetEnv={candidate.targetEnv}
              version={candidate.version}
              fromVersion={candidate.fromVersion}
              targetCurrentVersion={candidate.targetCurrentVersion}
              sourceBranch={candidate.sourceBranch}
            />
          </div>
          {/* Commit-level provenance: the revision span this promotion ships, linking to the
              provider's diff so "what exactly is in this?" is one click, not an archaeology dig. */}
          {(candidate.fromRevision || candidate.toRevision) && (
            <div
              className="mt-1.5 flex items-center gap-1.5 text-[12px] font-mono"
              style={{ color: 'var(--text-muted)' }}
            >
              <GitCompareArrows size={13} style={{ flexShrink: 0 }} />
              {candidate.fromRevision && candidate.toRevision ? (
                compareUrl ? (
                  <a
                    href={compareUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-1 transition-colors hover:text-[var(--accent)]"
                    style={{ color: 'var(--text-secondary)' }}
                    title="Open the commit diff this promotion ships"
                  >
                    {shortHash(candidate.fromRevision)} → {shortHash(candidate.toRevision)}
                    <ExternalLink size={11} />
                  </a>
                ) : (
                  <span>
                    {shortHash(candidate.fromRevision)} → {shortHash(candidate.toRevision)}
                  </span>
                )
              ) : (
                <span>{shortHash(candidate.toRevision ?? candidate.fromRevision ?? '')}</span>
              )}
            </div>
          )}
        </div>
        {deploymentHref ? (
          <Link
            to={deploymentHref}
            className="badge transition-opacity hover:opacity-80 hover:underline"
            style={{ backgroundColor: cfg.bg, color: cfg.color }}
            title="View the deployment that shipped this version"
          >
            <StatusIcon size={10} />
            {candidate.status}
          </Link>
        ) : (
          <span className="badge" style={{ backgroundColor: cfg.bg, color: cfg.color }}>
            <StatusIcon size={10} />
            {candidate.status}
          </span>
        )}
      </div>

      {/* Success banner. Approved and Bypassed are both positive outcomes (the candidate advanced);
         Rejected is negative. */}
      {actionDone && (() => {
        const positive = actionDone === 'Approved' || actionDone === 'Bypassed';
        const message =
          actionDone === 'Approved'
            ? 'You approved this promotion.'
            : actionDone === 'Bypassed'
              ? 'You bypassed the approval gate — this promotion was force-approved.'
              : 'You rejected this promotion.';
        return (
          <div
            className="flex items-center gap-3 p-4 rounded-xl border"
            style={{
              backgroundColor: positive ? 'var(--success-bg)' : 'var(--danger-bg)',
              borderColor: positive ? 'var(--success)' : 'var(--danger)',
              color: positive ? 'var(--success)' : 'var(--danger)',
            }}
          >
            {positive ? <CheckCircle size={18} /> : <XCircle size={18} />}
            <span className="text-[13px] font-medium">{message}</span>
          </div>
        );
      })()}

      {/* Cancelled-approval banner. Not folded into `actionDone` above: this outcome leaves the
         promotion open, and the one thing the user needs to know is whether the downstream webhook
         got out before they took the approval back. */}
      {cancelled && (
        <div
          className="flex items-start gap-3 p-4 rounded-xl border"
          style={{
            backgroundColor: 'var(--bg-secondary)',
            borderColor: 'var(--border-color)',
            color: 'var(--text-secondary)',
          }}
        >
          <Undo2 size={18} style={{ color: 'var(--text-muted)', flexShrink: 0, marginTop: 1 }} />
          <span className="text-[13px]">
            Approval cancelled — this promotion is back to <strong>Pending</strong> and awaits sign-off again.
            {cancelled.webhookStopped ? (
              <> The <code>promotion.approved</code> webhook was stopped before it went out.</>
            ) : (
              <>
                {' '}The <code>promotion.approved</code> webhook had already been sent, so anything
                downstream may have acted on it.
              </>
            )}
          </span>
        </div>
      )}

      {/* Error banner */}
      {error && candidate && (
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{ backgroundColor: 'var(--danger-bg)', borderColor: 'var(--danger)', color: 'var(--danger)' }}
        >
          <XCircle size={18} />
          <span className="text-[13px] font-medium">{error}</span>
        </div>
      )}

      {/* Read-only banner — shown when the candidate has reached a terminal state */}
      {isReadOnly && (
        <div
          className="flex items-center gap-2 px-4 py-2.5 rounded-xl border text-[12px]"
          style={{
            backgroundColor: 'var(--bg-secondary)',
            borderColor: 'var(--border-color)',
            color: 'var(--text-muted)',
          }}
        >
          <CheckCircle size={13} style={{ color: cfg.color, flexShrink: 0 }} />
          This promotion is{' '}
          {deploymentHref ? (
            <Link
              to={deploymentHref}
              className="hover:underline"
              title="View the deployment that shipped this version"
            >
              <strong style={{ color: cfg.color }}>{candidate.status}</strong>
            </Link>
          ) : (
            <strong style={{ color: cfg.color }}>{candidate.status}</strong>
          )}{' '}
          — the page is read-only.
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left column */}
        <div className="lg:col-span-2 space-y-4">
          {/* Work items card — bundle of work-items keyed (key, product, targetEnv). Rows link to
             the work-item detail page, which owns sign-off and discussion; assigning people here
             refetches the candidate so the row re-renders with the new participant. */}
          <WorkItemsCard
            candidate={candidate}
            workItems={bundleWorkItems}
            onChanged={fetchData}
          />

          {/* Promotion approval — the live gate progress (per step / per requirement) and the
             approve/reject action shown together in one card. Progress is visible to everyone;
             the controls appear only when the current user can act. */}
          <PromotionApprovalCard
            candidate={candidate}
            progress={approvalProgress}
            actionDone={actionDone}
            actionLoading={actionLoading}
            onAction={handleAction}
            onBypass={handleBypass}
            onCancelApproval={handleCancelApproval}
            canCancelApproval={canCancelApproval}
            isAdmin={isAdmin}
            eligibleRequirements={eligibleRequirements}
          />

          {/* Admin bypass banner — a bypass leaves no approval row, so this is the only trace of
             who force-approved the promotion and why. Shown in the approval area. */}
          {bypass && (
            <div
              className="rounded-xl border p-4 flex items-start gap-3"
              style={{ borderColor: 'var(--warning)', backgroundColor: 'var(--warning-bg, rgba(234,179,8,0.1))' }}
            >
              <Rocket size={18} style={{ color: 'var(--warning)', flexShrink: 0, marginTop: 1 }} />
              <div className="text-[13px]">
                <p style={{ color: 'var(--text-primary)' }}>
                  Approval gate <b>bypassed</b> by <b>{bypass.byName}</b>
                  {' '}on {format(new Date(bypass.at), 'MMM d, yyyy HH:mm')} — force-approved without satisfying the gate.
                </p>
                {bypass.reason && (
                  <p className="mt-1" style={{ color: 'var(--text-secondary)' }}>
                    Reason: {bypass.reason}
                  </p>
                )}
              </div>
            </div>
          )}

          {/* Approval trail */}
          {approvals.length > 0 && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2
                className="text-[11px] font-semibold uppercase tracking-wider mb-4"
                style={{ color: 'var(--text-muted)' }}
              >
                Approval Trail ({approvals.length})
              </h2>
              <div className="space-y-2">
                {approvals.map((a) => {
                  const isApproved = a.decision === 'Approved';
                  return (
                    <div
                      key={a.id}
                      className="flex items-start gap-3 p-3 rounded-lg border"
                      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
                    >
                      <div
                        className="w-7 h-7 rounded-full flex items-center justify-center shrink-0 mt-0.5"
                        style={{
                          backgroundColor: isApproved ? 'var(--success-bg)' : 'var(--danger-bg)',
                          color: isApproved ? 'var(--success)' : 'var(--danger)',
                        }}
                      >
                        {isApproved ? <CheckCircle size={14} /> : <XCircle size={14} />}
                      </div>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between">
                          <span className="inline-flex items-center gap-1.5 text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
                            {a.approverName}
                            <CopyEmailButton email={a.approverEmail} />
                          </span>
                          <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                            {format(new Date(a.createdAt), 'MMM d, HH:mm')}
                          </span>
                        </div>
                        <div className="mt-1">
                          <span
                            className="badge"
                            style={{
                              backgroundColor: isApproved ? 'var(--success-bg)' : 'var(--danger-bg)',
                              color: isApproved ? 'var(--success)' : 'var(--danger)',
                            }}
                          >
                            {a.decision}
                          </span>
                        </div>
                        {a.comment && (
                          <p className="text-[12px] mt-1.5" style={{ color: 'var(--text-secondary)' }}>
                            &ldquo;{a.comment}&rdquo;
                          </p>
                        )}
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          )}

          {/* Comments */}
          <CommentsCard
            candidateId={candidate.id}
            comments={comments}
            currentUserEmail={currentUserEmail}
            onChange={setComments}
          />

          {/* References — the change set being promoted (commits / work-items / PRs). Placed at the
             bottom of the main column because it can be long; the full width keeps it readable. */}
          {sourceEvent && sourceEvent.references.length > 0 && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2
                className="text-[11px] font-semibold uppercase tracking-wider mb-4"
                style={{ color: 'var(--text-muted)' }}
              >
                References ({sourceEvent.references.length})
              </h2>
              <div className="space-y-2">
                {sourceEvent.references.map((ref, i) => (
                  <ReferenceItem
                    key={i}
                    reference={ref}
                    labels={sourceEvent.enrichment?.labels ?? {}}
                  />
                ))}
              </div>
            </div>
          )}
        </div>

        {/* Right column */}
        <div className="space-y-4">
          {/* Details card */}
          <div
            className="rounded-xl border p-5"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
          >
            <h2
              className="text-[11px] font-semibold uppercase tracking-wider mb-4"
              style={{ color: 'var(--text-muted)' }}
            >
              Details
            </h2>
            <div className="space-y-3 text-[13px]">
              <div className="flex items-center gap-2">
                <GitPullRequest size={14} style={{ color: 'var(--text-muted)' }} />
                <span style={{ color: 'var(--text-muted)' }}>Product:</span>
                <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
                  {candidate.product}
                </span>
              </div>
              <div className="flex items-center gap-2">
                <Rocket size={14} style={{ color: 'var(--text-muted)' }} />
                <span style={{ color: 'var(--text-muted)' }}>Service:</span>
                <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
                  {candidate.service}
                </span>
              </div>
            </div>
          </div>

          {/* Timestamps */}
          <div
            className="rounded-xl border p-5"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
          >
            <h2
              className="text-[11px] font-semibold uppercase tracking-wider mb-4"
              style={{ color: 'var(--text-muted)' }}
            >
              Timestamps
            </h2>
            <div className="space-y-3 text-[13px]">
              <div>
                <span className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
                  Created
                </span>
                <p className="font-medium mt-0.5" style={{ color: 'var(--text-primary)' }}>
                  {format(new Date(candidate.createdAt), 'MMM d, yyyy HH:mm')}
                  <span className="ml-2 text-[11px] font-normal" style={{ color: 'var(--text-muted)' }}>
                    ({formatDistanceToNow(new Date(candidate.createdAt), { addSuffix: true })})
                  </span>
                </p>
              </div>
              {candidate.approvedAt && (
                <div>
                  <span className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
                    Approved
                  </span>
                  <p className="font-medium mt-0.5" style={{ color: 'var(--text-primary)' }}>
                    {format(new Date(candidate.approvedAt), 'MMM d, yyyy HH:mm')}
                  </p>
                </div>
              )}
              {candidate.deployedAt && (
                <div>
                  {/* "Deployed" links to the deploy event that put this version live, when we can
                      resolve it — the promotion says the change shipped, and this is where to go to
                      see what actually landed. Plain text when there is no event to point at. */}
                  {deploymentHref ? (
                    <Link
                      to={deploymentHref}
                      className="inline-flex items-center gap-1 text-[11px] font-medium uppercase tracking-wider hover:underline"
                      style={{ color: 'var(--accent)' }}
                      title="View the deployment that shipped this version"
                    >
                      Deployed
                      <ArrowRight size={11} />
                    </Link>
                  ) : (
                    <span className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
                      Deployed
                    </span>
                  )}
                  <p className="font-medium mt-0.5" style={{ color: 'var(--text-primary)' }}>
                    {format(new Date(candidate.deployedAt), 'MMM d, yyyy HH:mm')}
                  </p>
                </div>
              )}
            </div>
          </div>

          {/* People — event-level participants (read-only) + promotion-level (editable).
              Reference-level participants are shown nested under each reference above. */}
          <PeopleCard
            candidate={candidate}
            sourceEvent={sourceEvent}
            onChange={(next) => setCandidate({ ...candidate, participants: next })}
          />

          {/* External run link */}
          {candidate.externalRunUrl && (
            <a
              href={candidate.externalRunUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="flex items-center gap-2 rounded-xl border p-4 text-[13px] font-medium transition-colors hover:text-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
              }}
            >
              <ExternalLink size={14} style={{ color: 'var(--accent)' }} />
              View CI run
            </a>
          )}
        </div>
      </div>
    </div>
    </PromoReadOnlyCtx.Provider>
  );
}

function ReferenceItem({
  reference,
  labels,
}: {
  reference: PromotionSourceEventReference;
  labels: Record<string, string>;
}) {
  const Icon = REFERENCE_ICONS[reference.type] ?? ExternalLink;
  const label = buildReferenceLabel(reference, labels);
  const href = resolveReferenceHref({
    type: reference.type,
    url: reference.url ?? undefined,
    provider: reference.provider ?? undefined,
    revision: reference.revision ?? undefined,
  });

  const participants = reference.participants ?? [];

  return (
    <div className="min-w-0">
      <div className="flex items-center gap-2 text-[13px] min-w-0">
        <Icon size={13} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
        {href ? (
          <a
            href={href}
            target="_blank"
            rel="noopener noreferrer"
            className="hover:underline truncate"
            title={label}
            style={{ color: 'var(--accent)' }}
          >
            {label}
          </a>
        ) : (
          <span className="truncate" title={label} style={{ color: 'var(--text-secondary)' }}>
            {label}
          </span>
        )}
      </div>
      {participants.length > 0 && (
        <div className="pl-5 mt-1 space-y-0.5">
          {participants.map((p, i) => (
            <div key={i} className="flex items-center justify-between text-[12px]">
              <span style={{ color: 'var(--text-muted)' }}>{roleDisplay(p)}</span>
              <span
                className="inline-flex items-center gap-1.5"
                style={{ color: 'var(--text-secondary)' }}
              >
                {p.displayName ?? p.email ?? '—'}
                <CopyEmailButton email={p.email ?? null} />
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}


function buildReferenceLabel(
  ref: PromotionSourceEventReference,
  labels: Record<string, string>,
): string {
  switch (ref.type) {
    case 'work-item': {
      const key = ref.key ?? 'work-item';
      const title = ref.title ?? labels.workItemTitle;
      return title ? `${key} \u2014 ${title}` : key;
    }
    case 'pull-request': {
      const num = ref.key ? `#${ref.key}` : 'Pull Request';
      const title = ref.title ?? labels.prTitle;
      return title ? `${num} \u2014 ${title}` : num;
    }
    case 'repository': {
      if (ref.key) return ref.revision ? `${ref.key} @ ${ref.revision.slice(0, 8)}` : ref.key;
      if (ref.revision) return ref.revision.slice(0, 8);
      return 'repository';
    }
    case 'pipeline':
      return ref.key ?? ref.provider ?? 'pipeline';
    default:
      return ref.key ?? ref.type;
  }
}

function PeopleCard({
  candidate,
  sourceEvent,
  onChange,
}: {
  candidate: PromotionCandidate;
  sourceEvent: PromotionSourceEvent | null;
  onChange: (participants: PromotionParticipant[]) => void;
}) {
  const readOnly = useContext(PromoReadOnlyCtx);
  const [showForm, setShowForm] = useState(false);
  const configuredRoles = useConfiguredRoles();
  const [role, setRole] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [userQuery, setUserQuery] = useState('');
  const [userResults, setUserResults] = useState<
    Array<{ id: string; displayName: string; email: string }>
  >([]);
  const [userSearchOpen, setUserSearchOpen] = useState(false);
  const [userSearchLoading, setUserSearchLoading] = useState(false);

  // Debounced directory search (Entra / local users via IIdentityService).
  useEffect(() => {
    if (!showForm) return;
    const q = userQuery.trim();
    if (q.length < 2) {
      setUserResults([]);
      return;
    }
    setUserSearchLoading(true);
    const handle = setTimeout(async () => {
      try {
        const res = await api.searchPromotionUsers(q);
        setUserResults(res.users || []);
      } catch {
        setUserResults([]);
      } finally {
        setUserSearchLoading(false);
      }
    }, 250);
    return () => clearTimeout(handle);
  }, [userQuery, showForm]);

  const sourceParticipants: PromotionSourceEventParticipant[] = sourceEvent
    ? [...sourceEvent.participants, ...(sourceEvent.enrichment?.participants ?? [])]
    : [];

  // Promotion-level roles override same-role event-level entries (case-insensitive).
  // Reference-level participants are NOT filtered out here — they're scoped to a specific
  // ref (a ticket / PR / commit), so a promotion-level "QA = Alice" doesn't shadow a
  // ticket's "QA on FOO-123 = Bob"; both are legitimate and the operator wants to see them.
  const promotionRoleSet = new Set(
    candidate.participants.map((p) => p.role.toLowerCase()),
  );
  const filteredSource = sourceParticipants.filter(
    (p) => !promotionRoleSet.has(p.role.toLowerCase()),
  );

  const hasAny = filteredSource.length > 0 || candidate.participants.length > 0;

  const reset = () => {
    setRole('');
    setDisplayName('');
    setEmail('');
    setErr(null);
    setShowForm(false);
    setUserQuery('');
    setUserResults([]);
    setUserSearchOpen(false);
  };

  const pickUser = (u: { displayName: string; email: string }) => {
    setDisplayName(u.displayName);
    setEmail(u.email);
    setUserQuery(`${u.displayName} (${u.email})`);
    setUserSearchOpen(false);
  };

  const handleSave = async () => {
    if (!role.trim()) {
      setErr('Role is required');
      return;
    }
    setSaving(true);
    setErr(null);
    try {
      const res = await api.upsertPromotionParticipant(candidate.id, {
        role: role.trim(),
        displayName: displayName.trim() || null,
        email: email.trim() || null,
      });
      onChange(res.participants);
      reset();
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  const handleRemove = async (removeRole: string) => {
    try {
      const res = await api.removePromotionParticipant(candidate.id, removeRole);
      onChange(res.participants);
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Failed to remove');
    }
  };

  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-center justify-between mb-4">
        <h2
          className="text-[11px] font-semibold uppercase tracking-wider flex items-center gap-1.5"
          style={{ color: 'var(--text-muted)' }}
        >
          <Users size={12} /> People
        </h2>
        {!readOnly && !showForm && (
          <button
            onClick={() => setShowForm(true)}
            className="inline-flex items-center gap-1 text-[11px] font-medium transition-opacity hover:opacity-80"
            style={{ color: 'var(--accent)' }}
          >
            <Plus size={12} /> Assign
          </button>
        )}
      </div>

      {!hasAny && !showForm && (
        <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          No participants yet. Assign a QA, reviewer, or other role.
        </p>
      )}

      <div className="space-y-2">
        {filteredSource.map((p, i) => (
          <div key={`src-${i}`} className="flex items-center justify-between text-[13px]">
            <span style={{ color: 'var(--text-muted)' }}>{roleDisplay(p)}</span>
            <span
              className="inline-flex items-center gap-1.5"
              style={{ color: 'var(--text-secondary)' }}
            >
              {p.displayName ?? p.email ?? '—'}
              <CopyEmailButton email={p.email ?? null} />
            </span>
          </div>
        ))}
        {candidate.participants.map((p) => (
          <div key={`prm-${p.role}`} className="flex items-center justify-between text-[13px]">
            <span style={{ color: 'var(--text-muted)' }}>{roleDisplay(p)}</span>
            <span
              className="inline-flex items-center gap-1.5"
              style={{ color: 'var(--text-primary)' }}
            >
              {p.displayName ?? p.email ?? '—'}
              <CopyEmailButton email={p.email ?? null} />
              {!readOnly && (
                <button
                  onClick={() => handleRemove(p.role)}
                  className="transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-muted)' }}
                  title="Remove"
                >
                  <X size={12} />
                </button>
              )}
            </span>
          </div>
        ))}
      </div>

      {!readOnly && showForm && (
        <div
          className="mt-4 pt-4 space-y-2 border-t"
          style={{ borderColor: 'var(--border-color)' }}
        >
          {/* The configured vocabulary, not free text: the server rejects roles that aren't in it,
              and a typo here would otherwise become a slot nothing can label, filter, or route on. */}
          <select
            value={role}
            onChange={(e) => setRole(e.target.value)}
            className="w-full rounded-lg border px-3 py-1.5 text-[13px]"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
            }}
            disabled={configuredRoles.length === 0}
          >
            <option value="">
              {configuredRoles.length === 0 ? 'No roles configured' : 'Pick a role…'}
            </option>
            {configuredRoles.map((r) => (
              <option key={r.key} value={r.key}>
                {r.displayName}
              </option>
            ))}
          </select>
          {configuredRoles.length === 0 && (
            <p className="text-[12px]" style={{ color: 'var(--warning)' }}>
              Add participant roles under Settings → Participant Roles before assigning anyone.
            </p>
          )}
          <div className="relative">
            <input
              value={userQuery}
              onChange={(e) => {
                setUserQuery(e.target.value);
                setUserSearchOpen(true);
              }}
              onFocus={() => setUserSearchOpen(true)}
              placeholder="Search directory (name or email)..."
              className="w-full rounded-lg border px-3 py-1.5 text-[13px]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-secondary)',
                color: 'var(--text-primary)',
              }}
            />
            {userSearchOpen && userQuery.trim().length >= 2 && (
              <div
                className="absolute left-0 right-0 mt-1 rounded-lg border shadow-lg max-h-48 overflow-y-auto z-10"
                style={{
                  backgroundColor: 'var(--bg-primary)',
                  borderColor: 'var(--border-color)',
                }}
              >
                {userSearchLoading && (
                  <div className="px-3 py-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
                    Searching...
                  </div>
                )}
                {!userSearchLoading && userResults.length === 0 && (
                  <div className="px-3 py-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
                    No matches — fill in manually below.
                  </div>
                )}
                {!userSearchLoading &&
                  userResults.map((u) => (
                    <button
                      key={u.id}
                      type="button"
                      onClick={() => pickUser(u)}
                      className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
                      style={{ color: 'var(--text-primary)' }}
                    >
                      <span className="font-medium">{u.displayName}</span>
                      <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                        {u.email}
                      </span>
                    </button>
                  ))}
              </div>
            )}
          </div>
          <input
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            placeholder="Display name"
            className="w-full rounded-lg border px-3 py-1.5 text-[13px]"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
            }}
          />
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="Email"
            className="w-full rounded-lg border px-3 py-1.5 text-[13px]"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
            }}
          />
          {err && (
            <p className="text-[12px]" style={{ color: 'var(--danger)' }}>
              {err}
            </p>
          )}
          <div className="flex items-center gap-2 pt-1">
            <button
              onClick={handleSave}
              disabled={saving}
              className="px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--accent)',
                color: '#fff',
                opacity: saving ? 0.6 : 1,
              }}
            >
              {saving ? 'Saving...' : 'Save'}
            </button>
            <button
              onClick={reset}
              className="px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Cancel
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function CommentsCard({
  candidateId,
  comments,
  currentUserEmail,
  onChange,
}: {
  candidateId: string;
  comments: PromotionComment[];
  currentUserEmail: string;
  onChange: (next: PromotionComment[]) => void;
}) {
  const readOnly = useContext(PromoReadOnlyCtx);
  const [body, setBody] = useState('');
  const [posting, setPosting] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editBody, setEditBody] = useState('');

  const sorted = useMemo(
    () =>
      [...comments].sort(
        (a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime(),
      ),
    [comments],
  );

  const post = async () => {
    const text = body.trim();
    if (!text) return;
    setPosting(true);
    setErr(null);
    try {
      const created = await api.addPromotionComment(candidateId, text);
      onChange([...comments, created]);
      setBody('');
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Failed to post');
    } finally {
      setPosting(false);
    }
  };

  const saveEdit = async (commentId: string) => {
    const text = editBody.trim();
    if (!text) return;
    try {
      const updated = await api.updatePromotionComment(commentId, text);
      onChange(comments.map((c) => (c.id === commentId ? updated : c)));
      setEditingId(null);
      setEditBody('');
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Failed to update');
    }
  };

  const remove = async (commentId: string) => {
    try {
      await api.deletePromotionComment(commentId);
      onChange(comments.filter((c) => c.id !== commentId));
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Failed to delete');
    }
  };

  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2
        className="text-[11px] font-semibold uppercase tracking-wider mb-4 flex items-center gap-1.5"
        style={{ color: 'var(--text-muted)' }}
      >
        <MessageSquare size={12} /> Comments ({sorted.length})
      </h2>

      <div className="space-y-3 mb-4">
        {sorted.length === 0 && (
          <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            No comments yet.
          </p>
        )}
        {sorted.map((c) => {
          const isMine =
            !!currentUserEmail &&
            c.authorEmail.toLowerCase() === currentUserEmail.toLowerCase();
          // Entries the platform wrote for an action taken on the promotion. Every action leaves
          // one, so they outnumber human comments — muted and marked so the thread still reads as
          // discussion with a history running through it, not one undifferentiated wall.
          const isSystem = c.authorEmail.toLowerCase() === SYSTEM_COMMENT_AUTHOR;
          const isEditing = editingId === c.id;
          return (
            <div
              key={c.id}
              className="p-3 rounded-lg border"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: isSystem ? 'transparent' : 'var(--bg-secondary)',
                borderStyle: isSystem ? 'dashed' : 'solid',
              }}
            >
              <div className="flex items-center justify-between mb-1">
                <span
                  className="text-[13px] font-medium inline-flex items-center gap-1.5"
                  style={{ color: isSystem ? 'var(--text-muted)' : 'var(--text-primary)' }}
                >
                  {isSystem && <History size={11} />}
                  {c.authorName || c.authorEmail}
                </span>
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  {format(new Date(c.createdAt), 'MMM d, HH:mm')}
                  {c.updatedAt && (
                    <span className="ml-1" title={`Edited ${format(new Date(c.updatedAt), 'MMM d, HH:mm')}`}>
                      (edited)
                    </span>
                  )}
                </span>
              </div>
              {isEditing ? (
                <div className="space-y-2">
                  <textarea
                    value={editBody}
                    onChange={(e) => setEditBody(e.target.value)}
                    rows={3}
                    className="w-full rounded-lg border px-2 py-1.5 text-[13px] resize-none"
                    style={{
                      borderColor: 'var(--border-color)',
                      backgroundColor: 'var(--bg-primary)',
                      color: 'var(--text-primary)',
                    }}
                  />
                  <div className="flex items-center gap-2">
                    <button
                      onClick={() => saveEdit(c.id)}
                      className="px-2.5 py-1 rounded-lg text-[11px] font-medium"
                      style={{ backgroundColor: 'var(--accent)', color: '#fff' }}
                    >
                      Save
                    </button>
                    <button
                      onClick={() => {
                        setEditingId(null);
                        setEditBody('');
                      }}
                      className="px-2.5 py-1 rounded-lg text-[11px] font-medium"
                      style={{ color: 'var(--text-muted)' }}
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : (
                <>
                  <p
                    className="text-[13px] whitespace-pre-wrap"
                    style={{ color: isSystem ? 'var(--text-muted)' : 'var(--text-secondary)' }}
                  >
                    {c.body}
                  </p>
                  {isMine && !readOnly && (
                    <div className="flex items-center gap-3 mt-2">
                      <button
                        onClick={() => {
                          setEditingId(c.id);
                          setEditBody(c.body);
                        }}
                        className="inline-flex items-center gap-1 text-[11px] transition-opacity hover:opacity-80"
                        style={{ color: 'var(--text-muted)' }}
                      >
                        <Edit2 size={10} /> Edit
                      </button>
                      <button
                        onClick={() => remove(c.id)}
                        className="inline-flex items-center gap-1 text-[11px] transition-opacity hover:opacity-80"
                        style={{ color: 'var(--danger)' }}
                      >
                        <Trash2 size={10} /> Delete
                      </button>
                    </div>
                  )}
                </>
              )}
            </div>
          );
        })}
      </div>

      {!readOnly && (
        <div className="space-y-2">
          <textarea
            value={body}
            onChange={(e) => setBody(e.target.value)}
            placeholder="Add a comment..."
            rows={2}
            className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
            }}
          />
          {err && (
            <p className="text-[12px]" style={{ color: 'var(--danger)' }}>
              {err}
            </p>
          )}
          <div className="flex items-center justify-end">
            <button
              onClick={post}
              disabled={posting || !body.trim()}
              className="px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--accent)',
                color: '#fff',
                opacity: posting || !body.trim() ? 0.6 : 1,
              }}
            >
              {posting ? 'Posting...' : 'Post'}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────
// Promotion approval (manual) card
//
// Shows the live gate progress plus the approve/reject controls. When a policy
// has no manual approver requirements there is nothing to manually approve —
// the approver has no eligible requirements — so the controls simply don't render.
// ─────────────────────────────────────────────────────────────────────────
function PromotionApprovalCard({
  candidate,
  progress,
  actionDone,
  actionLoading,
  onAction,
  onBypass,
  onCancelApproval,
  canCancelApproval,
  isAdmin,
  eligibleRequirements,
}: {
  candidate: PromotionCandidate;
  progress: PromotionApprovalProgress | null;
  actionDone: string | null;
  actionLoading: boolean;
  onAction: (action: 'approve' | 'reject', comment: string, target?: EligibleRequirement) => void;
  onBypass: (reason: string) => void;
  onCancelApproval: (comment: string) => void;
  canCancelApproval: boolean;
  isAdmin: boolean;
  eligibleRequirements: EligibleRequirement[];
}) {
  // Driven by "am I an eligible approver", NOT by candidate.canApprove — that flag now also means
  // "and nothing is blocking right now", which would hide this card exactly when the approver most
  // needs it: the Approve button below renders disabled with `approveBlockedReason` explaining what
  // to go and do. Same set the radios are built from, so the card and its controls can't disagree.
  const showActions = eligibleRequirements.length > 0 && !actionDone;
  const showProgress = !!progress?.requiresApproval;
  // Admin escape hatch: available on any Pending candidate regardless of whether this admin is an
  // eligible approver — that's the point of a bypass.
  const showBypass = isAdmin && candidate.status === 'Pending' && !actionDone;
  const [showBypassBox, setShowBypassBox] = useState(false);
  const [bypassReason, setBypassReason] = useState('');
  // Undo, for the wrong row approved by mistake. The server decides who may do it and until when
  // (Approved, not yet dispatched) — this only asks whether it said yes.
  const showCancelApproval = canCancelApproval && !actionDone;
  const [confirmingCancel, setConfirmingCancel] = useState(false);

  // When the approver is eligible for more than one open requirement they must choose which one
  // they approve as. Key by `${stepName}\u0000${requirementName}` so step+requirement is unique.
  const reqKey = (r: EligibleRequirement) => `${r.stepName}\u0000${r.requirementName}`;
  const [selectedKey, setSelectedKey] = useState<string>('');
  // Approving releases a deploy and rejecting refuses one; both are single keystrokes now (`A`, `R`),
  // so both go through a confirmation that names what it is about to do. `pending` is which one is
  // waiting on that confirmation. Declared with the other hooks, above the early return below.
  const [pending, setPending] = useState<'approve' | 'reject' | null>(null);
  // Ties the visible "why you can't approve" line to the Approve button. Up here with the other
  // hooks — the early return below means anything called after it runs conditionally.
  const approveBlockedId = useId();
  // Always offer the "Approve as" radios. With exactly one eligible requirement, preselect it
  // (one pre-checked radio) so the UI is uniform; with more than one, the approver must pick.
  const selected =
    eligibleRequirements.find((r) => reqKey(r) === selectedKey)
    ?? (eligibleRequirements.length === 1 ? eligibleRequirements[0] : null);

  // Hide the card entirely when there's nothing to show: no progress to surface, no action
  // available to the current user, and neither escape hatch on offer.
  if (!showActions && !showProgress && !showBypass && !showCancelApproval) return null;

  const confirmAction = (actionComment: string) => {
    if (!pending) return;
    // For approvals: pass the chosen requirement (preselected when only one is eligible).
    const target = pending === 'approve' ? selected ?? undefined : undefined;
    onAction(pending, actionComment, target);
    setPending(null);
  };

  // Why the Approve button is unavailable, or null when it isn't. Doubles as the tooltip, so the
  // button can never be greyed out without saying why.
  //
  // The work-item gate is checked first because it outranks the other reason: when the policy holds
  // approval back until every item is signed off, picking a requirement changes nothing. The
  // condition mirrors the server's guard in ApproveAsync exactly — an approver should learn this
  // from a disabled button, not from a failed request.
  const gate = progress?.workItems;
  const approveBlockedReason: string | null = (() => {
    if (gate && gate.required && !gate.satisfied) {
      const outstanding = gate.total - gate.approved;
      const issues = (gate.issues ?? 0) > 0 ? `, ${gate.issues} flagged with issues` : '';
      return (
        `This promotion's policy requires every work item to be approved first. ` +
        `${gate.approved} of ${gate.total} signed off${issues} — ` +
        `${outstanding} still outstanding. Sign them off from the work items below or the queue.`
      );
    }
    if (!selected) return 'Select which requirement you are approving as';
    return null;
  })();
  const approveBlocked = approveBlockedReason !== null;

  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-center justify-between mb-3">
        <h2
          className="text-[11px] font-semibold uppercase tracking-wider"
          style={{ color: 'var(--text-muted)' }}
        >
          Promotion approval
        </h2>
      </div>

      {/* Live gate progress (per step / requirement). Shown to everyone who can see the card;
         a divider separates it from the action controls when those are present. */}
      {showProgress && progress && (
        <div
          className={showActions ? 'mb-4 pb-4 border-b' : ''}
          style={showActions ? { borderColor: 'var(--border-color)' } : undefined}
        >
          <ApprovalProgressBody progress={progress} />
        </div>
      )}

      {showActions && (
        <>
          {/* "Approve as" selector — always shown when the user is eligible for any open
             requirement. A single eligible requirement is preselected (one pre-checked radio);
             with more than one the approver must pick before the Approve button enables. */}
          {eligibleRequirements.length > 0 && (
            <div className="mb-3">
              <p
                className="text-[12px] font-medium mb-1.5"
                style={{ color: 'var(--text-secondary)' }}
              >
                Approve as
              </p>
              <div className="flex flex-col gap-1.5">
                {eligibleRequirements.map((r) => {
                  const key = reqKey(r);
                  const active = selected != null && reqKey(selected) === key;
                  return (
                    <label
                      key={key}
                      className="flex items-center gap-2 px-3 py-2 rounded-lg border cursor-pointer text-[13px] transition-colors"
                      style={{
                        borderColor: active ? 'var(--accent)' : 'var(--border-color)',
                        backgroundColor: active ? 'var(--bg-secondary)' : 'transparent',
                        color: 'var(--text-primary)',
                      }}
                    >
                      <input
                        type="radio"
                        name="approve-as"
                        value={key}
                        checked={active}
                        onChange={() => setSelectedKey(key)}
                      />
                      <span className="font-medium">{r.requirementName}</span>
                      {r.stepName && (
                        <span style={{ color: 'var(--text-muted)' }}>· {r.stepName}</span>
                      )}
                    </label>
                  );
                })}
              </div>
            </div>
          )}

          {/* The comment used to live here behind an "Add comment" toggle. It moved into the
              confirmation, which now always asks — two comment fields for one decision reads as a
              bug, and the one attached to the confirmation is the one that gets used. */}
          <div className="flex items-center gap-2 flex-wrap">
            {/* The tooltip lives on a wrapper, not on the button. A disabled button receives no
                pointer events, so a `title` on it never opens — the explanation for why a control
                is unavailable would be invisible on exactly the control that needs it. */}
            <span
              title={approveBlockedReason ?? undefined}
              style={{ cursor: approveBlocked ? 'not-allowed' : undefined }}
            >
              <button
                onClick={() => setPending('approve')}
                {...{ [ROW_ACTION_ATTR]: 'approve' }}
                disabled={actionLoading || approveBlocked}
                aria-describedby={approveBlocked ? approveBlockedId : undefined}
                className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium transition-opacity"
                style={{
                  backgroundColor: 'var(--success-solid)',
                  color: '#fff',
                  opacity: actionLoading || approveBlocked ? 0.5 : 1,
                  cursor: approveBlocked ? 'not-allowed' : 'pointer',
                  pointerEvents: approveBlocked ? 'none' : undefined,
                }}
              >
                <CheckCircle size={14} />
                Approve
              </button>
            </span>
            <button
              onClick={() => setPending('reject')}
              {...{ [ROW_ACTION_ATTR]: 'reject' }}
              disabled={actionLoading}
              className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--danger-solid)',
                color: '#fff',
                opacity: actionLoading ? 0.5 : 1,
                cursor: 'pointer',
              }}
            >
              <XCircle size={14} />
              Reject
            </button>
            <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              or press <kbd className="font-mono">A</kbd> / <kbd className="font-mono">R</kbd>
            </span>
          </div>

          {/* Also stated in the open, not only on hover. A greyed-out primary action is the thing
              people report as broken, and a tooltip is unreachable on touch and to a screen reader
              that never lands on the disabled button. `aria-describedby` ties it to the button. */}
          {approveBlockedReason && (
            <p
              id={approveBlockedId}
              className="text-[12px] mt-2 flex items-start gap-1.5"
              style={{ color: 'var(--warning)' }}
            >
              <AlertTriangle size={13} className="shrink-0 mt-0.5" />
              <span>{approveBlockedReason}</span>
            </p>
          )}
        </>
      )}

      {pending && (
        <ConfirmDialog
          title={pending === 'approve' ? 'Approve this promotion?' : 'Reject this promotion?'}
          body={
            <>
              <strong>
                {candidate.product} / {candidate.service}
              </strong>{' '}
              v{candidate.version} → <strong>{candidate.targetEnv}</strong>.
              {pending === 'approve'
                ? ' Approving records your sign-off; the promotion deploys once its gate is fully satisfied.'
                : ' Rejecting turns this promotion down. It will not deploy.'}
            </>
          }
          confirmLabel={pending === 'approve' ? 'Approve' : 'Reject'}
          confirmTone={pending === 'approve' ? 'success' : 'danger'}
          commentLabel={pending === 'approve' ? 'Comment (optional)' : 'Reason'}
          // A refusal without a reason leaves the author nothing to act on, so rejection asks for one.
          commentRequired={pending === 'reject'}
          busy={actionLoading}
          onConfirm={confirmAction}
          onCancel={() => setPending(null)}
        />
      )}

      {/* Undo. Only ever on an Approved candidate the executor hasn't taken yet — the moment it is
         Deploying the answer is a rollback, and the server stops offering this. */}
      {showCancelApproval && (
        <div
          className={showProgress ? 'mt-4 pt-4 border-t' : ''}
          style={showProgress ? { borderColor: 'var(--border-color)' } : undefined}
        >
          <p className="text-[12px] mb-2" style={{ color: 'var(--text-muted)' }}>
            Approved by mistake? Cancelling puts this promotion back to <b>Pending</b> and clears the
            recorded sign-offs, so it has to be approved again. The <code>promotion.approved</code>{' '}
            webhook is held for {Math.round(APPROVED_WEBHOOK_DELAY_MS / 1000)} seconds — cancel within
            that window and it never goes out.
          </p>
          <button
            type="button"
            onClick={() => setConfirmingCancel(true)}
            disabled={actionLoading}
            className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium border transition-opacity hover:opacity-80"
            style={{
              borderColor: 'var(--border-color)',
              color: 'var(--text-secondary)',
              opacity: actionLoading ? 0.5 : 1,
            }}
          >
            <Undo2 size={14} />
            Cancel approval
          </button>
        </div>
      )}

      {confirmingCancel && (
        <ConfirmDialog
          title="Cancel this approval?"
          body={
            <>
              <strong>
                {candidate.product} / {candidate.service}
              </strong>{' '}
              v{candidate.version} → <strong>{candidate.targetEnv}</strong> goes back to Pending and
              every recorded sign-off is cleared — the approval has to be given again. If the{' '}
              <code>promotion.approved</code> webhook has already gone out, downstream may have acted
              on it.
            </>
          }
          confirmLabel="Cancel approval"
          confirmTone="danger"
          commentLabel="Reason (optional)"
          busy={actionLoading}
          onConfirm={(cancelComment) => {
            onCancelApproval(cancelComment);
            setConfirmingCancel(false);
          }}
          onCancel={() => setConfirmingCancel(false)}
        />
      )}

      {/* Admin-only bypass. Shown on any Pending candidate to admins, even when they aren't an
         eligible approver. Force-approves the candidate without satisfying the gate; the reason is
         required and the existing promotion.approved webhook still fires. */}
      {showBypass && (
        <div
          className={showActions || showProgress ? 'mt-4 pt-4 border-t' : ''}
          style={showActions || showProgress ? { borderColor: 'var(--border-color)' } : undefined}
        >
          {!showBypassBox ? (
            <button
              type="button"
              onClick={() => setShowBypassBox(true)}
              className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium border transition-opacity hover:opacity-80"
              style={{ borderColor: 'var(--warning)', color: 'var(--warning)' }}
            >
              <Rocket size={14} />
              Bypass approval gate
            </button>
          ) : (
            <div className="space-y-2">
              <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                Admin bypass force-approves this promotion <b>without</b> satisfying its approval gate.
                It is audited and still fires the downstream <code>promotion.approved</code> webhook.
                A reason is required.
              </p>
              <textarea
                value={bypassReason}
                onChange={(e) => setBypassReason(e.target.value)}
                placeholder="Reason for bypassing (required)…"
                rows={2}
                className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-secondary)',
                  color: 'var(--text-primary)',
                }}
              />
              <div className="flex items-center gap-2">
                <button
                  onClick={() => onBypass(bypassReason.trim())}
                  disabled={actionLoading || bypassReason.trim().length === 0}
                  title={bypassReason.trim().length === 0 ? 'Enter a reason first' : undefined}
                  className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium transition-opacity"
                  style={{
                    backgroundColor: 'var(--warning)',
                    color: '#fff',
                    opacity: actionLoading || bypassReason.trim().length === 0 ? 0.5 : 1,
                    cursor: bypassReason.trim().length === 0 ? 'not-allowed' : 'pointer',
                  }}
                >
                  <Rocket size={14} />
                  Confirm bypass
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setShowBypassBox(false);
                    setBypassReason('');
                  }}
                  className="text-[13px] transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-muted)' }}
                >
                  Cancel
                </button>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────
// Approval progress card
//
// Surfaces the live promotion gate (GET /promotions/{id} → approvalProgress)
// as a per-step / per-requirement breakdown of "how many approvals are in vs.
// required". The counts come straight from the backend matcher so the panel
// always mirrors the real gate — it never recomputes progress. Approver names
// live in the Approval Trail; this panel is counts + status only.
// ─────────────────────────────────────────────────────────────────────────
function ApprovalProgressBody({ progress }: { progress: PromotionApprovalProgress }) {
  const { allSatisfied, totalApproved, totalRequired, steps, workItems: workItemGate } = progress;
  const remaining = Math.max(0, totalRequired - totalApproved);

  return (
    <div>
      <div className="flex items-center justify-end mb-3">
        <span
          className="inline-flex items-center gap-1.5 text-[12px] font-medium"
          style={{ color: allSatisfied ? 'var(--success)' : 'var(--warning)' }}
        >
          {allSatisfied ? (
            <>
              <CheckCircle size={14} />
              All approvals met
            </>
          ) : (
            <>
              <Clock size={14} />
              {totalApproved} of {totalRequired} approvals
              {remaining > 0 ? ` · needs ${remaining} more` : ''}
            </>
          )}
        </span>
      </div>

      <div className="space-y-4">
        {steps.map((step, si) => (
          <div key={`${step.name}-${si}`}>
            <div className="flex items-center gap-1.5 mb-2">
              {step.satisfied ? (
                <CheckCircle size={13} style={{ color: 'var(--success)' }} />
              ) : (
                <Clock size={13} style={{ color: 'var(--warning)' }} />
              )}
              <span className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
                {step.name}
              </span>
            </div>
            <div className="space-y-1.5">
              {step.requirements.map((req, ri) => {
                // Who can satisfy this requirement: group names + explicitly-listed users.
                const approvers = [...req.groups.map((g) => g.name), ...req.users];
                const approversText = approvers.join(' · ');
                return (
                  <div
                    key={`${req.name}-${ri}`}
                    className="flex items-start justify-between gap-3 p-2.5 rounded-lg border"
                    style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
                  >
                    <div className="min-w-0">
                      <span
                        className="inline-flex items-center gap-2 text-[13px] min-w-0"
                        style={{ color: 'var(--text-primary)' }}
                      >
                        {req.satisfied ? (
                          <CheckCircle size={14} style={{ color: 'var(--success)', flexShrink: 0 }} />
                        ) : (
                          <Clock size={14} style={{ color: 'var(--warning)', flexShrink: 0 }} />
                        )}
                        <span className="truncate">{req.name}</span>
                      </span>
                      {approversText && (
                        <p
                          className="text-[11px] mt-0.5 ml-6 truncate"
                          style={{ color: 'var(--text-muted)' }}
                          title={`Can approve: ${approversText}`}
                        >
                          Approvers: {approversText}
                        </p>
                      )}
                    </div>
                    <span
                      className="text-[12px] font-medium whitespace-nowrap"
                      style={{ color: req.satisfied ? 'var(--success)' : 'var(--text-secondary)' }}
                    >
                      {req.approved} of {req.required} approved
                    </span>
                  </div>
                );
              })}
            </div>
          </div>
        ))}

        {/* The "all work items resolved" gate condition — shown when the policy requires every
           work item signed off, so the approver can see whether that condition is fulfilled. */}
        {workItemGate && (
          <div>
            <div className="flex items-center gap-1.5 mb-2">
              {workItemGate.satisfied ? (
                <CheckCircle size={13} style={{ color: 'var(--success)' }} />
              ) : (
                <Clock size={13} style={{ color: 'var(--warning)' }} />
              )}
              <span className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
                Work items
              </span>
            </div>
            <div
              className="flex items-start justify-between gap-3 p-2.5 rounded-lg border"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
            >
              <div className="min-w-0">
                <span
                  className="inline-flex items-center gap-2 text-[13px] min-w-0"
                  style={{ color: 'var(--text-primary)' }}
                >
                  {workItemGate.satisfied ? (
                    <CheckCircle size={14} style={{ color: 'var(--success)', flexShrink: 0 }} />
                  ) : (
                    <Clock size={14} style={{ color: 'var(--warning)', flexShrink: 0 }} />
                  )}
                  <span className="truncate">All work items resolved</span>
                </span>
                {workItemGate.autoApprove && (
                  <p className="text-[11px] mt-0.5 ml-6" style={{ color: 'var(--text-muted)' }}>
                    {workItemGate.satisfied
                      ? 'Auto-approved the promotion'
                      : 'Resolving all work items auto-approves this promotion'}
                  </p>
                )}
                {/* Flagged items explain a shortfall that the approved count alone doesn't. */}
                {(workItemGate.issues ?? 0) > 0 && (
                  <p className="text-[11px] mt-0.5 ml-6" style={{ color: 'var(--warning)' }}>
                    {workItemGate.issues} with issues — resolve or approve them to satisfy this gate
                  </p>
                )}
              </div>
              <span
                className="text-[12px] font-medium whitespace-nowrap"
                style={{ color: workItemGate.satisfied ? 'var(--success)' : 'var(--text-secondary)' }}
              >
                {workItemGate.approved} of {workItemGate.total} approved
              </span>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────
// Work items card
//
// Lists every work-item in the candidate's bundle (the candidate's own
// references, deduped on key). Rows are navigational: the key opens the
// work-item detail page, which owns sign-off and discussion. Here we show the
// current sign-off state — from GET /api/work-items/{key}?product=&targetEnv=
// — and let an operator assign the people responsible.
//
// Empty bundle: explicit message.
// ─────────────────────────────────────────────────────────────────────────
function WorkItemsCard({
  candidate,
  workItems,
  onChanged,
}: {
  candidate: PromotionCandidate;
  workItems: PromotionSourceEventReference[];
  onChanged: () => void;
}) {
  // Which work items have nobody in a role the policy requires, and which roles those are. Derived
  // server-side onto the candidate, so it is already right for items attached after the promotion was
  // created and after a policy edit re-gated it.
  const roleGaps = candidate.workItemRoleGaps ?? [];
  const missingRolesByKey = new Map(roleGaps.map((g) => [g.workItemKey, g.missingRoles]));
  const distinctMissingRoles = Array.from(new Set(roleGaps.flatMap((g) => g.missingRoles)));

  // A dev-only edge whose policy creates no work items. The rows below would be sign-off surfaces for
  // records that don't exist — every one of them would fetch nothing and link to a 404 — so the card
  // collapses to a one-liner. What shipped is still on the page: the References card lists the same
  // work-item entries as change-set history, which is all they are here.
  if (candidate.tracksWorkItems === false) {
    return (
      <div
        className="rounded-xl border p-5"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
      >
        <h2
          className="text-[11px] font-semibold uppercase tracking-wider flex items-center gap-1.5 mb-3"
          style={{ color: 'var(--text-muted)' }}
        >
          <Ticket size={12} /> Work items
        </h2>
        <div
          className="p-3 rounded-lg text-[12px]"
          style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
        >
          Work items aren&rsquo;t tracked on this edge — its promotion policy doesn&rsquo;t create
          them, so nothing here needs a sign-off. The{' '}
          {workItems.length === 1 ? 'work item' : `${workItems.length} work items`} this promotion
          carries {workItems.length === 1 ? 'is' : 'are'} listed under References.
        </div>
      </div>
    );
  }

  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-center justify-between gap-3 mb-4">
        <h2
          className="text-[11px] font-semibold uppercase tracking-wider flex items-center gap-1.5"
          style={{ color: 'var(--text-muted)' }}
        >
          <Ticket size={12} /> Work items ({workItems.length})
        </h2>
        <WorkItemsNeedingAttentionBadge count={roleGaps.length} roles={distinctMissingRoles} />
      </div>

      {/* Card-level notice: names the roles nobody is in, so the ask is legible without reading every
         row. The per-row badges below say which items are affected. */}
      {roleGaps.length > 0 && (
        <div className="mb-3">
          <MissingRolesNotice
            roles={distinctMissingRoles}
            action="Assign someone on the affected work items below."
          />
        </div>
      )}

      {workItems.length === 0 ? (
        <div
          className="p-3 rounded-lg text-[12px]"
          style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
        >
          No work-items on this candidate.
        </div>
      ) : (
        // The work items are what a reviewer moves through on this page, so the arrow keys iterate
        // them. Not auto-focused: the approval card above is the point of the page, and stealing the
        // caret down here on arrival would bury it.
        <KeyboardList
          className="space-y-2"
          count={workItems.length}
          ariaLabel="Work items on this promotion"
          autoFocus={false}
        >
          {workItems.map((reference, i) => (
            <TicketRow
              key={reference.key ?? reference.url ?? `wi-${i}`}
              index={i}
              candidate={candidate}
              reference={reference}
              missingRoles={missingRolesByKey.get(reference.key ?? '')}
              onChanged={onChanged}
            />
          ))}
        </KeyboardList>
      )}
    </div>
  );
}

/**
 * One work-item row.
 *
 * The whole tile opens the work-item detail page, matching the queue and the promotions list;
 * regions with their own behaviour (the tracker link, the participant controls) swallow the click
 * rather than every leaf control having to know about the tile.
 *
 * Layout note: the state badge lives in its own flex column rather than being pushed right with
 * `ml-auto` inside the title row — in the wrapping version it dropped onto a line of its own as
 * soon as the title got long. The title truncates on a single line for the same reason, and the
 * participant chips get the full content width so a long assignee list stays readable instead of
 * being clipped.
 */
function TicketRow({
  index,
  candidate,
  reference,
  missingRoles,
  onChanged,
}: {
  /** Position in the list, for the roving tabindex. */
  index: number;
  candidate: PromotionCandidate;
  reference: PromotionSourceEventReference;
  /** Policy-required roles nobody holds on this work item. Undefined/empty ⇒ nothing to flag. */
  missingRoles?: string[];
  onChanged: () => void;
}) {
  const readOnly = useContext(PromoReadOnlyCtx);
  const navigate = useNavigate();
  const key = reference.key ?? '';
  const [ctx, setCtx] = useState<WorkItemContext | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refresh = async () => {
    if (!key) {
      setLoading(false);
      return;
    }
    try {
      const next = await api.getWorkItemContext(key, candidate.product, candidate.targetEnv);
      setCtx(next);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load work item state');
    } finally {
      setLoading(false);
    }
  };

  // Each row owns its work-item context, so it also owns reacting to that work item changing.
  const workItemTick = useEntityRefresh(['work-item'], {
    filter: (evt) => !evt.key || evt.key === key,
  });

  useEffect(() => {
    refresh();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, candidate.product, candidate.targetEnv, candidate.id, workItemTick]);

  const Icon = REFERENCE_ICONS[reference.type] ?? Ticket;
  const href = resolveReferenceHref({
    type: reference.type,
    url: reference.url ?? undefined,
    provider: reference.provider ?? undefined,
    revision: reference.revision ?? undefined,
  });

  // Pick a single decision (Approved / Issue / Blocked) to render — first row wins. The API
  // keeps one row per approver and the detail page shows the full trail, so the summary badge only
  // needs the canonical outcome.
  const decided = ctx?.approvals[0] ?? null;
  const decidedStyle = decided ? decisionStyle(decided.decision) : null;
  // Undecided is "Pending" regardless of whose turn it is. The badge reports the work item's state;
  // whether the reader can act on it is already answered by the "Sign off & discuss" link below.
  const stateLabel = decidedStyle ? decidedStyle.label : 'Pending';
  const stateColor = decidedStyle?.color ?? 'var(--warning)';
  const stateBg = decidedStyle?.bg ?? 'var(--warning-bg)';

  // Carry this candidate as the referrer so the work-item page can offer a way back here.
  const detailPath = key
    ? workItemDetailPath(key, candidate.product, candidate.targetEnv, candidate.id)
    : null;

  const rowProps = useKeyboardListRow(index, () => detailPath && navigate(detailPath), {
    disabled: !detailPath,
    label: `${key || 'Work item'} — ${stateLabel}${detailPath ? '. Open work item.' : ''}`,
  });

  // The second line, back in its parts — one per commit. The server already drops a subtitle that
  // repeats the title; the guard here is for a candidate stored before it did.
  const commitLines =
    reference.subTitle && reference.subTitle !== reference.title
      ? commitMessageLines(reference.subTitle)
      : [];

  return (
    <div
      {...rowProps}
      className={`p-3 rounded-lg border ${detailPath ? 'card-hover cursor-pointer' : ''}`}
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div className="flex items-start gap-3">
        <Icon size={14} style={{ color: 'var(--text-muted)', flexShrink: 0, marginTop: 2 }} />
        <div className="flex-1 min-w-0">
          <div className="flex items-baseline gap-2 min-w-0">
            {detailPath ? (
              <Link
                to={detailPath}
                className="text-[13px] font-medium hover:underline shrink-0"
                style={{ color: 'var(--accent)' }}
                title={`Open ${key} details`}
              >
                {key}
              </Link>
            ) : (
              <span className="text-[13px] font-medium shrink-0" style={{ color: 'var(--text-primary)' }}>
                work-item
              </span>
            )}
            {/* External tracker link is a bare icon — the label itself now goes to the in-app
                detail page, which is the primary destination. */}
            {href && (
              <a
                href={href}
                target="_blank"
                rel="noopener noreferrer"
                onClick={(e) => e.stopPropagation()}
                className="shrink-0 transition-opacity hover:opacity-70"
                style={{ color: 'var(--text-muted)' }}
                title={`Open ${key || 'work item'} in ${reference.provider ?? 'the tracker'}`}
                aria-label={`Open ${key || 'work item'} in ${reference.provider ?? 'the tracker'}`}
              >
                <ExternalLink size={11} />
              </a>
            )}
            {reference.title && (
              <span
                className="text-[12px] truncate"
                style={{ color: 'var(--text-secondary)' }}
                title={reference.title}
              >
                {reference.title}
              </span>
            )}
            {/* Nobody in a role the policy requires. Sits on the title line, next to the state badge's
                column, so a reviewer scanning the bundle sees which item is missing an owner. */}
            <MissingRolesBadge roles={missingRoles} />
          </div>

          {/* Every commit message behind the ticket, one per line. The line above is the ticket's
              own name; these are what actually changed under it — a list whenever it rode in on
              more than one commit, so they get a line each rather than one running sentence. Each
              line truncates on its own, with the full message in its tooltip, so a verbose commit
              subject can't widen the row. */}
          {commitLines.length > 0 && (
            <div className="min-w-0">
              {commitLines.map((line, i) => (
                <p
                  key={i}
                  className="text-[11px] truncate"
                  style={{ color: 'var(--text-muted)' }}
                  title={line}
                >
                  {/* Marker only when there is more than one: a lone message is a sentence, not a
                      list of one. */}
                  {commitLines.length > 1 ? `• ${line}` : line}
                </p>
              ))}
            </div>
          )}

          {/* Reference-level participants (e.g. QA on a ticket). Editable in place: writes go to
              PATCH /api/promotions/{id}/references/{key}/participants. The wrapper keeps assignment
              clicks from being read as "open the work item". */}
          <div onClick={(e) => e.stopPropagation()}>
            <WorkItemParticipants
              candidateId={candidate.id}
              referenceKey={key}
              participants={reference.participants ?? []}
              onChanged={onChanged}
              readOnly={readOnly}
            />
          </div>

          {decided && (
            <div className="mt-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
              {decidedStyle?.label} by{' '}
              <span style={{ color: 'var(--text-secondary)' }}>{decided.approverEmail}</span>
              {' · '}
              {format(new Date(decided.updatedAt ?? decided.createdAt), 'MMM d, HH:mm')}
              {decided.comment && (
                <span
                  className="block mt-1 italic"
                  style={{ color: 'var(--text-secondary)' }}
                  title={decided.comment}
                >
                  &ldquo;{decided.comment}&rdquo;
                </span>
              )}
            </div>
          )}

          {!decided && ctx && !ctx.canApprove && ctx.blockedReason && (
            <p className="mt-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
              {ctx.blockedReason}
            </p>
          )}

          {loading && (
            <p className="mt-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
              Loading…
            </p>
          )}

          {error && (
            <p className="mt-1 text-[11px]" style={{ color: 'var(--danger)' }}>
              {error}
            </p>
          )}

          {detailPath && (
            <Link
              to={detailPath}
              className="inline-flex items-center gap-1 mt-1.5 text-[11px] font-medium transition-opacity hover:opacity-80"
              style={{ color: 'var(--accent)' }}
            >
              Sign off &amp; discuss
              <ArrowRight size={11} />
            </Link>
          )}
        </div>
        <span
          className="badge shrink-0 self-start"
          style={{ backgroundColor: stateBg, color: stateColor }}
        >
          {stateLabel}
        </span>
      </div>
    </div>
  );
}
