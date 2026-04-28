import { useEffect, useState, useMemo } from 'react';
import { useParams, Link } from 'react-router-dom';
import { api } from '@/lib/api';
import type {
  PromotionCandidate,
  PromotionApprovalEntry,
  PromotionStatus,
  PromotionGate,
  PromotionSourceEvent,
  PromotionSourceEventReference,
  PromotionSourceEventParticipant,
  PromotionParticipant,
  PromotionComment,
  PromotionInheritedReference,
  PromotionInheritedParticipant,
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

const GATE_LABEL: Record<PromotionGate, string> = {
  PromotionOnly: 'Promotion only',
  TicketsOnly: 'Tickets only',
  TicketsAndManual: 'Tickets + manual',
};

// Distinct work-items in the candidate's bundle. Built from the source event's
// references plus inherited references (which carry forward when an older
// pending candidate was superseded). Deduped on key.
function buildBundleWorkItems(
  sourceEvent: PromotionSourceEvent | null,
  inheritedRefs: PromotionInheritedReference[],
): PromotionSourceEventReference[] {
  const out: PromotionSourceEventReference[] = [];
  const seen = new Set<string>();
  const push = (r: PromotionSourceEventReference) => {
    if (r.type !== 'work-item') return;
    const k = (r.key ?? '').trim();
    if (!k || seen.has(k)) return;
    seen.add(k);
    out.push(r);
  };
  if (sourceEvent) for (const r of sourceEvent.references) push(r);
  for (const ir of inheritedRefs) push(ir.reference);
  return out;
}

// Compact one-line label for a reference-level participant. Format: "Role: Display <email>",
// falling back to email-only or just the role label when no human name is available.
// Display names are truncated client-side so a long full name can't blow the row layout.
function formatReferenceParticipant(p: PromotionSourceEventParticipant): string {
  const role = roleDisplay(p.role);
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
  const [candidate, setCandidate] = useState<PromotionCandidate | null>(null);
  const [approvals, setApprovals] = useState<PromotionApprovalEntry[]>([]);
  const [sourceEvent, setSourceEvent] = useState<PromotionSourceEvent | null>(null);
  const [comments, setComments] = useState<PromotionComment[]>([]);
  const [inheritedRefs, setInheritedRefs] = useState<PromotionInheritedReference[]>([]);
  const [inheritedParticipants, setInheritedParticipants] = useState<PromotionInheritedParticipant[]>([]);
  const [inheritedOpen, setInheritedOpen] = useState(false);
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
        setInheritedRefs(data.inheritedReferences || []);
        setInheritedParticipants(data.inheritedParticipants || []);
      })
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    fetchData();
  }, [id]);

  const handleAction = async (action: 'approve' | 'reject') => {
    setActionLoading(true);
    try {
      if (action === 'approve') {
        await api.approvePromotion(id!, comment || undefined);
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
  const bundleWorkItems = buildBundleWorkItems(sourceEvent, inheritedRefs);

  return (
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
            <span className="font-medium">{candidate.sourceEnv}</span>
            <ArrowRight size={14} style={{ color: 'var(--text-muted)' }} />
            <span className="font-medium">{candidate.targetEnv}</span>
            <span
              className="px-1.5 py-0.5 rounded text-[12px] font-mono"
              style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
              title={
                candidate.targetCurrentVersion
                  ? `Replaces v${candidate.targetCurrentVersion} currently in ${candidate.targetEnv}`
                  : `First deploy to ${candidate.targetEnv}`
              }
            >
              {candidate.targetCurrentVersion
                ? `v${candidate.targetCurrentVersion} → v${candidate.version}`
                : candidate.version}
            </span>
            {candidate.gate && (
              <span
                className="badge"
                style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
                title="Promotion gate mode from the resolved policy snapshot"
              >
                <Ticket size={10} />
                {GATE_LABEL[candidate.gate]}
              </span>
            )}
          </div>
        </div>
        <span className="badge" style={{ backgroundColor: cfg.bg, color: cfg.color }}>
          <StatusIcon size={10} />
          {candidate.status}
        </span>
      </div>

      {/* Success banner */}
      {actionDone && (
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{
            backgroundColor: actionDone === 'Approved' ? 'var(--success-bg)' : 'var(--danger-bg)',
            borderColor: actionDone === 'Approved' ? 'var(--success)' : 'var(--danger)',
            color: actionDone === 'Approved' ? 'var(--success)' : 'var(--danger)',
          }}
        >
          {actionDone === 'Approved' ? <CheckCircle size={18} /> : <XCircle size={18} />}
          <span className="text-[13px] font-medium">
            {actionDone === 'Approved'
              ? 'You approved this promotion.'
              : 'You rejected this promotion.'}
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

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left column */}
        <div className="lg:col-span-2 space-y-4">
          {/* Tickets card — bundle of work-items keyed (key, product, targetEnv).
             Per-row Approve / Reject buttons hit the work-item endpoints; after each
             decision we refetch the candidate so the manual card stays in sync. */}
          <TicketsCard
            candidate={candidate}
            workItems={bundleWorkItems}
            onChanged={fetchData}
          />

          {/* Manual promotion-level approval. Behaviour depends on the candidate's gate. */}
          <PromotionApprovalCard
            candidate={candidate}
            actionDone={actionDone}
            comment={comment}
            setComment={setComment}
            actionLoading={actionLoading}
            onAction={handleAction}
          />

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

          {/* References (from source deploy event) */}
          {sourceEvent && sourceEvent.references.length > 0 && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2
                className="text-[11px] font-semibold uppercase tracking-wider mb-4"
                style={{ color: 'var(--text-muted)' }}
              >
                References
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

          {/* Inherited from superseded predecessors — refs and participants carried forward */}
          {(inheritedRefs.length > 0 || inheritedParticipants.length > 0) && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <button
                type="button"
                onClick={() => setInheritedOpen((v) => !v)}
                className="w-full flex items-center justify-between text-left"
              >
                <h2
                  className="text-[11px] font-semibold uppercase tracking-wider"
                  style={{ color: 'var(--text-muted)' }}
                >
                  Inherited from superseded candidates
                  <span className="ml-2 normal-case font-normal" style={{ color: 'var(--text-muted)' }}>
                    ({inheritedRefs.length} refs, {inheritedParticipants.length} people)
                  </span>
                </h2>
                <span style={{ color: 'var(--text-muted)' }}>{inheritedOpen ? '−' : '+'}</span>
              </button>
              {inheritedOpen && (
                <div className="mt-4 space-y-3">
                  {inheritedRefs.length > 0 && (
                    <div className="space-y-2">
                      {inheritedRefs.map((ir, i) => (
                        <div key={`ir-${i}`} className="flex items-center gap-2">
                          <ReferenceItem reference={ir.reference} labels={{}} />
                          <span
                            className="text-[10px] px-1.5 py-0.5 rounded font-mono"
                            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
                            title={`Carried from v${ir.fromVersion}`}
                          >
                            v{ir.fromVersion}
                          </span>
                        </div>
                      ))}
                    </div>
                  )}
                  {inheritedParticipants.length > 0 && (
                    <div className="space-y-1 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                      {inheritedParticipants.map((ip, i) => (
                        <div key={`ip-${i}`} className="flex items-center gap-2">
                          <span style={{ color: 'var(--text-muted)' }}>{roleDisplay({ role: ip.participant.role })}</span>
                          <span>{ip.participant.displayName ?? ip.participant.email ?? '—'}</span>
                          <CopyEmailButton email={ip.participant.email} />
                          <span
                            className="ml-auto text-[10px] px-1.5 py-0.5 rounded font-mono"
                            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
                            title={`Carried from v${ip.fromVersion}`}
                          >
                            v{ip.fromVersion}
                          </span>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          )}

          {/* People — merges source-event participants (read-only) + promotion-level (editable) */}
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

  return (
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

  // Promotion-level roles override same-role source-event entries (case-insensitive).
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
        {!showForm && (
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
              <button
                onClick={() => handleRemove(p.role)}
                className="transition-opacity hover:opacity-80"
                style={{ color: 'var(--text-muted)' }}
                title="Remove"
              >
                <X size={12} />
              </button>
            </span>
          </div>
        ))}
      </div>

      {showForm && (
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
                  {isMine && (
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
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────
// Promotion approval (manual) card
//
// Mirrors the legacy single-block approval UI but adapts to the candidate's
// gate mode:
//   - PromotionOnly: as today
//   - TicketsAndManual: as today, with an explanatory "Manual + Tickets" line
//   - TicketsOnly: disabled with the same wording the API returns on a 400
// ─────────────────────────────────────────────────────────────────────────
function PromotionApprovalCard({
  candidate,
  actionDone,
  comment,
  setComment,
  actionLoading,
  onAction,
}: {
  candidate: PromotionCandidate;
  actionDone: string | null;
  comment: string;
  setComment: (v: string) => void;
  actionLoading: boolean;
  onAction: (action: 'approve' | 'reject') => void;
}) {
  const gate: PromotionGate = candidate.gate ?? 'PromotionOnly';
  const ticketsOnly = gate === 'TicketsOnly';
  const showActions = candidate.canApprove && !actionDone;

  // Hide the card entirely when there's nothing actionable: not your decision
  // and not a TicketsOnly candidate (where we want to surface the explainer
  // even for non-approvers so the gating is discoverable).
  if (!showActions && !ticketsOnly) return null;

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
        {gate !== 'PromotionOnly' && (
          <span
            className="badge"
            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
            title="Gate mode"
          >
            {GATE_LABEL[gate]}
          </span>
        )}
      </div>

      {ticketsOnly && (
        <p
          className="text-[12px] mb-3 p-3 rounded-lg"
          style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
        >
          This candidate auto-promotes when all tickets are signed off — approve the
          tickets, not the promotion.
        </p>
      )}

      {showActions && (
        <>
          <textarea
            value={comment}
            onChange={(e) => setComment(e.target.value)}
            placeholder="Optional comment..."
            rows={3}
            disabled={ticketsOnly}
            className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none mb-3"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
              opacity: ticketsOnly ? 0.6 : 1,
            }}
          />
          <div className="flex items-center gap-2">
            <button
              onClick={() => onAction('approve')}
              disabled={actionLoading || ticketsOnly}
              title={ticketsOnly ? 'Disabled under TicketsOnly gate' : undefined}
              className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--success)',
                color: '#fff',
                opacity: actionLoading || ticketsOnly ? 0.5 : 1,
                cursor: ticketsOnly ? 'not-allowed' : 'pointer',
              }}
            >
              <CheckCircle size={14} />
              Approve
            </button>
            <button
              onClick={() => onAction('reject')}
              disabled={actionLoading || ticketsOnly}
              title={ticketsOnly ? 'Disabled under TicketsOnly gate' : undefined}
              className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--danger)',
                color: '#fff',
                opacity: actionLoading || ticketsOnly ? 0.5 : 1,
                cursor: ticketsOnly ? 'not-allowed' : 'pointer',
              }}
            >
              <XCircle size={14} />
              Reject
            </button>
          </div>
        </>
      )}
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────
// Tickets (work-item) card
//
// Lists every work-item in the candidate's bundle (own source-event refs +
// inherited from superseded predecessors, deduped on key). Per-row buttons
// drive POST /api/work-items/{key}/approvals|rejections. Authority is decided
// by GET /api/work-items/{key}?product=&targetEnv= so we surface the same
// blockedReason wording the API would return on a failed POST.
//
// Empty bundle: explicit message, plus a hint about the TicketsOnly fallback.
// ─────────────────────────────────────────────────────────────────────────
function TicketsCard({
  candidate,
  workItems,
  onChanged,
}: {
  candidate: PromotionCandidate;
  workItems: PromotionSourceEventReference[];
  onChanged: () => void;
}) {
  const gate: PromotionGate = candidate.gate ?? 'PromotionOnly';

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
          <Ticket size={12} /> Tickets ({workItems.length})
        </h2>
      </div>

      {workItems.length === 0 ? (
        <div
          className="p-3 rounded-lg text-[12px]"
          style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
        >
          No work-items on this candidate.
          {gate === 'TicketsOnly' && (
            <span className="block mt-1" style={{ color: 'var(--text-muted)' }}>
              Under the TicketsOnly gate the candidate falls back to manual signoff — see the
              promotion approval card.
            </span>
          )}
        </div>
      ) : (
        <div className="space-y-2">
          {workItems.map((wi, i) => (
            <TicketRow
              key={wi.key ?? wi.url ?? `wi-${i}`}
              candidate={candidate}
              reference={wi}
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
  onChanged,
}: {
  candidate: PromotionCandidate;
  reference: PromotionSourceEventReference;
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
      setError(err instanceof Error ? err.message : 'Failed to load ticket state');
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

          {/* Reference-level participants (e.g. QA on a ticket, author on a PR). Optional —
              old payloads have none. Compact one-liner under the title; truncated if long. */}
          {reference.participants && reference.participants.length > 0 && (
            <div
              className="mt-0.5 text-[11px] truncate"
              style={{ color: 'var(--text-muted)' }}
              title={reference.participants
                .map((p) => formatReferenceParticipant(p))
                .join(', ')}
            >
              {reference.participants
                .map((p) => formatReferenceParticipant(p))
                .join(', ')}
            </div>
          )}

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
