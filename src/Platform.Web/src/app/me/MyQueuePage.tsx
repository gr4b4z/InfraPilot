import { useEffect, useState, useMemo } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api } from '@/lib/api';
import type { PendingAssignee, PendingTicket, WorkItemDecision } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { roleDisplay } from '@/lib/roleLabel';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { WorkItemParticipants } from '@/components/promotions/WorkItemParticipants';
import { decisionStyle, workItemDetailPath } from '@/lib/workItem';
import { formatDistanceToNow } from 'date-fns';
import {
  Ticket,
  Ban,
  CheckCircle,
  XCircle,
  ExternalLink,
  ArrowRight,
  Inbox,
  Unlink,
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
 * "My queue" page — work items awaiting the current user's signoff. Reads
 * GET /api/work-items/me/pending which returns one row per (work item × candidate)
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
  // Decider narrowing — only meaningful on the "decided" view. Filters by who clicked
  // Approve / Reject ("Me" = the current user's own decisions). Persisted via localStorage.
  const [deciderFilter, setDeciderFilter] = useState<DeciderFilterValue>(() => loadDeciderFilter());
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
    decider: DeciderFilterValue,
  ) => {
    setLoading(true);
    setError(null);
    try {
      const apiArg = toApiArg(filter, currentUserEmail, status, tf, decider);
      const res = await api.getMyPendingWorkItems(apiArg);
      setTickets(res.tickets ?? []);
      setAssignees(res.assignees ?? []);
      setRoles(res.roles ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load work items');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void fetchData(assigneeFilter, statusFilter, timeFrame, deciderFilter);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assigneeFilter, statusFilter, timeFrame, deciderFilter, currentUserEmail]);

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

  const handleDeciderChange = (next: DeciderFilterValue) => {
    saveDeciderFilter(next);
    setDeciderFilter(next);
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
          Work items queue
        </h1>
        <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
          Work items awaiting your signoff across all products and environments.
        </p>
      </div>

      <div className="flex items-center gap-2 flex-wrap">
        <StatusFilter value={statusFilter} onChange={handleStatusChange} />
        {/* Time frame + decider narrowing are only meaningful on the decided view. */}
        {statusFilter === 'decided' && (
          <>
            <TimeFrameFilter value={timeFrame} onChange={handleTimeFrameChange} />
            <DeciderFilter
              value={deciderFilter}
              onChange={handleDeciderChange}
              deciders={assignees}
              currentUserEmail={currentUserEmail}
            />
          </>
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
              ? 'No work items match the current filters.'
              : statusFilter === 'decided'
                ? decidedEmptyTitle(deciderFilter)
                : emptyStateTitle(assigneeFilter)}
          </p>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            {tickets.length > 0 && hasActiveScope(scopeFilter)
              ? 'Widen the product / service / target-env picks to see more rows.'
              : statusFilter === 'decided'
                ? 'Try a wider time frame, or switch the decider to "Anyone".'
                : emptyStateBody(assigneeFilter)}
          </p>
        </div>
      ) : (
        <div className="space-y-2">
          {filteredTickets.map((t) => (
            <TicketRow
              key={`${t.workItemKey}-${t.candidateId}-${t.decidedAt ?? 'pending'}-${t.decidedByEmail ?? ''}`}
              ticket={t}
              onChanged={() => fetchData(assigneeFilter, statusFilter, timeFrame, deciderFilter)}
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
  decider: DeciderFilterValue,
): { role?: string; assignee?: string; status?: 'pending' | 'decided'; since?: string } | undefined {
  // Decision-history views ignore role/participant narrowing but DO honour the decider filter:
  // `assignee` here means "who decided" (a single email; "Me" → current user). The backend
  // maps this param to WorkItemApproval.ApproverEmail on the decided path.
  if (status === 'decided') {
    const since = timeFrameToSince(timeFrame);
    const decidedBy = deciderToEmail(decider, currentUserEmail);
    return { status, ...(since ? { since } : {}), ...(decidedBy ? { assignee: decidedBy } : {}) };
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
        <option value="decided">Decided</option>
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

// ── Decider filter (only meaningful on "decided" view) ───────────────────────────────────
// Narrows the decision history by who clicked Approve / Reject. Distinct from the pending
// AssigneeFilter (which narrows by work-item participant/role) — a decider has no role and
// "unassigned" is not a valid choice, so this is its own lightweight picker.

export type DeciderFilterValue =
  | { mode: 'all' }
  | { mode: 'me' }
  | { mode: 'person'; email: string; displayName: string };

const DECIDER_FILTER_STORAGE_KEY = 'me.queue.deciderFilter';

const DECIDER_ANYONE = '__all__';
const DECIDER_ME = '__me__';

function loadDeciderFilter(): DeciderFilterValue {
  try {
    const raw = window.localStorage.getItem(DECIDER_FILTER_STORAGE_KEY);
    if (!raw) return { mode: 'all' };
    const parsed = JSON.parse(raw);
    if (parsed?.mode === 'me') return { mode: 'me' };
    if (
      parsed?.mode === 'person' &&
      typeof parsed.email === 'string' &&
      typeof parsed.displayName === 'string'
    ) {
      return { mode: 'person', email: parsed.email, displayName: parsed.displayName };
    }
  } catch {
    // Ignore — corrupted entry; fall through to default.
  }
  return { mode: 'all' };
}

function saveDeciderFilter(value: DeciderFilterValue): void {
  try {
    window.localStorage.setItem(DECIDER_FILTER_STORAGE_KEY, JSON.stringify(value));
  } catch {
    // Ignore — quota or disabled storage.
  }
}

/** Resolves the decider filter to a single email for the API, or undefined for "Anyone". */
function deciderToEmail(value: DeciderFilterValue, currentUserEmail: string): string | undefined {
  if (value.mode === 'me') return currentUserEmail || undefined;
  if (value.mode === 'person') return value.email;
  return undefined;
}

function DeciderFilter({
  value,
  onChange,
  deciders,
  currentUserEmail,
}: {
  value: DeciderFilterValue;
  onChange: (next: DeciderFilterValue) => void;
  /** Decider rollup (email, displayName, count) from the decided endpoint, pre-narrowing. */
  deciders: PendingAssignee[];
  currentUserEmail: string;
}) {
  // Dedupe by email — the decided endpoint already returns one row per decider, but guard
  // anyway. Exclude the current user from the named list; "Me" covers them.
  const people = useMemo(() => {
    const seen = new Set<string>();
    const out: Array<{ email: string; displayName: string }> = [];
    for (const d of deciders) {
      if (!d.email || seen.has(d.email.toLowerCase())) continue;
      if (d.email.toLowerCase() === currentUserEmail.toLowerCase()) continue;
      seen.add(d.email.toLowerCase());
      out.push({ email: d.email, displayName: d.displayName });
    }
    return out;
  }, [deciders, currentUserEmail]);

  // If the persisted person is no longer in the list, render "Anyone" rather than a stale pick.
  const selectValue = useMemo(() => {
    if (value.mode === 'me') return DECIDER_ME;
    if (value.mode === 'person') {
      const stillVisible = people.some(
        (p) => p.email.toLowerCase() === value.email.toLowerCase(),
      );
      return stillVisible ? `email:${value.email}` : DECIDER_ANYONE;
    }
    return DECIDER_ANYONE;
  }, [value, people]);

  const handleChange = (next: string) => {
    if (next === DECIDER_ANYONE) return onChange({ mode: 'all' });
    if (next === DECIDER_ME) return onChange({ mode: 'me' });
    if (next.startsWith('email:')) {
      const email = next.slice('email:'.length);
      const person = people.find((p) => p.email === email);
      if (person) onChange({ mode: 'person', email: person.email, displayName: person.displayName });
    }
  };

  return (
    <label
      className="inline-flex items-center gap-1.5 text-[12px]"
      style={{ color: 'var(--text-muted)' }}
    >
      <span>Decided by</span>
      <select
        value={selectValue}
        onChange={(e) => handleChange(e.target.value)}
        className="rounded-lg border px-2 py-1.5 text-[12px] font-medium"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          color: 'var(--text-primary)',
        }}
      >
        <option value={DECIDER_ANYONE}>Anyone</option>
        <option value={DECIDER_ME}>Me</option>
        {people.length > 0 && (
          <optgroup label="Deciders">
            {people.map((p) => (
              <option key={p.email} value={`email:${p.email}`}>
                {p.displayName}
              </option>
            ))}
          </optgroup>
        )}
      </select>
    </label>
  );
}

function decidedEmptyTitle(decider: DeciderFilterValue): string {
  switch (decider.mode) {
    case 'me':
      return 'No decisions you made in this time frame.';
    case 'person':
      return `No decisions by ${decider.displayName} in this time frame.`;
    default:
      return 'No decisions recorded in this time frame.';
  }
}

function emptyStateTitle(filter: AssigneeFilterValue): string {
  const roleLabel = filter.role ? roleDisplay({ role: filter.role }) : null;
  switch (filter.mode) {
    case 'all':
      return roleLabel
        ? `No work items where someone is ${roleLabel}.`
        : 'No work items awaiting your signoff.';
    case 'me':
      return roleLabel
        ? `No work items where you're the ${roleLabel}.`
        : 'Nothing assigned to you right now.';
    case 'unassigned':
      return roleLabel
        ? `No work items without a ${roleLabel} assigned.`
        : 'No unassigned work items in your authorized list.';
    case 'person':
      return roleLabel
        ? `No work items with ${filter.displayName} as ${roleLabel}.`
        : `No work items with ${filter.displayName} as any role.`;
  }
}

function emptyStateBody(filter: AssigneeFilterValue): string {
  switch (filter.mode) {
    case 'all':
      return filter.role
        ? 'Pick a different role or "Any role" to widen the queue.'
        : 'New work items will appear here as promotions roll through your environments.';
    case 'me':
      return 'Switch the assignee to "Anyone" to see the full queue you can sign off on.';
    case 'unassigned':
      return filter.role
        ? 'Work items where this role is empty will show up here.'
        : 'Work items without a named QA / reviewer / assignee will show up here.';
    case 'person':
      return 'Try a different person, or switch to "Anyone".';
  }
}

/**
 * One queue row. Navigational, not transactional: sign-off (Approve / Block / Reject) and the
 * discussion thread live on the work-item detail page, so the row's job is to surface state and get
 * you there. Assigning people stays inline — it's the one edit that's useful while triaging a list.
 *
 * The whole tile is the click target for "open details", matching the promotions list. Regions with
 * their own behaviour (the tracker link, the participant controls) swallow the click rather than
 * every leaf control having to know about the tile.
 */
function TicketRow({
  ticket,
  onChanged,
}: {
  ticket: PendingTicket;
  onChanged: () => void;
}) {
  const navigate = useNavigate();
  // Decided rows are read-only history: decision badge + decider + comment, and no participant
  // editing. The candidate may also have moved on (Approved/Deployed/Rejected/Superseded) so we
  // surface its current status as a hint.
  const decided = ticket.decision != null;
  // Undecided row whose promotion is gone: superseded without the replacement picking the ticket up,
  // or rejected. The item is still ours to resolve, so it stays in the queue — flagged, because the
  // service/version shown belongs to a promotion that is no longer going anywhere.
  const orphaned =
    !decided && !!ticket.candidateStatus && ticket.candidateStatus !== 'Pending';
  const detailPath = workItemDetailPath(ticket.workItemKey, ticket.product, ticket.targetEnv);

  return (
    <div
      className="card-hover rounded-xl border p-4 cursor-pointer"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
      onClick={() => navigate(detailPath)}
    >
      <div className="flex items-start gap-3">
        <Ticket size={16} style={{ color: 'var(--text-muted)', marginTop: 2, flexShrink: 0 }} />
        <div className="flex-1 min-w-0">
          {/* Title row. The key goes to the in-app detail page; the tracker link is a bare icon. */}
          <div className="flex items-baseline gap-2 min-w-0">
            <Link
              to={detailPath}
              className="text-[13px] font-semibold hover:underline shrink-0"
              style={{ color: 'var(--accent)' }}
              title={`Open ${ticket.workItemKey} details`}
            >
              {ticket.workItemKey}
            </Link>
            {ticket.url && (
              <a
                href={ticket.url}
                target="_blank"
                rel="noopener noreferrer"
                onClick={(e) => e.stopPropagation()}
                className="shrink-0 transition-opacity hover:opacity-70"
                style={{ color: 'var(--text-muted)' }}
                title={`Open ${ticket.workItemKey} in ${ticket.provider ?? 'the tracker'}`}
                aria-label={`Open ${ticket.workItemKey} in ${ticket.provider ?? 'the tracker'}`}
              >
                <ExternalLink size={11} />
              </a>
            )}
            {ticket.title && (
              <span
                className="text-[12px] truncate"
                style={{ color: 'var(--text-secondary)' }}
                title={ticket.title}
              >
                {ticket.title}
              </span>
            )}
            {ticket.blockingPromotions > 1 && (
              <span
                className="badge shrink-0"
                style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
                title={`Referenced by ${ticket.blockingPromotions} pending promotion candidates`}
              >
                ×{ticket.blockingPromotions}
              </span>
            )}
            {orphaned && (
              <span
                className="badge shrink-0"
                style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
                title={`The promotion carrying this work item is ${ticket.candidateStatus}. The item still needs signing off.`}
              >
                <Unlink size={10} />
                No live promotion
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
              <EnvBadge env={ticket.sourceEnv} size="xs" />
              <ArrowRight size={11} style={{ color: 'var(--text-muted)' }} />
              <EnvBadge env={ticket.targetEnv} size="xs" />
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

          {/* Participants — each chip carries its own role icon, and the dashed "Assign" button is
              the empty state, so no separate header is needed here. The wrapper keeps assignment
              clicks from being read as "open the details page". */}
          <div onClick={(e) => e.stopPropagation()}>
            <WorkItemParticipants
              candidateId={ticket.candidateId}
              referenceKey={ticket.workItemKey}
              participants={ticket.participants ?? []}
              onChanged={onChanged}
              /* Orphan rows are read-only for assignment: their candidate is the dead one, which
                 isn't necessarily the candidate the detail page writes people to, so an edit here
                 could land somewhere the user never sees it. The detail page is the place. */
              readOnly={decided || orphaned}
            />
          </div>

          {decided && (
            <DecisionBanner
              decision={ticket.decision!}
              decidedAt={ticket.decidedAt ?? null}
              decidedByName={ticket.decidedByName ?? null}
              decidedByEmail={ticket.decidedByEmail ?? null}
              comment={ticket.decisionComment ?? null}
              candidateStatus={ticket.candidateStatus ?? null}
            />
          )}
        </div>
        <Link
          to={detailPath}
          className="shrink-0 self-center inline-flex items-center gap-1 text-[12px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--accent)' }}
        >
          Details
          <ArrowRight size={14} />
        </Link>
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
  decision: WorkItemDecision;
  decidedAt: string | null;
  decidedByName: string | null;
  decidedByEmail: string | null;
  comment: string | null;
  candidateStatus: string | null;
}) {
  const s = decisionStyle(decision);
  const decider = decidedByName ?? decidedByEmail ?? 'someone';
  const Icon = decision === 'Approved' ? CheckCircle : decision === 'Blocked' ? Ban : XCircle;
  return (
    <div
      className="mt-2.5 rounded-lg border px-3 py-2 text-[12px] space-y-1"
      style={{ borderColor: s.color, backgroundColor: s.bg, color: s.color }}
    >
      <div className="inline-flex items-center gap-2 font-medium flex-wrap">
        <Icon size={12} />
        <span>
          {s.label} by <span title={decidedByEmail ?? undefined}>{decider}</span>
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
      {comment && <p style={{ color: 'var(--text-secondary)' }}>“{comment}”</p>}
    </div>
  );
}
