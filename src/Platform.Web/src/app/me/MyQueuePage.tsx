import { useEffect, useState, useMemo } from 'react';
import { Link } from 'react-router-dom';
import { api } from '@/lib/api';
import type { PendingAssignee, PendingTicket, PromotionSourceEventParticipant } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { roleDisplay } from '@/lib/roleLabel';
import { formatDistanceToNow } from 'date-fns';
import {
  Ticket,
  CheckCircle,
  XCircle,
  ExternalLink,
  ArrowRight,
  Inbox,
  Clock,
  Plus,
  Users,
  X,
} from 'lucide-react';
import {
  AssigneeFilter,
  loadAssigneeFilter,
  saveAssigneeFilter,
  type AssigneeFilterValue,
} from './AssigneeFilter';
import {
  ScopeFilter,
  loadScopeFilter,
  saveScopeFilter,
  applyScopeFilter,
  hasActiveScope,
  type ScopeFilterValue,
} from './ScopeFilter';

/**
 * "My queue" page — tickets awaiting the current user's signoff. Reads
 * GET /api/work-items/me/pending which returns one row per (ticket × candidate)
 * after applying authority filters server-side (approver group, excluded role,
 * already-decided), so client-side rendering is straight-through.
 */
export function MyQueuePage() {
  const [tickets, setTickets] = useState<PendingTicket[]>([]);
  // Server-supplied (email, role) rollup + canonical role set, both feeding the dropdowns.
  // Computed against the user's authorized list pre-narrowing — the queue itself, not the
  // org directory — so every choice is one we can actually render results for.
  const [assignees, setAssignees] = useState<PendingAssignee[]>([]);
  const [roles, setRoles] = useState<string[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // Hydrate the filter from localStorage so the user's pick survives reloads. Only happens
  // on mount — subsequent updates flow through the onChange callback below.
  const [assigneeFilter, setAssigneeFilter] = useState<AssigneeFilterValue>(() => loadAssigneeFilter());
  // Product / service / targetEnv narrowing — applied client-side to the loaded queue.
  const [scopeFilter, setScopeFilter] = useState<ScopeFilterValue>(() => loadScopeFilter());
  // Status mode — controls whether the queue shows the pending inbox or the user's own
  // decision history. Persisted via localStorage.
  const [statusFilter, setStatusFilter] = useState<StatusFilterValue>(() => loadStatusFilter());
  // Time frame — only meaningful on the "decided" view; defaults to last day.
  const [timeFrame, setTimeFrame] = useState<TimeFrameValue>(() => loadTimeFrame());
  // The auth store already carries the current user's email — same source PromotionDetailPage
  // uses for `currentUserEmail`. No extra API call needed; we just send this email to the
  // server when the user picks "Assigned to me".
  const currentUserEmail = useAuthStore((s) => s.user?.email ?? '');

  // Defined as an async function so the initial fetch from `useEffect` can be a
  // microtask (avoids the eslint react-hooks/set-state-in-effect rule and the
  // associated cascading-render warning) while still letting decision handlers
  // call `fetchData()` directly to refresh after Approve / Reject.
  const fetchData = async (
    filter: AssigneeFilterValue,
    status: StatusFilterValue,
    tf: TimeFrameValue,
  ) => {
    setLoading(true);
    setError(null);
    try {
      const apiArg = toApiArg(filter, currentUserEmail, status, tf);
      const res = await api.getMyPendingWorkItems(apiArg);
      setTickets(res.tickets ?? []);
      setAssignees(res.assignees ?? []);
      setRoles(res.roles ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load tickets');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void fetchData(assigneeFilter, statusFilter, timeFrame);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assigneeFilter, statusFilter, timeFrame, currentUserEmail]);

  const handleFilterChange = (next: AssigneeFilterValue) => {
    saveAssigneeFilter(next);
    setAssigneeFilter(next);
  };

  const handleScopeChange = (next: ScopeFilterValue) => {
    saveScopeFilter(next);
    setScopeFilter(next);
  };

  const handleStatusChange = (next: StatusFilterValue) => {
    saveStatusFilter(next);
    setStatusFilter(next);
  };

  const handleTimeFrameChange = (next: TimeFrameValue) => {
    saveTimeFrame(next);
    setTimeFrame(next);
  };

  // Server-narrowed list × scope filter → what the user actually sees.
  const filteredTickets = useMemo(
    () => applyScopeFilter(tickets, scopeFilter),
    [tickets, scopeFilter],
  );

  return (
    <div className="space-y-6">
      <div>
        <h1
          className="text-xl font-semibold tracking-tight"
          style={{ color: 'var(--text-primary)' }}
        >
          Tickets queue
        </h1>
        <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
          Work-items awaiting your signoff across all products and environments.
        </p>
      </div>

      <div className="flex items-center gap-2 flex-wrap">
        <StatusFilter value={statusFilter} onChange={handleStatusChange} />
        {/* Time frame is only meaningful on the decided view. */}
        {statusFilter === 'decided' && (
          <TimeFrameFilter value={timeFrame} onChange={handleTimeFrameChange} />
        )}
        {/* Role/assignee narrowing only meaningful for the pending pool — hide for history views. */}
        {statusFilter === 'pending' && (
          <AssigneeFilter
            value={assigneeFilter}
            onChange={handleFilterChange}
            assignees={assignees}
            roles={roles}
          />
        )}
        <ScopeFilter
          value={scopeFilter}
          onChange={handleScopeChange}
          tickets={tickets}
        />
      </div>

      {error && (
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{
            backgroundColor: 'var(--danger-bg)',
            borderColor: 'var(--danger)',
            color: 'var(--danger)',
          }}
        >
          <XCircle size={18} />
          <span className="text-[13px] font-medium">{error}</span>
        </div>
      )}

      {loading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="skeleton h-16" />
          ))}
        </div>
      ) : filteredTickets.length === 0 ? (
        <div
          className="flex flex-col items-center justify-center py-20 rounded-xl border"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
          }}
        >
          <div
            className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
          >
            <Inbox size={24} />
          </div>
          <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>
            {tickets.length > 0 && hasActiveScope(scopeFilter)
              ? 'No tickets match the current filters.'
              : emptyStateTitle(assigneeFilter)}
          </p>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            {tickets.length > 0 && hasActiveScope(scopeFilter)
              ? 'Widen the product / service / target-env picks to see more rows.'
              : emptyStateBody(assigneeFilter)}
          </p>
        </div>
      ) : (
        <div className="space-y-2">
          {filteredTickets.map((t) => (
            <TicketRow
              key={`${t.workItemKey}-${t.candidateId}-${t.decidedAt ?? 'pending'}-${t.decidedByEmail ?? ''}`}
              ticket={t}
              onChanged={() => fetchData(assigneeFilter, statusFilter, timeFrame)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function toApiArg(
  filter: AssigneeFilterValue,
  currentUserEmail: string,
  status: StatusFilterValue,
  timeFrame: TimeFrameValue,
): { role?: string; assignee?: string; status?: 'pending' | 'decided'; since?: string } | undefined {
  // Decision-history views ignore role/assignee narrowing — the backend short-circuits.
  if (status === 'decided') {
    const since = timeFrameToSince(timeFrame);
    return since ? { status, since } : { status };
  }

  const role = filter.role ?? undefined;
  let assignee: string | undefined;
  switch (filter.mode) {
    case 'all':
      assignee = undefined;
      break;
    case 'me':
      assignee = currentUserEmail || undefined;
      break;
    case 'unassigned':
      assignee = 'unassigned';
      break;
    case 'person':
      assignee = filter.email;
      break;
  }
  if (!role && !assignee) return undefined;
  return { role, assignee };
}

// ── Status filter (pending inbox vs. recent decisions) ───────────────────────────────────

export type StatusFilterValue = 'pending' | 'decided';

const STATUS_FILTER_STORAGE_KEY = 'me.queue.statusFilter';

function loadStatusFilter(): StatusFilterValue {
  try {
    const raw = window.localStorage.getItem(STATUS_FILTER_STORAGE_KEY);
    if (raw === 'decided') return raw;
  } catch {
    // Ignore — fall through to default.
  }
  return 'pending';
}

function saveStatusFilter(value: StatusFilterValue): void {
  try {
    window.localStorage.setItem(STATUS_FILTER_STORAGE_KEY, value);
  } catch {
    // Ignore.
  }
}

function StatusFilter({
  value,
  onChange,
}: {
  value: StatusFilterValue;
  onChange: (next: StatusFilterValue) => void;
}) {
  return (
    <label
      className="inline-flex items-center gap-1.5 text-[12px]"
      style={{ color: 'var(--text-muted)' }}
    >
      <span>Show</span>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value as StatusFilterValue)}
        className="rounded-lg border px-2 py-1.5 text-[12px] font-medium"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          color: 'var(--text-primary)',
        }}
      >
        <option value="pending">Pending</option>
        <option value="decided">Approved &amp; Rejected</option>
      </select>
    </label>
  );
}

// ── Time-frame filter (only meaningful on "decided" view) ────────────────────────────────

export type TimeFrameValue = '1d' | '7d' | '30d' | 'all';

const TIME_FRAME_STORAGE_KEY = 'me.queue.timeFrame';

function loadTimeFrame(): TimeFrameValue {
  try {
    const raw = window.localStorage.getItem(TIME_FRAME_STORAGE_KEY);
    if (raw === '7d' || raw === '30d' || raw === 'all') return raw;
  } catch {
    // Ignore.
  }
  return '1d';
}

function saveTimeFrame(value: TimeFrameValue): void {
  try {
    window.localStorage.setItem(TIME_FRAME_STORAGE_KEY, value);
  } catch {
    // Ignore.
  }
}

/**
 * Maps the time-frame pick into an ISO `since` cutoff for the API. Returns null for "all"
 * (no cutoff — server returns everything).
 */
function timeFrameToSince(value: TimeFrameValue): string | null {
  if (value === 'all') return null;
  const days = value === '1d' ? 1 : value === '7d' ? 7 : 30;
  const cutoff = new Date();
  cutoff.setDate(cutoff.getDate() - days);
  return cutoff.toISOString();
}

function TimeFrameFilter({
  value,
  onChange,
}: {
  value: TimeFrameValue;
  onChange: (next: TimeFrameValue) => void;
}) {
  return (
    <label
      className="inline-flex items-center gap-1.5 text-[12px]"
      style={{ color: 'var(--text-muted)' }}
    >
      <span>Time frame</span>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value as TimeFrameValue)}
        className="rounded-lg border px-2 py-1.5 text-[12px] font-medium"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          color: 'var(--text-primary)',
        }}
      >
        <option value="1d">Last day</option>
        <option value="7d">Last 7 days</option>
        <option value="30d">Last 30 days</option>
        <option value="all">All time</option>
      </select>
    </label>
  );
}

function emptyStateTitle(filter: AssigneeFilterValue): string {
  const roleLabel = filter.role ? roleDisplay({ role: filter.role }) : null;
  switch (filter.mode) {
    case 'all':
      return roleLabel
        ? `No tickets where someone is ${roleLabel}.`
        : 'No tickets awaiting your signoff.';
    case 'me':
      return roleLabel
        ? `No tickets where you're the ${roleLabel}.`
        : 'Nothing assigned to you right now.';
    case 'unassigned':
      return roleLabel
        ? `No tickets without a ${roleLabel} assigned.`
        : 'No unassigned tickets in your authorized list.';
    case 'person':
      return roleLabel
        ? `No tickets with ${filter.displayName} as ${roleLabel}.`
        : `No tickets with ${filter.displayName} as any role.`;
  }
}

function emptyStateBody(filter: AssigneeFilterValue): string {
  switch (filter.mode) {
    case 'all':
      return filter.role
        ? 'Pick a different role or "Any role" to widen the queue.'
        : 'New tickets will appear here as promotions roll through your environments.';
    case 'me':
      return 'Switch the assignee to "Anyone" to see the full queue you can sign off on.';
    case 'unassigned':
      return filter.role
        ? 'Tickets where this role is empty will show up here.'
        : 'Tickets without a named QA / reviewer / assignee will show up here.';
    case 'person':
      return 'Try a different person, or switch to "Anyone".';
  }
}

function TicketRow({
  ticket,
  onChanged,
}: {
  ticket: PendingTicket;
  onChanged: () => void;
}) {
  // Decided rows render a read-only history view: decision badge + decider + comment, no
  // Approve/Reject buttons, no participant editing. The candidate may also have moved on
  // (Approved/Deployed/Rejected/Superseded) so we surface its current status as a hint.
  const decided = ticket.decision != null;

  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showComment, setShowComment] = useState(false);
  // Local participant state — seeded from the ticket, updated optimistically after assign.
  const [participants, setParticipants] = useState<PromotionSourceEventParticipant[]>(
    ticket.participants ?? [],
  );
  // Re-sync from the prop whenever the parent reloads tickets (filter change, post-decision
  // refresh, etc.). Without this the useState initializer only runs once and stale data sticks.
  useEffect(() => {
    setParticipants(ticket.participants ?? []);
  }, [ticket.participants]);
  const [showAssignForm, setShowAssignForm] = useState(false);

  const decide = async (decision: 'approve' | 'reject') => {
    setBusy(true);
    setError(null);
    try {
      if (decision === 'approve') {
        await api.approveWorkItem(ticket.workItemKey, ticket.product, ticket.targetEnv, comment || undefined);
      } else {
        await api.rejectWorkItem(ticket.workItemKey, ticket.product, ticket.targetEnv, comment || undefined);
      }
      onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Action failed');
    } finally {
      setBusy(false);
    }
  };

  const handleAssign = async (role: string, assignee: { email: string; displayName: string }) => {
    try {
      const res = await api.assignReferenceParticipant(
        ticket.sourceDeployEventId,
        ticket.workItemKey,
        role,
        assignee,
      );
      setParticipants(res.participants);
      setShowAssignForm(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to assign');
    }
  };

  // Tombstone a participant. Same API as assign but with `assignee: null` — the server
  // records a tombstone-override that suppresses the lower (reference / event / enrichment)
  // layers for this (refKey, role). Mirrors PromotionDetailPage's "Clear" action.
  const handleRemove = async (role: string) => {
    try {
      const res = await api.assignReferenceParticipant(
        ticket.sourceDeployEventId,
        ticket.workItemKey,
        role,
        null,
      );
      setParticipants(res.participants);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to remove');
    }
  };

  return (
    <div
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-start gap-3">
        <Ticket size={16} style={{ color: 'var(--text-muted)', marginTop: 2 }} />
        <div className="flex-1 min-w-0">
          {/* Title row */}
          <div className="flex items-center gap-2 flex-wrap">
            {ticket.url ? (
              <a
                href={ticket.url}
                target="_blank"
                rel="noopener noreferrer"
                className="text-[13px] font-semibold hover:underline inline-flex items-center gap-1"
                style={{ color: 'var(--accent)' }}
                title={ticket.title ?? undefined}
              >
                {ticket.workItemKey}
                <ExternalLink size={11} />
              </a>
            ) : (
              <span className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                {ticket.workItemKey}
              </span>
            )}
            {ticket.title && (
              <span className="text-[12px] truncate" style={{ color: 'var(--text-secondary)' }}>
                {ticket.title}
              </span>
            )}
            {ticket.blockingPromotions > 1 && (
              <span
                className="badge"
                style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
                title={`Referenced by ${ticket.blockingPromotions} pending promotion candidates`}
              >
                ×{ticket.blockingPromotions}
              </span>
            )}
          </div>

          {/* Context */}
          <div
            className="flex items-center gap-2 flex-wrap mt-1.5 text-[12px]"
            style={{ color: 'var(--text-secondary)' }}
          >
            <span>
              <span style={{ color: 'var(--text-muted)' }}>Product:</span>{' '}
              <span className="font-medium">{ticket.product}</span>
            </span>
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <span className="inline-flex items-center gap-1">
              <span className="font-medium">{ticket.sourceEnv}</span>
              <ArrowRight size={11} style={{ color: 'var(--text-muted)' }} />
              <span className="font-medium">{ticket.targetEnv}</span>
            </span>
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <span>
              <span style={{ color: 'var(--text-muted)' }}>Service:</span>{' '}
              <span className="font-medium">{ticket.service}</span>
            </span>
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <span>
              <span style={{ color: 'var(--text-muted)' }}>Version:</span>{' '}
              <span className="font-mono">{ticket.version}</span>
            </span>
          </div>

          {/* Participants row */}
          <div className="flex items-center gap-1.5 flex-wrap mt-2">
            <Users size={11} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
            {participants.length === 0 && !showAssignForm && (
              <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                No people assigned
              </span>
            )}
            {participants.map((p, i) => (
              <span
                key={`${p.role}-${i}`}
                className="inline-flex items-center gap-1 pl-1.5 pr-1 py-0.5 rounded text-[11px]"
                style={{
                  backgroundColor: p.isOverride ? 'var(--accent-bg)' : 'var(--bg-secondary)',
                  color: p.isOverride ? 'var(--accent)' : 'var(--text-secondary)',
                  border: p.isOverride ? '1px solid color-mix(in srgb, var(--accent) 30%, transparent)' : '1px solid var(--border-color)',
                }}
                title={p.email ?? undefined}
              >
                <span style={{ color: 'var(--text-muted)' }}>{roleDisplay(p)}:</span>
                <span className="font-medium">{p.displayName ?? p.email ?? '—'}</span>
                {!decided && (
                  <button
                    type="button"
                    onClick={() => handleRemove(p.role)}
                    className="ml-0.5 inline-flex items-center justify-center rounded-full transition-opacity hover:opacity-70"
                    style={{ color: 'var(--text-muted)', width: 14, height: 14 }}
                    title={p.isOverride ? 'Remove assignment' : 'Hide this participant for this ticket'}
                    aria-label={`Remove ${roleDisplay(p)} ${p.displayName ?? p.email ?? ''}`}
                  >
                    <X size={10} />
                  </button>
                )}
              </span>
            ))}
            {!decided && !showAssignForm && (
              <button
                type="button"
                onClick={() => setShowAssignForm(true)}
                className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[11px] transition-opacity hover:opacity-80"
                style={{
                  border: '1px dashed var(--border-color)',
                  color: 'var(--text-muted)',
                }}
              >
                <Plus size={10} /> Assign
              </button>
            )}
          </div>

          {/* Inline assign form */}
          {!decided && showAssignForm && (
            <AssignForm
              onSave={handleAssign}
              onCancel={() => setShowAssignForm(false)}
            />
          )}

          {/* Candidate link */}
          <div className="flex items-center gap-3 mt-1.5 text-[11px]" style={{ color: 'var(--text-muted)' }}>
            <Link
              to={`/promotions/${ticket.candidateId}`}
              className="inline-flex items-center gap-1 hover:underline"
              style={{ color: 'var(--accent)' }}
            >
              View candidate
              <ExternalLink size={10} />
            </Link>
          </div>

          {!decided && showComment && (
            <textarea
              value={comment}
              onChange={(e) => setComment(e.target.value)}
              placeholder="Optional comment..."
              rows={2}
              className="w-full rounded-lg border px-2 py-1.5 text-[12px] resize-none mt-2"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-secondary)',
                color: 'var(--text-primary)',
              }}
            />
          )}

          {error && (
            <p className="mt-1 text-[11px]" style={{ color: 'var(--danger)' }}>
              {error}
            </p>
          )}

          {decided ? (
            <DecisionBanner
              decision={ticket.decision!}
              decidedAt={ticket.decidedAt ?? null}
              decidedByName={ticket.decidedByName ?? null}
              decidedByEmail={ticket.decidedByEmail ?? null}
              comment={ticket.decisionComment ?? null}
              candidateStatus={ticket.candidateStatus ?? null}
            />
          ) : (
            <div className="flex items-center gap-2 mt-2.5">
              <button
                onClick={() => decide('approve')}
                disabled={busy}
                className="inline-flex items-center gap-1 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{ backgroundColor: 'var(--success)', color: '#fff', opacity: busy ? 0.6 : 1 }}
              >
                <CheckCircle size={12} />
                Approve
              </button>
              <button
                onClick={() => decide('reject')}
                disabled={busy}
                className="inline-flex items-center gap-1 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{ backgroundColor: 'var(--danger)', color: '#fff', opacity: busy ? 0.6 : 1 }}
              >
                <XCircle size={12} />
                Reject
              </button>
              <button
                type="button"
                onClick={() => setShowComment((v) => !v)}
                className="text-[11px] transition-opacity hover:opacity-80"
                style={{ color: 'var(--text-muted)' }}
              >
                {showComment ? 'Hide comment' : 'Add comment'}
              </button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

/**
 * Read-only banner shown on rows that already have a decision (any approver). Surfaces the
 * decision (with colour cue), who made it, when, the comment if any, and the candidate's
 * current status (which may have moved past Approved while the user wasn't looking).
 */
function DecisionBanner({
  decision,
  decidedAt,
  decidedByName,
  decidedByEmail,
  comment,
  candidateStatus,
}: {
  decision: 'Approved' | 'Rejected';
  decidedAt: string | null;
  decidedByName: string | null;
  decidedByEmail: string | null;
  comment: string | null;
  candidateStatus: string | null;
}) {
  const isApproved = decision === 'Approved';
  const decider = decidedByName ?? decidedByEmail ?? 'someone';
  return (
    <div
      className="mt-2.5 rounded-lg border px-3 py-2 text-[12px] space-y-1"
      style={{
        borderColor: isApproved ? 'var(--success)' : 'var(--danger)',
        backgroundColor: isApproved ? 'var(--success-bg)' : 'var(--danger-bg)',
        color: isApproved ? 'var(--success)' : 'var(--danger)',
      }}
    >
      <div className="inline-flex items-center gap-2 font-medium flex-wrap">
        {isApproved ? <CheckCircle size={12} /> : <XCircle size={12} />}
        <span>
          {isApproved ? 'Approved' : 'Rejected'} by{' '}
          <span title={decidedByEmail ?? undefined}>{decider}</span>
        </span>
        {decidedAt && (
          <span className="font-normal" style={{ color: 'var(--text-muted)' }}>
            · {formatDistanceToNow(new Date(decidedAt), { addSuffix: true })}
          </span>
        )}
        {candidateStatus && candidateStatus !== 'Pending' && candidateStatus !== 'Unknown' && (
          <span className="font-normal" style={{ color: 'var(--text-muted)' }}>
            · candidate is now <span className="font-medium">{candidateStatus}</span>
          </span>
        )}
      </div>
      {comment && (
        <p style={{ color: 'var(--text-secondary)' }}>“{comment}”</p>
      )}
    </div>
  );
}

/**
 * Inline role + person picker. Directory-searched, falls back to manual email entry.
 * Same UX as the assign form on the promotion detail page.
 */
function AssignForm({
  onSave,
  onCancel,
}: {
  onSave: (role: string, assignee: { email: string; displayName: string }) => Promise<void>;
  onCancel: () => void;
}) {
  const [roleInput, setRoleInput] = useState('');
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Array<{ id: string; displayName: string; email: string }>>([]);
  const [searching, setSearching] = useState(false);
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [knownRoles, setKnownRoles] = useState<string[]>([]);
  const datalistId = useMemo(() => `queue-assign-roles-${Math.random().toString(36).slice(2, 8)}`, []);

  useEffect(() => {
    api.listPromotionRoles()
      .then((d) => setKnownRoles(d.roles || []))
      .catch(() => setKnownRoles([]));
  }, []);

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) { setResults([]); return; }
    let cancelled = false;
    setSearching(true);
    const timer = setTimeout(async () => {
      try {
        const res = await api.searchPromotionUsers(q);
        if (!cancelled) setResults(res.users ?? []);
      } catch {
        if (!cancelled) setResults([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 250);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [query]);

  const submit = async (email: string, displayName: string) => {
    const role = roleInput.trim();
    if (!role) { setErr('Role is required'); return; }
    setSaving(true);
    setErr(null);
    try {
      await onSave(role, { email, displayName });
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  const submitManual = () => {
    const q = query.trim();
    if (!q.includes('@') || !q.includes('.')) return;
    void submit(q, q);
  };

  return (
    <div
      className="mt-2 p-3 rounded-lg border space-y-2"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <input
        autoFocus
        list={datalistId}
        value={roleInput}
        onChange={(e) => setRoleInput(e.target.value)}
        placeholder="Role (e.g. QA, reviewer)"
        className="w-full rounded-lg border px-3 py-1.5 text-[12px]"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
        disabled={saving}
        onKeyDown={(e) => { if (e.key === 'Escape') onCancel(); }}
      />
      <datalist id={datalistId}>
        {knownRoles.map((r) => (
          <option key={r} value={roleDisplay({ role: r })} />
        ))}
      </datalist>
      <div className="relative">
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search directory (name or email)..."
          className="w-full rounded-lg border px-3 py-1.5 text-[12px]"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
          disabled={saving}
          onKeyDown={(e) => { if (e.key === 'Escape') onCancel(); if (e.key === 'Enter' && results.length === 0) submitManual(); }}
        />
        {query.trim().length >= 2 && (
          <div
            className="absolute left-0 right-0 mt-1 rounded-lg border shadow-lg max-h-40 overflow-y-auto z-10"
            style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
          >
            {searching && (
              <div className="px-3 py-2 text-[11px]" style={{ color: 'var(--text-muted)' }}>Searching...</div>
            )}
            {!searching && results.length === 0 && (
              <button
                type="button"
                onClick={submitManual}
                disabled={saving || !roleInput.trim()}
                className="w-full text-left px-3 py-2 text-[12px] flex flex-col hover:opacity-80"
                style={{ color: 'var(--text-primary)' }}
              >
                <span className="font-medium">Use &ldquo;{query.trim()}&rdquo; as email</span>
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>No directory matches.</span>
              </button>
            )}
            {!searching && results.map((u) => (
              <button
                key={u.id}
                type="button"
                onClick={() => void submit(u.email, u.displayName)}
                disabled={saving || !roleInput.trim()}
                className="w-full text-left px-3 py-2 text-[12px] flex flex-col hover:opacity-80"
                style={{ color: 'var(--text-primary)' }}
                title={!roleInput.trim() ? 'Pick a role first' : undefined}
              >
                <span className="font-medium">{u.displayName}</span>
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>{u.email}</span>
              </button>
            ))}
          </div>
        )}
      </div>
      {err && <p className="text-[11px]" style={{ color: 'var(--danger)' }}>{err}</p>}
      <div className="flex items-center gap-2 pt-0.5">
        <button
          onClick={onCancel}
          className="text-[11px] transition-opacity hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          Cancel
        </button>
      </div>
    </div>
  );
}

