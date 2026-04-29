import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '@/lib/api';
import type { PendingTicket } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import {
  Ticket,
  CheckCircle,
  XCircle,
  ExternalLink,
  ArrowRight,
  Inbox,
  Clock,
} from 'lucide-react';
import {
  AssigneeFilter,
  loadAssigneeFilter,
  saveAssigneeFilter,
  type AssigneeFilterValue,
} from './AssigneeFilter';

/**
 * "My queue" page — tickets awaiting the current user's signoff. Reads
 * GET /api/work-items/me/pending which returns one row per (ticket × candidate)
 * after applying authority filters server-side (approver group, excluded role,
 * already-decided), so client-side rendering is straight-through.
 */
export function MyQueuePage() {
  const [tickets, setTickets] = useState<PendingTicket[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // Hydrate the filter from localStorage so the user's pick survives reloads. Only happens
  // on mount — subsequent updates flow through the onChange callback below.
  const [assigneeFilter, setAssigneeFilter] = useState<AssigneeFilterValue>(() => loadAssigneeFilter());
  // The auth store already carries the current user's email — same source PromotionDetailPage
  // uses for `currentUserEmail`. No extra API call needed; we just send this email to the
  // server when the user picks "Assigned to me".
  const currentUserEmail = useAuthStore((s) => s.user?.email ?? '');

  // Defined as an async function so the initial fetch from `useEffect` can be a
  // microtask (avoids the eslint react-hooks/set-state-in-effect rule and the
  // associated cascading-render warning) while still letting decision handlers
  // call `fetchData()` directly to refresh after Approve / Reject.
  const fetchData = async (filter: AssigneeFilterValue) => {
    setLoading(true);
    setError(null);
    try {
      const apiArg = toApiArg(filter, currentUserEmail);
      const res = await api.getMyPendingWorkItems(apiArg);
      setTickets(res.tickets ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load tickets');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void fetchData(assigneeFilter);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assigneeFilter, currentUserEmail]);

  const handleFilterChange = (next: AssigneeFilterValue) => {
    saveAssigneeFilter(next);
    setAssigneeFilter(next);
  };

  return (
    <div className="space-y-6">
      <div>
        <h1
          className="text-xl font-semibold tracking-tight"
          style={{ color: 'var(--text-primary)' }}
        >
          My ticket queue
        </h1>
        <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
          Work-items awaiting your signoff across all products and environments.
        </p>
      </div>

      <div className="flex items-center gap-2">
        <AssigneeFilter value={assigneeFilter} onChange={handleFilterChange} />
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
      ) : tickets.length === 0 ? (
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
            {emptyStateTitle(assigneeFilter)}
          </p>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            {emptyStateBody(assigneeFilter)}
          </p>
        </div>
      ) : (
        <div className="space-y-2">
          {tickets.map((t) => (
            <TicketRow
              key={`${t.workItemKey}-${t.candidateId}`}
              ticket={t}
              onChanged={() => fetchData(assigneeFilter)}
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
): { assignee?: string } | undefined {
  switch (filter.mode) {
    case 'all':
      return undefined;
    case 'me':
      // The endpoint always knows who the user is, but it treats `assignee` as the slot
      // we're filtering on — so even "Me" needs the email so the server matches it as a
      // person rather than as the caller. If the auth store hasn't populated the email
      // yet (rare race during initial mount), fall back to "all" rather than narrow to
      // an empty string and surface no results.
      if (!currentUserEmail) return undefined;
      return { assignee: currentUserEmail };
    case 'unassigned':
      return { assignee: 'unassigned' };
    case 'person':
      return { assignee: filter.email };
  }
}

function emptyStateTitle(filter: AssigneeFilterValue): string {
  switch (filter.mode) {
    case 'all':
      return 'No tickets awaiting your signoff.';
    case 'me':
      return 'Nothing assigned to you right now.';
    case 'unassigned':
      return 'No unassigned tickets in your authorized list.';
    case 'person':
      return `No tickets assigned to ${filter.displayName}.`;
  }
}

function emptyStateBody(filter: AssigneeFilterValue): string {
  switch (filter.mode) {
    case 'all':
      return 'New tickets will appear here as promotions roll through your environments.';
    case 'me':
      return 'Switch to "Anyone" to see the full queue you can sign off on.';
    case 'unassigned':
      return 'Tickets without a named QA / reviewer / assignee will show up here.';
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
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showComment, setShowComment] = useState(false);

  const decide = async (decision: 'approve' | 'reject') => {
    setBusy(true);
    setError(null);
    try {
      if (decision === 'approve') {
        await api.approveWorkItem(
          ticket.workItemKey,
          ticket.product,
          ticket.targetEnv,
          comment || undefined,
        );
      } else {
        await api.rejectWorkItem(
          ticket.workItemKey,
          ticket.product,
          ticket.targetEnv,
          comment || undefined,
        );
      }
      // Always refresh — the row may disappear (decision recorded) or shift
      // (cascade). The list is server-built per request so a refetch is the
      // simplest source of truth.
      onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Action failed');
    } finally {
      setBusy(false);
    }
  };

  // The pending-tickets API doesn't surface the "first observed" timestamp for
  // a ticket → candidate pairing yet, so we don't render a fabricated number.
  // Wire a real value here once the API exposes it on PendingTicket.
  const daysWaiting: string | null = null;

  return (
    <div
      className="rounded-xl border p-4"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
      }}
    >
      <div className="flex items-start gap-3">
        <Ticket size={16} style={{ color: 'var(--text-muted)', marginTop: 2 }} />
        <div className="flex-1 min-w-0">
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
              <span
                className="text-[13px] font-semibold"
                style={{ color: 'var(--text-primary)' }}
              >
                {ticket.workItemKey}
              </span>
            )}
            {ticket.title && (
              <span
                className="text-[12px] truncate"
                style={{ color: 'var(--text-secondary)' }}
              >
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

          <div
            className="flex items-center gap-3 mt-1.5 text-[11px]"
            style={{ color: 'var(--text-muted)' }}
          >
            <Link
              to={`/promotions/${ticket.candidateId}`}
              className="inline-flex items-center gap-1 hover:underline"
              style={{ color: 'var(--accent)' }}
            >
              View candidate
              <ExternalLink size={10} />
            </Link>
            {daysWaiting !== null && (
              <span className="inline-flex items-center gap-1">
                <Clock size={10} />
                {daysWaiting}
              </span>
            )}
          </div>

          {showComment && (
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

          <div className="flex items-center gap-2 mt-2.5">
            <button
              onClick={() => decide('approve')}
              disabled={busy}
              className="inline-flex items-center gap-1 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--success)',
                color: '#fff',
                opacity: busy ? 0.6 : 1,
              }}
            >
              <CheckCircle size={12} />
              Approve
            </button>
            <button
              onClick={() => decide('reject')}
              disabled={busy}
              className="inline-flex items-center gap-1 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--danger)',
                color: '#fff',
                opacity: busy ? 0.6 : 1,
              }}
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
        </div>
      </div>
    </div>
  );
}

