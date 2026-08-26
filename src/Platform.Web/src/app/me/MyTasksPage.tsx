import { useMemo } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { formatDistanceToNow } from 'date-fns';
import {
  ArrowRight,
  CheckCircle,
  Clock,
  ExternalLink,
  GitPullRequest,
  RefreshCw,
  Ticket,
  Unlink,
  UserPlus,
  XCircle,
} from 'lucide-react';
import type { PendingTicket, PromotionCandidate } from '@/lib/api';
import { ComboBox, type ComboOption } from '@/components/ui/ComboBox';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import { ROW_ACTION_ATTR } from '@/lib/keys';
import { PromotionRoute } from '@/components/promotions/PromotionRoute';
import { WorkItemEnvironments } from '@/components/promotions/WorkItemEnvironments';
import { MissingRolesBadge } from '@/components/promotions/MissingRoles';
import { workItemDetailPath } from '@/lib/workItem';
import { roleDisplay } from '@/lib/roleLabel';
import { useAuthStore } from '@/stores/authStore';
import { useDocumentTitle } from '@/lib/pageTitle';
import { useMyTasksStore } from '@/stores/myTasksStore';

/**
 * "My tasks" — everything waiting on the signed-in user, in one list. Reached from the topbar
 * bell, whose badge shows this page's total.
 *
 * Read-only by design: each row links to the place the decision actually gets made (the promotion
 * detail page, the work-item detail page). Approving from an inbox means approving without having
 * read the diff, the comments, or the rest of the bundle — the two clicks are the point.
 *
 * Data comes from the shared rollup in {@link useMyTasksStore}, so the counts here are the same
 * numbers the bell and the sidebar show.
 */
export function MyTasksPage() {
  const promotions = useMyTasksStore((s) => s.promotions);
  const workItems = useMyTasksStore((s) => s.workItems);
  const unassignedWorkItems = useMyTasksStore((s) => s.unassignedWorkItems);
  const loading = useMyTasksStore((s) => s.loading);
  const loaded = useMyTasksStore((s) => s.loaded);
  const error = useMyTasksStore((s) => s.error);
  const refresh = useMyTasksStore((s) => s.refresh);
  const total = promotions.length + workItems.length + unassignedWorkItems.length;

  // Environment filter, kept in the URL (`?env=`) so a filtered view survives a refresh and can be
  // handed to somebody as a link. Filters on each task's *target* environment — the env the pending
  // decision gates — not on where a work item's version happens to be deployed already.
  const [searchParams, setSearchParams] = useSearchParams();
  const envFilter = searchParams.get('env') ?? '';
  const setEnvFilter = (next: string) =>
    setSearchParams(
      (prev) => {
        const params = new URLSearchParams(prev);
        if (next) params.set('env', next);
        else params.delete('env');
        return params;
      },
      { replace: true },
    );

  // Built from the unfiltered lists, so picking an environment never removes the others from the
  // dropdown — the field keeps working as a browser of what's actually waiting.
  const envOptions = useMemo<ComboOption[]>(() => {
    const counts = new Map<string, number>();
    for (const env of [
      ...promotions.map((c) => c.targetEnv),
      ...workItems.map((t) => t.targetEnv),
      ...unassignedWorkItems.map((t) => t.targetEnv),
    ]) {
      counts.set(env, (counts.get(env) ?? 0) + 1);
    }
    return [...counts.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([value, count]) => ({ value, hint: `${count} task${count === 1 ? '' : 's'}` }));
  }, [promotions, workItems, unassignedWorkItems]);

  // Case-insensitive substring, matching how the ComboBox narrows its own dropdown — a half-typed
  // "pro" already shows the production rows instead of blanking the page until Enter.
  const needle = envFilter.trim().toLowerCase();
  const matchesEnv = (env: string) => !needle || env.toLowerCase().includes(needle);
  const visiblePromotions = promotions.filter((c) => matchesEnv(c.targetEnv));
  const visibleWorkItems = workItems.filter((t) => matchesEnv(t.targetEnv));
  const visibleUnassigned = unassignedWorkItems.filter((t) => matchesEnv(t.targetEnv));

  // No count in the title: this page is per-viewer, so a number here would be the sender's inbox
  // depth, not the recipient's. The bell badge is where a live count belongs.
  useDocumentTitle(['My tasks']);

  return (
    <div className="space-y-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1
            className="text-xl font-semibold tracking-tight"
            style={{ color: 'var(--text-primary)' }}
          >
            My tasks
          </h1>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            Promotions and work items awaiting your action.
          </p>
        </div>
        <div className="shrink-0 flex items-center gap-2">
          <ComboBox
            value={envFilter}
            onChange={setEnvFilter}
            options={envOptions}
            placeholder="Any environment"
            ariaLabel="Environment"
            clearable
            className="w-44"
          />
          <button
            type="button"
            onClick={() => void refresh()}
            disabled={loading}
            className="shrink-0 inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[12px] font-medium transition-opacity"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-primary)',
              color: 'var(--text-secondary)',
              opacity: loading ? 0.6 : 1,
            }}
          >
            <RefreshCw size={12} className={loading ? 'animate-spin' : undefined} />
            Refresh
          </button>
        </div>
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
          <span className="text-[13px] font-medium">
            {error} Counts below may be incomplete.
          </span>
        </div>
      )}

      {!loaded && loading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="skeleton h-20" />
          ))}
        </div>
      ) : total === 0 ? (
        <AllCaughtUp />
      ) : (
        <div className="space-y-8">
          <Section
            icon={GitPullRequest}
            title="Promotions awaiting your approval"
            count={visiblePromotions.length}
            allLink={{ to: '/promotions', label: 'Open promotions' }}
          >
            {visiblePromotions.map((c, index) => (
              <PromotionTaskRow key={c.id} index={index} candidate={c} />
            ))}
          </Section>

          <Section
            icon={Ticket}
            title="Work items assigned to you"
            count={visibleWorkItems.length}
            allLink={{ to: '/me/work-items', label: 'Open work items queue' }}
          >
            {visibleWorkItems.map((t, index) => (
              <WorkItemTaskRow
                key={`${t.workItemKey}-${t.candidateId}`}
                index={index}
                ticket={t}
              />
            ))}
          </Section>

          {/* Work items whose promotion policy asks for a role nobody is in. Last of the three: the
             action is to find an owner rather than to decide something, so it shouldn't sit above the
             items already waiting on this user. */}
          <Section
            icon={UserPlus}
            title="Work items with nobody assigned"
            count={visibleUnassigned.length}
            allLink={{ to: '/me/work-items', label: 'Open work items queue' }}
          >
            {visibleUnassigned.map((t, index) => (
              <WorkItemTaskRow
                key={`${t.workItemKey}-${t.candidateId}`}
                index={index}
                ticket={t}
                variant="unassigned"
              />
            ))}
          </Section>
        </div>
      )}
    </div>
  );
}

function AllCaughtUp() {
  return (
    <div
      className="flex flex-col items-center justify-center py-20 rounded-xl border"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div
        className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
        style={{ backgroundColor: 'var(--success-bg)', color: 'var(--success)' }}
      >
        <CheckCircle size={24} />
      </div>
      <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>
        You're all caught up.
      </p>
      <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
        Promotions you can approve, work items assigned to you, and work items still waiting for
        someone will show up here.
      </p>
    </div>
  );
}

/**
 * One task group. Rendered even when empty (with a one-line "nothing here") so the page keeps a
 * stable shape — a section that vanishes when it empties makes the remaining one look like the
 * whole story.
 */
function Section({
  icon: Icon,
  title,
  count,
  allLink,
  children,
}: {
  icon: typeof Ticket;
  title: string;
  count: number;
  allLink: { to: string; label: string };
  children: React.ReactNode;
}) {
  return (
    <section>
      <div className="flex items-center justify-between gap-3 mb-3">
        <h2
          className="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wider"
          style={{ color: 'var(--text-muted)' }}
        >
          <Icon size={13} />
          {title}
          <span
            className="px-1.5 rounded-full text-[11px] font-semibold normal-case tracking-normal"
            style={{
              backgroundColor: count > 0 ? 'var(--warning-bg)' : 'var(--bg-secondary)',
              color: count > 0 ? 'var(--warning)' : 'var(--text-muted)',
            }}
          >
            {count}
          </span>
        </h2>
        <Link
          to={allLink.to}
          className="text-[12px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--accent)' }}
        >
          {allLink.label}
        </Link>
      </div>
      {count === 0 ? (
        <p
          className="rounded-xl border px-4 py-3 text-[12px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-muted)',
          }}
        >
          Nothing here right now.
        </p>
      ) : (
        // Each section is its own arrow-key list: the two groups are separate concerns, and running
        // one cursor across both would let ArrowDown walk from a promotion into a work item.
        <KeyboardList className="space-y-2" count={count} ariaLabel={title}>
          {children}
        </KeyboardList>
      )}
    </section>
  );
}

function PromotionTaskRow({ index, candidate }: { index: number; candidate: PromotionCandidate }) {
  const navigate = useNavigate();
  const workItemCount = (candidate.sourceEventReferences ?? []).filter(
    (r) => r.type === 'work-item',
  ).length;

  const rowProps = useKeyboardListRow(index, () => navigate(`/promotions/${candidate.id}`), {
    label: `${candidate.product} / ${candidate.service} to ${candidate.targetEnv} — awaiting your approval. Open promotion.`,
  });

  return (
    <div
      {...rowProps}
      className="card-hover rounded-xl border p-4 flex items-start gap-3 cursor-pointer"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        borderLeft: '3px solid var(--warning)',
      }}
    >
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2 mb-1 flex-wrap">
          <h3
            className="text-[14px] font-semibold truncate"
            style={{ color: 'var(--text-primary)' }}
          >
            {candidate.product} / {candidate.service}
          </h3>
          <span
            className="badge"
            style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
          >
            <Clock size={10} />
            Awaiting your approval
          </span>
        </div>
        <div className="text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          <PromotionRoute
            product={candidate.product}
            service={candidate.service}
            sourceEnv={candidate.sourceEnv}
            targetEnv={candidate.targetEnv}
            version={candidate.version}
            targetCurrentVersion={candidate.targetCurrentVersion}
            sourceBranch={candidate.sourceBranch}
          />
        </div>
        <div
          className="flex items-center gap-4 mt-2 text-[11px]"
          style={{ color: 'var(--text-muted)' }}
        >
          <span className="inline-flex items-center gap-1">
            <Clock size={10} />
            {formatDistanceToNow(new Date(candidate.createdAt), { addSuffix: true })}
          </span>
          <span className="inline-flex items-center gap-1">
            <Ticket size={10} />
            {workItemCount === 0
              ? 'No work items'
              : `${workItemCount} work item${workItemCount === 1 ? '' : 's'}`}
          </span>
        </div>
      </div>
      <Link
        to={`/promotions/${candidate.id}`}
        className="shrink-0 self-center inline-flex items-center gap-1 text-[12px] font-medium transition-opacity hover:opacity-80"
        style={{ color: 'var(--accent)' }}
      >
        Review
        <ArrowRight size={14} />
      </Link>
    </div>
  );
}

function WorkItemTaskRow({
  index,
  ticket,
  variant = 'assigned',
}: {
  index: number;
  ticket: PendingTicket;
  /**
   * `assigned` — the user holds a policy-required role on this item, so the ask is a sign-off.
   * `unassigned` — nobody holds one, so the ask is to put somebody on it. Only the accent and the
   * call-to-action differ; the row's content is the same either way.
   */
  variant?: 'assigned' | 'unassigned';
}) {
  const navigate = useNavigate();
  const currentUserEmail = useAuthStore((s) => s.user?.email ?? '');
  const detailPath = workItemDetailPath(ticket.workItemKey, ticket.product, ticket.targetEnv);
  // Which hat the user is wearing on this item — the reason it's in their list at all.
  const myRoles = (ticket.participants ?? [])
    .filter((p) => (p.email ?? '').toLowerCase() === currentUserEmail.toLowerCase())
    .map((p) => roleDisplay(p));
  // The carrying promotion has moved on (superseded / rejected) but the item is still unsigned.
  const orphaned = !!ticket.candidateStatus && ticket.candidateStatus !== 'Pending';
  const unassigned = variant === 'unassigned';

  const rowProps = useKeyboardListRow(index, () => navigate(detailPath), {
    label: `${ticket.workItemKey} — ${ticket.product} / ${ticket.service}, ${
      unassigned ? 'nobody assigned' : 'assigned to you'
    }. Open work item.`,
  });

  return (
    <div
      {...rowProps}
      className="card-hover rounded-xl border p-4 flex items-start gap-3 cursor-pointer"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        borderLeft: `3px solid ${unassigned ? 'var(--warning)' : 'var(--accent)'}`,
      }}
    >
      <Ticket size={16} style={{ color: 'var(--text-muted)', marginTop: 2, flexShrink: 0 }} />
      <div className="flex-1 min-w-0">
        <div className="flex items-baseline gap-2 flex-wrap min-w-0">
          <span className="text-[13px] font-semibold shrink-0" style={{ color: 'var(--accent)' }}>
            {ticket.workItemKey}
          </span>
          {ticket.url && (
            <a
              href={ticket.url}
              target="_blank"
              rel="noopener noreferrer"
              onClick={(e) => e.stopPropagation()}
              className="shrink-0 transition-opacity hover:opacity-70"
              style={{ color: 'var(--text-muted)' }}
              {...{ [ROW_ACTION_ATTR]: 'open-external' }}
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
              // Tooltip carries the tracker summary too when the visible title is the commit subject.
              title={ticket.subTitle ? `${ticket.title}\n${ticket.subTitle}` : ticket.title}
            >
              {ticket.title}
            </span>
          )}
          {myRoles.length > 0 && (
            <span
              className="badge shrink-0"
              style={{ backgroundColor: 'var(--accent-bg)', color: 'var(--accent)' }}
              title={`You are assigned as ${myRoles.join(', ')}`}
            >
              {myRoles.join(', ')}
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
          <MissingRolesBadge roles={ticket.missingRoles} />
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
          <WorkItemEnvironments environments={ticket.environments ?? []} />
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
      </div>
      <Link
        to={detailPath}
        className="shrink-0 self-center inline-flex items-center gap-1 text-[12px] font-medium transition-opacity hover:opacity-80"
        style={{ color: 'var(--accent)' }}
      >
        {unassigned ? 'Assign' : 'Sign off'}
        <ArrowRight size={14} />
      </Link>
    </div>
  );
}
