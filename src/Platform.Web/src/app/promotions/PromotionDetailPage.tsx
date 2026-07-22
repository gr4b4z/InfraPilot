import { useEffect, useState, useMemo, createContext, useContext } from 'react';
import { useParams, Link } from 'react-router-dom';
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
import { roleDisplay } from '@/lib/roleLabel';
import { formatDistanceToNow, format } from 'date-fns';
import {
  ArrowLeft,
  ArrowRight,
  Clock,
  CheckCircle,
  XCircle,
  Rocket,
  ExternalLink,
  GitPullRequest,
  GitBranch,
  Ticket,
  Workflow,
  Users,
  Plus,
  X,
  MessageSquare,
  Edit2,
  Trash2,
} from 'lucide-react';
import { CopyEmailButton } from '@/components/deployments/CopyEmailButton';
import { resolveReferenceHref } from '@/lib/refUrl';

// Terminal statuses: no further mutations are allowed once one of these is reached.
const TERMINAL_STATUSES: PromotionStatus[] = ['Deployed', 'Rejected', 'Superseded'];

// Context that gates all interactive controls on the detail page.
// Set to true when the candidate is in a terminal state.
const PromoReadOnlyCtx = createContext(false);

// Distinct work-items in the candidate's bundle. Built from the source event's
// references (the candidate's own references). Deduped on key. Each entry carries
// the origin deploy event id so the override-assign PATCH can target the right event.
export interface BundleWorkItem {
  reference: PromotionSourceEventReference;
  /** Deploy event id this reference came from. Needed to PATCH overrides. */
  deployEventId: string | null;
}

function buildBundleWorkItems(
  sourceEvent: PromotionSourceEvent | null,
): BundleWorkItem[] {
  const out: BundleWorkItem[] = [];
  const seen = new Set<string>();
  const push = (r: PromotionSourceEventReference, deployEventId: string | null) => {
    if (r.type !== 'work-item') return;
    const k = (r.key ?? '').trim();
    if (!k || seen.has(k)) return;
    seen.add(k);
    out.push({ reference: r, deployEventId });
  };
  if (sourceEvent) for (const r of sourceEvent.references) push(r, sourceEvent.id);
  return out;
}

// Compact one-line label for a reference-level participant. Format: "Role: Display <email>",
// falling back to email-only or just the role label when no human name is available.
// Display names are truncated client-side so a long full name can't blow the row layout.
function formatReferenceParticipant(p: PromotionSourceEventParticipant): string {
  const role = roleDisplay(p);
  const name = (p.displayName ?? '').trim();
  const truncatedName = name.length > 40 ? `${name.slice(0, 37)}...` : name;
  const email = (p.email ?? '').trim();
  if (truncatedName && email) return `${role}: ${truncatedName} <${email}>`;
  if (truncatedName) return `${role}: ${truncatedName}`;
  if (email) return `${role}: ${email}`;
  return role;
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
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [comment, setComment] = useState('');
  const [actionLoading, setActionLoading] = useState(false);
  const [actionDone, setActionDone] = useState<string | null>(null);

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
      })
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    fetchData();
  }, [id]);

  const handleAction = async (
    action: 'approve' | 'reject',
    target?: EligibleRequirement,
  ) => {
    setActionLoading(true);
    try {
      if (action === 'approve') {
        await api.approvePromotion(id!, comment || undefined, target);
      } else {
        await api.rejectPromotion(id!, comment || undefined);
      }
      setActionDone(action === 'approve' ? 'Approved' : 'Rejected');
      setComment('');
      fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Action failed');
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
      <div className="max-w-3xl mx-auto space-y-4">
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

  return (
    <PromoReadOnlyCtx.Provider value={isReadOnly}>
    <div className="max-w-3xl mx-auto space-y-6">
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
          <div className="flex items-center gap-3 mt-1.5 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
            <span className="font-medium">
              {candidate.sourceEnv} ({candidate.version})
            </span>
            <ArrowRight size={14} style={{ color: 'var(--text-muted)' }} />
            <span
              className="font-medium"
              title={
                candidate.targetCurrentVersion
                  ? `Replaces v${candidate.targetCurrentVersion} currently in ${candidate.targetEnv}`
                  : `First deploy to ${candidate.targetEnv}`
              }
            >
              {candidate.targetEnv} ({candidate.targetCurrentVersion ?? 'new'})
            </span>
          </div>
        </div>
        <span className="badge" style={{ backgroundColor: cfg.bg, color: cfg.color }}>
          <StatusIcon size={10} />
          {candidate.status}
        </span>
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
          This promotion is <strong style={{ color: cfg.color }}>{candidate.status}</strong> — the page is read-only.
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left column */}
        <div className="lg:col-span-2 space-y-4">
          {/* Work items card — bundle of work-items keyed (key, product, targetEnv).
             Per-row Approve / Reject buttons hit the work-item endpoints; after each
             decision we refetch the candidate so the manual card stays in sync. */}
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
            comment={comment}
            setComment={setComment}
            actionLoading={actionLoading}
            onAction={handleAction}
            onBypass={handleBypass}
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
                  <span className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
                    Deployed
                  </span>
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
  const [roles, setRoles] = useState<string[]>([]);
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

  useEffect(() => {
    if (!showForm || roles.length > 0) return;
    api
      .listPromotionRoles()
      .then((d) => setRoles(d.roles || []))
      .catch(() => setRoles([]));
  }, [showForm, roles.length]);

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
          <input
            list="promotion-roles"
            value={role}
            onChange={(e) => setRole(e.target.value)}
            placeholder="Role (e.g. QA, reviewer)"
            className="w-full rounded-lg border px-3 py-1.5 text-[13px]"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
            }}
          />
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
          <datalist id="promotion-roles">
            {roles.map((r) => (
              // Suggest humanised forms ("QA", "Triggered by") so the user's pick is already
              // how the card will render. The backend canonicalises on write.
              <option key={r} value={roleDisplay({ role: r })} />
            ))}
          </datalist>
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
          const isEditing = editingId === c.id;
          return (
            <div
              key={c.id}
              className="p-3 rounded-lg border"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-secondary)',
              }}
            >
              <div className="flex items-center justify-between mb-1">
                <span
                  className="text-[13px] font-medium"
                  style={{ color: 'var(--text-primary)' }}
                >
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
                    style={{ color: 'var(--text-secondary)' }}
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
  comment,
  setComment,
  actionLoading,
  onAction,
  onBypass,
  isAdmin,
  eligibleRequirements,
}: {
  candidate: PromotionCandidate;
  progress: PromotionApprovalProgress | null;
  actionDone: string | null;
  comment: string;
  setComment: (v: string) => void;
  actionLoading: boolean;
  onAction: (action: 'approve' | 'reject', target?: EligibleRequirement) => void;
  onBypass: (reason: string) => void;
  isAdmin: boolean;
  eligibleRequirements: EligibleRequirement[];
}) {
  const showActions = candidate.canApprove && !actionDone;
  const showProgress = !!progress?.requiresApproval;
  // Admin escape hatch: available on any Pending candidate regardless of whether this admin is an
  // eligible approver — that's the point of a bypass.
  const showBypass = isAdmin && candidate.status === 'Pending' && !actionDone;
  const [showCommentBox, setShowCommentBox] = useState(false);
  const [showBypassBox, setShowBypassBox] = useState(false);
  const [bypassReason, setBypassReason] = useState('');

  // When the approver is eligible for more than one open requirement they must choose which one
  // they approve as. Key by `${stepName}\u0000${requirementName}` so step+requirement is unique.
  const reqKey = (r: EligibleRequirement) => `${r.stepName}\u0000${r.requirementName}`;
  const [selectedKey, setSelectedKey] = useState<string>('');
  // Always offer the "Approve as" radios. With exactly one eligible requirement, preselect it
  // (one pre-checked radio) so the UI is uniform; with more than one, the approver must pick.
  const selected =
    eligibleRequirements.find((r) => reqKey(r) === selectedKey)
    ?? (eligibleRequirements.length === 1 ? eligibleRequirements[0] : null);

  // Hide the card entirely when there's nothing to show: no progress to surface, no action
  // available to the current user, and no admin bypass on offer.
  if (!showActions && !showProgress && !showBypass) return null;

  const handleAction = (action: 'approve' | 'reject') => {
    // For approvals: pass the chosen requirement (preselected when only one is eligible).
    const target = action === 'approve' ? selected ?? undefined : undefined;
    onAction(action, target);
    setShowCommentBox(false);
    setComment('');
  };

  // Block the Approve button until a requirement is selected (the single case is preselected).
  const approveBlocked = !selected;

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

          {showCommentBox && (
            <textarea
              value={comment}
              onChange={(e) => setComment(e.target.value)}
              placeholder="Optional comment..."
              rows={3}
              className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none mb-3"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-secondary)',
                color: 'var(--text-primary)',
              }}
            />
          )}
          <div className="flex items-center gap-2">
            <button
              onClick={() => handleAction('approve')}
              disabled={actionLoading || approveBlocked}
              title={
                approveBlocked
                  ? 'Select which requirement you are approving as'
                  : undefined
              }
              className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--success)',
                color: '#fff',
                opacity: actionLoading || approveBlocked ? 0.5 : 1,
                cursor: approveBlocked ? 'not-allowed' : 'pointer',
              }}
            >
              <CheckCircle size={14} />
              Approve
            </button>
            <button
              onClick={() => handleAction('reject')}
              disabled={actionLoading}
              className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--danger)',
                color: '#fff',
                opacity: actionLoading ? 0.5 : 1,
                cursor: 'pointer',
              }}
            >
              <XCircle size={14} />
              Reject
            </button>
            <button
              type="button"
              onClick={() => {
                setShowCommentBox((v) => !v);
                if (showCommentBox) setComment('');
              }}
              className="text-[13px] transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              {showCommentBox ? 'Hide comment' : 'Add comment'}
            </button>
          </div>
        </>
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
// source-event refs, deduped on key). Per-row buttons
// drive POST /api/work-items/{key}/approvals|rejections. Authority is decided
// by GET /api/work-items/{key}?product=&targetEnv= so we surface the same
// blockedReason wording the API would return on a failed POST.
//
// Empty bundle: explicit message.
// ─────────────────────────────────────────────────────────────────────────
function WorkItemsCard({
  candidate,
  workItems,
  onChanged,
}: {
  candidate: PromotionCandidate;
  workItems: BundleWorkItem[];
  onChanged: () => void;
}) {
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
          <Ticket size={12} /> Work items ({workItems.length})
        </h2>
      </div>

      {workItems.length === 0 ? (
        <div
          className="p-3 rounded-lg text-[12px]"
          style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
        >
          No work-items on this candidate.
        </div>
      ) : (
        <div className="space-y-2">
          {workItems.map((wi, i) => (
            <TicketRow
              key={wi.reference.key ?? wi.reference.url ?? `wi-${i}`}
              candidate={candidate}
              reference={wi.reference}
              deployEventId={wi.deployEventId}
              onChanged={onChanged}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function TicketRow({
  candidate,
  reference,
  deployEventId,
  onChanged,
}: {
  candidate: PromotionCandidate;
  reference: PromotionSourceEventReference;
  /** Source deploy event id this reference belongs to. PATCH targets `/deployments/{eventId}/...`.
   *  Null when the reference can't be traced back (legacy data) — assign controls hidden. */
  deployEventId: string | null;
  onChanged: () => void;
}) {
  const key = reference.key ?? '';
  const [ctx, setCtx] = useState<WorkItemContext | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [comment, setComment] = useState('');
  const [showCommentBox, setShowCommentBox] = useState(false);

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

  useEffect(() => {
    refresh();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, candidate.product, candidate.targetEnv, candidate.id]);

  const decide = async (decision: 'approve' | 'reject') => {
    if (!key) return;
    setBusy(true);
    setError(null);
    try {
      if (decision === 'approve') {
        await api.approveWorkItem(key, candidate.product, candidate.targetEnv, comment || undefined);
      } else {
        await api.rejectWorkItem(key, candidate.product, candidate.targetEnv, comment || undefined);
      }
      setComment('');
      setShowCommentBox(false);
      // Refetch this row's context AND the parent candidate (the latter so the
      // promotion-level card mirrors any cascade — auto-promote on full approve,
      // veto-cascade on reject).
      await refresh();
      onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Action failed');
    } finally {
      setBusy(false);
    }
  };

  const Icon = REFERENCE_ICONS[reference.type] ?? Ticket;
  const href = resolveReferenceHref({
    type: reference.type,
    url: reference.url ?? undefined,
    provider: reference.provider ?? undefined,
    revision: reference.revision ?? undefined,
  });

  // Pick a single decision (Approved / Rejected) to render — first decision wins.
  // The unique index in the API guarantees one row per (key, product, env, approver),
  // and the blocked-reason path guarantees no second user can decide if any decision
  // is already present. So the first row is canonical.
  const decided = ctx?.approvals[0] ?? null;
  const stateLabel = decided
    ? decided.decision
    : ctx?.canApprove
      ? 'Pending — your turn'
      : 'Pending';
  const stateColor = decided
    ? decided.decision === 'Approved'
      ? 'var(--success)'
      : 'var(--danger)'
    : 'var(--warning)';
  const stateBg = decided
    ? decided.decision === 'Approved'
      ? 'var(--success-bg)'
      : 'var(--danger-bg)'
    : 'var(--warning-bg)';

  return (
    <div
      className="p-3 rounded-lg border"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div className="flex items-start gap-3">
        <Icon size={14} style={{ color: 'var(--text-muted)', marginTop: 2 }} />
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            {href ? (
              <a
                href={href}
                target="_blank"
                rel="noopener noreferrer"
                className="text-[13px] font-medium hover:underline"
                style={{ color: 'var(--accent)' }}
                title={reference.title ?? undefined}
              >
                {key || 'work-item'}
              </a>
            ) : (
              <span className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
                {key || 'work-item'}
              </span>
            )}
            {reference.title && (
              <span className="text-[12px] truncate" style={{ color: 'var(--text-secondary)' }}>
                {reference.title}
              </span>
            )}
            <span
              className="badge ml-auto"
              style={{ backgroundColor: stateBg, color: stateColor }}
            >
              {stateLabel}
            </span>
          </div>

          {/* Reference-level participants (e.g. QA on a ticket, author on a PR).
              Now interactive: each chip can be reassigned or cleared, and an empty slot
              for any role attached to a sibling reference can be filled. Operator
              overrides surface via PATCH /api/deployments/{eventId}/references/{key}/participants. */}
          <ParticipantChips
            participants={reference.participants ?? []}
            deployEventId={deployEventId}
            referenceKey={key}
            onChanged={onChanged}
          />

          {decided && (
            <div className="mt-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
              {decided.decision} by{' '}
              <span style={{ color: 'var(--text-secondary)' }}>{decided.approverEmail}</span>
              {' · '}
              {format(new Date(decided.createdAt), 'MMM d, HH:mm')}
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

          {!decided && ctx?.canApprove && (
            <div className="mt-2">
              {showCommentBox && (
                <textarea
                  value={comment}
                  onChange={(e) => setComment(e.target.value)}
                  placeholder="Optional comment..."
                  rows={2}
                  className="w-full rounded-lg border px-2 py-1.5 text-[12px] resize-none mb-2"
                  style={{
                    borderColor: 'var(--border-color)',
                    backgroundColor: 'var(--bg-primary)',
                    color: 'var(--text-primary)',
                  }}
                />
              )}
              <div className="flex items-center gap-2">
                <button
                  onClick={() => decide('approve')}
                  disabled={busy}
                  className="inline-flex items-center gap-1 px-2.5 py-1 rounded-lg text-[11px] font-medium transition-opacity"
                  style={{
                    backgroundColor: 'var(--success)',
                    color: '#fff',
                    opacity: busy ? 0.6 : 1,
                  }}
                >
                  <CheckCircle size={11} />
                  Approve
                </button>
                <button
                  onClick={() => decide('reject')}
                  disabled={busy}
                  className="inline-flex items-center gap-1 px-2.5 py-1 rounded-lg text-[11px] font-medium transition-opacity"
                  style={{
                    backgroundColor: 'var(--danger)',
                    color: '#fff',
                    opacity: busy ? 0.6 : 1,
                  }}
                >
                  <XCircle size={11} />
                  Reject
                </button>
                <button
                  type="button"
                  onClick={() => setShowCommentBox((v) => !v)}
                  className="text-[11px] transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-muted)' }}
                >
                  {showCommentBox ? 'Hide comment' : 'Add comment'}
                </button>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

// One row of role chips for a single reference. Each chip is a participant slot:
//  - filled  → "Role: Display <email>" with a popover containing Reassign / Clear.
// A single "+ Assign" button at the end opens a picker where the operator chooses
// both the role (free-form, with directory-suggested values) and the person —
// mirroring PeopleCard's add-form so the two flows feel like one thing.
function ParticipantChips({
  participants,
  deployEventId,
  referenceKey,
  onChanged,
}: {
  participants: PromotionSourceEventParticipant[];
  deployEventId: string | null;
  referenceKey: string;
  onChanged: () => void;
}) {
  const readOnly = useContext(PromoReadOnlyCtx);
  // editingRole === '' means "new assign" (role chosen inside picker).
  // editingRole === <role> means reassigning that specific chip.
  const [editingRole, setEditingRole] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // No event id or read-only → can't PATCH. Render the read-only text fallback.
  if (!deployEventId || !referenceKey || readOnly) {
    if (participants.length === 0) return null;
    return (
      <div
        className="mt-0.5 text-[11px] truncate"
        style={{ color: 'var(--text-muted)' }}
        title={participants.map((p) => formatReferenceParticipant(p)).join(', ')}
      >
        {participants.map((p) => formatReferenceParticipant(p)).join(', ')}
      </div>
    );
  }

  const submit = async (role: string, assignee: { email: string; displayName: string } | null) => {
    setBusy(true);
    setError(null);
    try {
      await api.assignReferenceParticipant(deployEventId, referenceKey, role, assignee);
      setEditingRole(null);
      onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update participant');
    } finally {
      setBusy(false);
    }
  };

  const newAssignOpen = editingRole === '';

  return (
    <div className="mt-1 flex flex-wrap items-center gap-1.5">
      {participants.map((p) => (
        <ParticipantChip
          key={`${p.role}-${p.email ?? ''}`}
          participant={p}
          onReassign={() => setEditingRole(p.role)}
          onClear={() => submit(p.role, null)}
          editing={editingRole === p.role}
          onCancelEdit={() => setEditingRole(null)}
          onPick={(picked) => submit(p.role, picked)}
          busy={busy}
        />
      ))}
      <span className="inline-flex items-center relative">
        <button
          type="button"
          onClick={() => setEditingRole(newAssignOpen ? null : '')}
          className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium transition-opacity hover:opacity-80"
          style={{
            borderColor: 'var(--border-color)',
            color: 'var(--text-muted)',
            border: '1px dashed var(--border-color)',
          }}
          disabled={busy}
          title="Assign a person to this reference"
        >
          <Plus size={10} /> Assign
        </button>
        {newAssignOpen && (
          <InlineUserPicker
            role={null}
            onPick={(picked) => submit(picked.role, { email: picked.email, displayName: picked.displayName })}
            onCancel={() => setEditingRole(null)}
            busy={busy}
          />
        )}
      </span>
      {error && (
        <span className="text-[10px]" style={{ color: 'var(--danger)' }}>
          {error}
        </span>
      )}
    </div>
  );
}

function ParticipantChip({
  participant,
  onReassign,
  onClear,
  editing,
  onCancelEdit,
  onPick,
  busy,
}: {
  participant: PromotionSourceEventParticipant;
  onReassign: () => void;
  onClear: () => void;
  editing: boolean;
  onCancelEdit: () => void;
  onPick: (picked: { email: string; displayName: string }) => void;
  busy: boolean;
}) {
  const [menuOpen, setMenuOpen] = useState(false);
  const overridden = participant.isOverride === true;
  const tooltip = overridden && participant.assignedBy
    ? `${formatReferenceParticipant(participant)} (overridden by ${participant.assignedBy})`
    : formatReferenceParticipant(participant);

  return (
    <span className="inline-flex items-center relative">
      <button
        type="button"
        onClick={() => setMenuOpen((v) => !v)}
        className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium transition-opacity hover:opacity-80"
        style={{
          backgroundColor: overridden ? 'var(--accent-bg)' : 'var(--bg-tertiary, var(--bg-primary))',
          color: overridden ? 'var(--accent)' : 'var(--text-secondary)',
          border: '1px solid var(--border-color)',
        }}
        title={tooltip}
        disabled={busy}
      >
        <Users size={10} />
        <span className="truncate max-w-[160px]">
          {roleDisplay(participant)}: {participant.displayName ?? participant.email ?? '—'}
        </span>
        {overridden && <span style={{ color: 'var(--accent)' }}>•</span>}
      </button>
      {menuOpen && !editing && (
        <div
          className="absolute z-10 mt-1 top-full left-0 rounded-lg border shadow-lg"
          style={{ backgroundColor: 'var(--bg-secondary)', borderColor: 'var(--border-color)' }}
        >
          <button
            type="button"
            onClick={() => { setMenuOpen(false); onReassign(); }}
            className="block w-full text-left px-3 py-1.5 text-[11px] hover:opacity-80"
            style={{ color: 'var(--text-primary)' }}
          >
            Reassign…
          </button>
          <button
            type="button"
            onClick={() => { setMenuOpen(false); onClear(); }}
            className="block w-full text-left px-3 py-1.5 text-[11px] hover:opacity-80"
            style={{ color: 'var(--danger)' }}
          >
            Clear (tombstone)
          </button>
        </div>
      )}
      {editing && (
        <InlineUserPicker
          role={participant.role}
          onPick={(picked) => onPick({ email: picked.email, displayName: picked.displayName })}
          onCancel={onCancelEdit}
          busy={busy}
        />
      )}
    </span>
  );
}

// Inline user picker — debounced search against /promotions/users/search. Mirrors the
// look-and-feel of PeopleCard's add-participant dropdown so the two flows feel like one
// thing. Anchored absolutely to its parent (which must be `position: relative`); pops
// out below the chip with a fixed width so the chip itself stays narrow.
//
// Two modes via the `role` prop:
//   - role = string  → reassigning a known role; only the user is selected.
//   - role = null    → new assignment; operator types/picks the role too. Suggested
//                      roles come from /api/promotions/roles via a <datalist> (same
//                      pattern as PeopleCard).
//
// Falls back to manual email entry when the directory returns no hits (local-auth dev).
function InlineUserPicker({
  role,
  onPick,
  onCancel,
  busy,
}: {
  role: string | null;
  onPick: (picked: { role: string; email: string; displayName: string }) => void;
  onCancel: () => void;
  busy: boolean;
}) {
  const roleEditable = role === null;
  const [roleInput, setRoleInput] = useState('');
  const [knownRoles, setKnownRoles] = useState<string[]>([]);
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Array<{ id: string; displayName: string; email: string }>>([]);
  const [searching, setSearching] = useState(false);
  const datalistId = useMemo(() => `assign-roles-${Math.random().toString(36).slice(2, 8)}`, []);

  // Pre-fetch role suggestions when in role-editable mode.
  useEffect(() => {
    if (!roleEditable) return;
    let cancelled = false;
    api
      .listPromotionRoles()
      .then((d) => { if (!cancelled) setKnownRoles(d.roles || []); })
      .catch(() => { if (!cancelled) setKnownRoles([]); });
    return () => { cancelled = true; };
  }, [roleEditable]);

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) { setResults([]); return; }
    let cancelled = false;
    setSearching(true);
    const timer = setTimeout(async () => {
      try {
        const res = await api.searchPromotionUsers(q);
        if (!cancelled) setResults(res.users);
      } catch {
        if (!cancelled) setResults([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 250);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [query]);

  // Resolve the role to send: either the locked prop or whatever the operator typed.
  const effectiveRole = (role ?? roleInput).trim();
  const canSubmit = effectiveRole.length > 0;

  const submitWithUser = (u: { email: string; displayName: string }) => {
    if (!canSubmit) return;
    onPick({ role: effectiveRole, email: u.email, displayName: u.displayName });
  };

  const submitManual = () => {
    const q = query.trim();
    // Cheap email-shape check. Server validates again with the same rule.
    if (!q.includes('@') || !q.includes('.')) return;
    submitWithUser({ email: q, displayName: q });
  };

  return (
    <div
      className="absolute z-20 mt-1 top-full left-0 rounded-lg border shadow-lg p-2 w-72"
      style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
    >
      <div className="text-[11px] mb-1.5 px-1" style={{ color: 'var(--text-muted)' }}>
        {roleEditable ? 'Assign person' : `Assign ${roleDisplay({ role: role! })}`}
      </div>
      {roleEditable && (
        <>
          <input
            autoFocus
            list={datalistId}
            value={roleInput}
            onChange={(e) => setRoleInput(e.target.value)}
            placeholder="Role (e.g. QA, reviewer)"
            className="w-full rounded-lg border px-3 py-1.5 text-[13px] outline-none mb-1.5"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
            }}
            disabled={busy}
            onKeyDown={(e) => { if (e.key === 'Escape') onCancel(); }}
          />
          <datalist id={datalistId}>
            {knownRoles.map((r) => (
              <option key={r} value={roleDisplay({ role: r })} />
            ))}
          </datalist>
        </>
      )}
      <input
        autoFocus={!roleEditable}
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder="Search directory (name or email)..."
        className="w-full rounded-lg border px-3 py-1.5 text-[13px] outline-none"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-secondary)',
          color: 'var(--text-primary)',
        }}
        disabled={busy}
        onKeyDown={(e) => { if (e.key === 'Escape') onCancel(); if (e.key === 'Enter' && results.length === 0) submitManual(); }}
      />
      {query.trim().length >= 2 && (
        <div className="mt-1 max-h-48 overflow-y-auto rounded-lg border" style={{ borderColor: 'var(--border-color)' }}>
          {searching && (
            <div className="px-3 py-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
              Searching...
            </div>
          )}
          {!searching && results.length === 0 && (
            <button
              type="button"
              onClick={submitManual}
              className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-primary)' }}
              disabled={busy || !canSubmit}
              title={!canSubmit ? 'Pick a role first' : undefined}
            >
              <span className="font-medium">Use &ldquo;{query.trim()}&rdquo; as email</span>
              <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                No directory matches — sent as-is.
              </span>
            </button>
          )}
          {!searching && results.map((u) => (
            <button
              key={u.id}
              type="button"
              onClick={() => submitWithUser({ email: u.email, displayName: u.displayName })}
              className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-primary)' }}
              disabled={busy || !canSubmit}
              title={!canSubmit ? 'Pick a role first' : undefined}
            >
              <span className="font-medium truncate">{u.displayName}</span>
              <span className="text-[11px] truncate" style={{ color: 'var(--text-muted)' }}>
                {u.email}
              </span>
            </button>
          ))}
        </div>
      )}
      <div className="mt-2 flex justify-end">
        <button
          type="button"
          onClick={onCancel}
          className="px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
          disabled={busy}
        >
          Cancel
        </button>
      </div>
    </div>
  );
}
