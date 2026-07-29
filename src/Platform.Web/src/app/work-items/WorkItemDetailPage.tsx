import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { api } from '@/lib/api';
import type {
  PromotionSourceEventParticipant,
  PromotionStatus,
  WorkItemComment,
  WorkItemDecision,
  WorkItemDetail,
} from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { decisionStyle, providerLabel, referringCandidateId, shortHash } from '@/lib/workItem';
import { refreshMyTasks } from '@/stores/myTasksStore';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { CopyEmailButton } from '@/components/deployments/CopyEmailButton';
import { WorkItemParticipants } from '@/components/promotions/WorkItemParticipants';
import { WorkItemEnvironments } from '@/components/promotions/WorkItemEnvironments';
import { format, formatDistanceToNow } from 'date-fns';
import {
  ArrowLeft,
  ArrowRight,
  Ban,
  CheckCircle,
  ExternalLink,
  FileText,
  GitCommitHorizontal,
  GitPullRequest,
  MessageSquare,
  Ticket,
  Users,
  XCircle,
  Edit2,
  Trash2,
} from 'lucide-react';

/**
 * Work-item detail page — the one place a work item is managed end to end: sign it off (Approve /
 * Block / Reject), discuss it, and assign the people responsible for it.
 *
 * Identity is the triple `(key, product, targetEnv)` — the grain decisions and comments key on —
 * with product and target env arriving as query params. Everything renders from a single
 * `GET /api/work-items/{key}/detail` call; every mutation refetches it, because a decision can
 * cascade (a rejection terminates the candidate, an approval can auto-promote it) and the page must
 * show the new truth rather than a guess at it.
 */

const CANDIDATE_STATUS_COLOR: Record<PromotionStatus, string> = {
  Pending: 'var(--warning)',
  Approved: 'var(--info)',
  Deploying: 'var(--accent)',
  Deployed: 'var(--success)',
  Superseded: 'var(--text-muted)',
  Rejected: 'var(--danger)',
};

export function WorkItemDetailPage() {
  const { key: rawKey } = useParams<{ key: string }>();
  const [searchParams] = useSearchParams();
  const workItemKey = rawKey ?? '';
  const product = searchParams.get('product') ?? '';
  const targetEnv = searchParams.get('targetEnv') ?? '';
  // Set when we were opened from a promotion — drives the breadcrumb back to it.
  const fromCandidateId = referringCandidateId(searchParams.get('from'));
  const currentUserEmail = useAuthStore((s) => s.user?.email ?? '');

  const [detail, setDetail] = useState<WorkItemDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!workItemKey || !product || !targetEnv) {
      setError('This link is missing the product or target environment.');
      setLoading(false);
      return;
    }
    try {
      const next = await api.getWorkItemDetail(workItemKey, product, targetEnv);
      setDetail(next);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load work item');
    } finally {
      setLoading(false);
    }
  }, [workItemKey, product, targetEnv]);

  useEffect(() => {
    void load();
  }, [load]);

  if (loading) {
    return (
      <div className="max-w-4xl mx-auto space-y-4">
        <div className="skeleton h-8 w-48" />
        <div className="skeleton h-64" />
      </div>
    );
  }

  if (!detail) {
    return (
      <div className="flex flex-col items-center justify-center h-64 gap-2">
        <XCircle size={24} style={{ color: 'var(--danger)' }} />
        <p className="text-[14px] font-medium" style={{ color: 'var(--danger)' }}>
          {error ?? 'Work item not found'}
        </p>
        <Link
          to={fromCandidateId ? `/promotions/${fromCandidateId}` : '/me/work-items'}
          className="text-[13px] font-medium"
          style={{ color: 'var(--accent)' }}
        >
          {fromCandidateId ? 'Back to promotion' : 'Back to work items'}
        </Link>
      </div>
    );
  }

  // First decision wins for the headline state: the API keeps one row per approver and the trail
  // below shows every one of them, so the summary badge only needs the canonical outcome.
  const headline = detail.approvals[0] ?? null;
  const headlineStyle = headline ? decisionStyle(headline.decision) : null;
  // The promotion we were opened from, when it's one this work item still lists. A superseded
  // referrer won't resolve (the API omits those), so the breadcrumb still links back by id but
  // without the service/edge suffix.
  const referrer = fromCandidateId
    ? (detail.candidates.find((c) => c.id === fromCandidateId) ?? null)
    : null;

  return (
    <div className="max-w-4xl mx-auto space-y-6">
      {fromCandidateId ? (
        <Link
          to={`/promotions/${fromCandidateId}`}
          className="inline-flex items-center gap-1.5 text-[12px] font-medium transition-colors hover:text-[var(--accent)]"
          style={{ color: 'var(--text-muted)' }}
        >
          <ArrowLeft size={14} /> Back to promotion
          {referrer && (
            <span style={{ color: 'var(--text-muted)', opacity: 0.8 }}>
              · {referrer.service} {referrer.sourceEnv} → {referrer.targetEnv}
            </span>
          )}
        </Link>
      ) : (
        <Link
          to="/me/work-items"
          className="inline-flex items-center gap-1.5 text-[12px] font-medium transition-colors hover:text-[var(--accent)]"
          style={{ color: 'var(--text-muted)' }}
        >
          <ArrowLeft size={14} /> Back to work items
        </Link>
      )}

      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          <div className="flex items-center gap-3 min-w-0 flex-wrap">
            <div className="flex items-center gap-2 min-w-0">
              <Ticket size={16} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
              <h1
                className="text-xl font-semibold tracking-tight"
                style={{ color: 'var(--text-primary)' }}
              >
                {detail.workItemKey}
              </h1>
            </div>
            {/* The tracker link is the most-used control on this page — someone reviewing a ticket
                goes to read it. It was a 14px bare icon next to the title; now it's a labelled
                button so it's findable without hunting. */}
            {detail.url && (
              <a
                href={detail.url}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-lg border text-[12px] font-semibold shrink-0 transition-colors hover:bg-[var(--accent-bg)]"
                style={{
                  borderColor: 'var(--accent)',
                  color: 'var(--accent)',
                  backgroundColor: 'var(--accent-muted)',
                }}
                title={`Open ${detail.workItemKey} in ${providerLabel(detail.provider)}`}
              >
                View in {providerLabel(detail.provider)}
                <ExternalLink size={12} />
              </a>
            )}
          </div>
          {detail.title && (
            <p className="text-[14px] mt-1" style={{ color: 'var(--text-secondary)' }}>
              {detail.title}
            </p>
          )}
          <div
            className="flex items-center gap-2 flex-wrap mt-2 text-[12px]"
            style={{ color: 'var(--text-secondary)' }}
          >
            <span>
              <span style={{ color: 'var(--text-muted)' }}>Product:</span>{' '}
              <span className="font-medium">{detail.product}</span>
            </span>
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            {/* Where the change can be exercised — not where the promotion is headed. The promotion
                edges themselves are in the Promotions card, which is where they belong. */}
            <WorkItemEnvironments environments={detail.environments ?? []} />
          </div>
        </div>
        {headline && headlineStyle && (
          <span
            className="badge shrink-0"
            style={{ backgroundColor: headlineStyle.bg, color: headlineStyle.color }}
          >
            <DecisionIcon decision={headline.decision} size={10} />
            {headlineStyle.label}
          </span>
        )}
      </div>

      {error && (
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{ backgroundColor: 'var(--danger-bg)', borderColor: 'var(--danger)', color: 'var(--danger)' }}
        >
          <XCircle size={18} />
          <span className="text-[13px] font-medium">{error}</span>
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-4">
          {/* Who's on this work item, up front — it's read constantly (who do I ask, who already
             signed off elsewhere) but assigning someone isn't an action worth a whole card, so it's
             a compact chip row rather than the stacked per-role blocks used elsewhere. */}
          <PeopleCard detail={detail} onChanged={load} />
          {/* The ticket/PR/commit body, when the producer sent one. Between People and Sign-off
             because it's the substance of what's being signed off — read after "who owns this",
             before deciding. Absent entirely when there's no content: an empty card would imply
             the description is blank upstream when it more likely just wasn't ingested. */}
          {detail.content && <ContentCard content={detail.content} />}
          <DecisionCard detail={detail} onChanged={load} onError={setError} />
          <DecisionTrail detail={detail} />
          <CommentsCard
            detail={detail}
            currentUserEmail={currentUserEmail}
            onChange={(comments) => setDetail({ ...detail, comments })}
          />
        </div>

        <div className="space-y-4">
          {/* First in the column: for a reviewer who arrived from the queue rather than from a
             promotion, this is the only route to the promotion this sign-off is holding up, so it
             has to be reachable without scrolling past the change set. */}
          <PromotionsCard detail={detail} />

          <ChangeSetCard detail={detail} />
        </div>
      </div>
    </div>
  );
}

/**
 * People assigned to this work item, as a single wrapping chip row rather than the stacked
 * one-line-per-role blocks the promotion detail page uses for the same data — that layout was built
 * to sit quietly in a sidebar; here People is core context for the sign-off decision, so it lives at
 * the top of the main column but shouldn't cost more height than the header line it replaces.
 *
 * Assignments write to the primary candidate's copy of this work-item reference, which is where
 * participants actually live.
 */
function PeopleCard({ detail, onChanged }: { detail: WorkItemDetail; onChanged: () => void }) {
  return (
    <div
      className="rounded-xl border px-5 py-3 flex flex-wrap items-center gap-x-3 gap-y-1.5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2
        className="text-[11px] font-semibold uppercase tracking-wider flex items-center gap-1.5 shrink-0"
        style={{ color: 'var(--text-muted)' }}
      >
        <Users size={12} /> People
      </h2>
      <WorkItemParticipants
        candidateId={detail.primaryCandidateId}
        referenceKey={detail.workItemKey}
        participants={detail.participants}
        onChanged={onChanged}
        readOnly={!detail.canManage}
        layout="chips"
      />
      {!detail.canManage && detail.participants.length === 0 && (
        <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          Nobody assigned.
        </span>
      )}
      {!detail.canManage && detail.participants.length > 0 && (
        <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          Assigning requires the QA or Admin role.
        </span>
      )}
    </div>
  );
}

// A body past either of these collapses behind "Show more". Both matter: a wall of one long
// paragraph and a fifty-line bullet list are each too tall to sit above the sign-off buttons.
const CONTENT_COLLAPSE_CHARS = 800;
const CONTENT_COLLAPSE_LINES = 12;

/**
 * The work item's body — the Jira description, PR description, or commit message body the producer
 * copied onto the reference. Rendered as plain text with line breaks preserved, deliberately *not*
 * as markdown: this string arrives from an ingest payload rather than from someone typing into this
 * app, so interpreting it as markup would let a producer inject HTML into a reviewer's page. The
 * release-note pages do run `marked`, but their input is authored in-product.
 *
 * Long bodies collapse, because the sign-off buttons below shouldn't be pushed off-screen by a
 * ticket description.
 */
function ContentCard({ content }: { content: string }) {
  const [expanded, setExpanded] = useState(false);

  // Decided from the text rather than by measuring the rendered box: it's plain text at a fixed
  // type size, so length and line count predict height closely enough, and it avoids a measuring
  // effect that would flash the expanded state on first paint.
  const isLong = useMemo(
    () =>
      content.length > CONTENT_COLLAPSE_CHARS ||
      content.split('\n').length > CONTENT_COLLAPSE_LINES,
    [content],
  );
  const collapsed = isLong && !expanded;

  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2
        className="text-[11px] font-semibold uppercase tracking-wider mb-4 flex items-center gap-1.5"
        style={{ color: 'var(--text-muted)' }}
      >
        <FileText size={12} /> Content
      </h2>
      <div className="relative">
        <p
          className="text-[13px] whitespace-pre-wrap break-words"
          style={{
            color: 'var(--text-secondary)',
            maxHeight: collapsed ? '15rem' : undefined,
            overflow: collapsed ? 'hidden' : undefined,
          }}
        >
          {content}
        </p>
        {/* Fades the clipped last line so it reads as "continues below" rather than as text that
           happens to end mid-sentence. */}
        {collapsed && (
          <div
            className="absolute inset-x-0 bottom-0 h-10 pointer-events-none"
            style={{ background: 'linear-gradient(to bottom, transparent, var(--bg-primary))' }}
          />
        )}
      </div>
      {isLong && (
        <button
          onClick={() => setExpanded((v) => !v)}
          className="mt-3 text-[11px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--accent)' }}
        >
          {expanded ? 'Show less' : 'Show more'}
        </button>
      )}
    </div>
  );
}

/**
 * The promotions carrying this work item, each linking to its detail page. This is the return path
 * out of a work item: a reviewer opens a ticket to sign it off, and the thing they were actually
 * working on is the promotion waiting on that sign-off.
 *
 * Superseded builds are omitted by the API, so the count here is "live promotions", not history.
 */
function PromotionsCard({ detail }: { detail: WorkItemDetail }) {
  const primary = detail.candidates.find((c) => c.isPrimary) ?? null;

  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2
        className="text-[11px] font-semibold uppercase tracking-wider mb-4 flex items-center gap-1.5"
        style={{ color: 'var(--text-muted)' }}
      >
        <GitPullRequest size={12} /> Promotions ({detail.candidates.length})
      </h2>
      {detail.candidates.length === 0 && (
        <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          No live promotion carries this work item — it still needs signing off so it stops sitting
          in the queue.
        </p>
      )}
      <div className="space-y-2">
        {detail.candidates.map((c) => (
          <Link
            key={c.id}
            to={`/promotions/${c.id}`}
            className="block p-3 rounded-lg border transition-opacity hover:opacity-80"
            style={{
              borderColor: c.isPrimary ? 'var(--accent)' : 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
            }}
            title={`Open the ${c.service} promotion ${c.sourceEnv} → ${c.targetEnv}`}
          >
            <div className="flex items-center justify-between gap-2">
              <span
                className="text-[13px] font-medium truncate"
                style={{ color: 'var(--text-primary)' }}
              >
                {c.service}
              </span>
              <span
                className="text-[11px] font-medium shrink-0"
                style={{ color: CANDIDATE_STATUS_COLOR[c.status] ?? 'var(--text-muted)' }}
              >
                {c.status}
              </span>
            </div>
            <div className="flex items-center gap-1 mt-1.5 text-[11px]">
              <EnvBadge env={c.sourceEnv} size="xs" />
              <ArrowRight size={10} style={{ color: 'var(--text-muted)' }} />
              <EnvBadge env={c.targetEnv} size="xs" />
              <span className="font-mono ml-1" style={{ color: 'var(--text-muted)' }}>
                {c.version}
              </span>
            </div>
            <p className="text-[11px] mt-1" style={{ color: 'var(--text-muted)' }}>
              {formatDistanceToNow(new Date(c.createdAt), { addSuffix: true })}
            </p>
          </Link>
        ))}
      </div>
      {primary && detail.candidates.length > 1 && (
        <p className="mt-3 text-[11px]" style={{ color: 'var(--text-muted)' }}>
          A sign-off here counts for every promotion above. People are assigned on{' '}
          <span className="font-medium">{primary.service}</span> (the newest one). Superseded builds
          aren&rsquo;t listed.
        </p>
      )}
    </div>
  );
}

/**
 * The change that carried this work item: the commits whose messages referenced it, and the pull
 * requests those commits merged. Both come from the promotion payload — the producer declares the
 * commit hashes on the work-item reference and the server resolves them against the `commit` and
 * `pull-request` references — so a reviewer can get from "what am I signing off?" to the actual diff
 * without going hunting in the promotion.
 *
 * Renders nothing when the producer declared no commits, which is the case for every payload written
 * before `commits` existed.
 */
function ChangeSetCard({ detail }: { detail: WorkItemDetail }) {
  const { commits, pullRequests } = detail;
  if (commits.length === 0 && pullRequests.length === 0) return null;

  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2
        className="text-[11px] font-semibold uppercase tracking-wider mb-4 flex items-center gap-1.5"
        style={{ color: 'var(--text-muted)' }}
      >
        <GitCommitHorizontal size={12} /> Change
      </h2>

      {pullRequests.length > 0 && (
        <div className="mb-4">
          <p className="text-[11px] font-medium mb-2" style={{ color: 'var(--text-muted)' }}>
            Pull requests ({pullRequests.length})
          </p>
          <div className="space-y-2">
            {pullRequests.map((pr) => (
              <ChangeRow
                key={pr.key || pr.url || pr.revision || ''}
                icon={<GitPullRequest size={12} />}
                label={pr.key ? `!${pr.key}` : 'Pull request'}
                title={pr.title}
                url={pr.url}
                provider={pr.provider}
                participants={pr.participants}
              />
            ))}
          </div>
        </div>
      )}

      {commits.length > 0 && (
        <div>
          <p className="text-[11px] font-medium mb-2" style={{ color: 'var(--text-muted)' }}>
            Commits ({commits.length})
          </p>
          <div className="space-y-2">
            {commits.map((c) => (
              <ChangeRow
                key={c.hash}
                icon={<GitCommitHorizontal size={12} />}
                label={shortHash(c.hash)}
                labelTitle={c.hash}
                title={c.title}
                url={c.url}
                provider={c.provider}
                participants={c.participants}
              />
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * One commit or pull-request row. The whole row is the link when the reference carried a URL;
 * otherwise it's inert text — a hash the producer declared but supplied no `commit` reference for is
 * still worth showing, just not clickable.
 */
function ChangeRow({
  icon,
  label,
  labelTitle,
  title,
  url,
  provider,
  participants,
}: {
  icon: React.ReactNode;
  label: string;
  labelTitle?: string;
  title: string | null;
  url: string | null;
  provider: string | null;
  participants: PromotionSourceEventParticipant[];
}) {
  const author = participants.find((p) => (p.role ?? '').toLowerCase() === 'author') ?? participants[0];

  const body = (
    <>
      <div className="flex items-center gap-1.5 min-w-0">
        <span style={{ color: 'var(--text-muted)', flexShrink: 0 }}>{icon}</span>
        <span
          className="font-mono text-[11px] font-semibold shrink-0"
          style={{ color: 'var(--accent)' }}
          title={labelTitle}
        >
          {label}
        </span>
        {url && <ExternalLink size={10} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />}
      </div>
      {title && (
        <p className="text-[12px] mt-1 line-clamp-2" style={{ color: 'var(--text-secondary)' }}>
          {title}
        </p>
      )}
      {author && (
        <p className="text-[11px] mt-1 truncate" style={{ color: 'var(--text-muted)' }}>
          {author.displayName ?? author.email}
        </p>
      )}
    </>
  );

  const style = {
    borderColor: 'var(--border-color)',
    backgroundColor: 'var(--bg-secondary)',
  };

  if (!url) {
    return (
      <div className="block p-2.5 rounded-lg border" style={style}>
        {body}
      </div>
    );
  }

  return (
    <a
      href={url}
      target="_blank"
      rel="noopener noreferrer"
      className="block p-2.5 rounded-lg border transition-opacity hover:opacity-80"
      style={style}
      title={`Open ${label} in ${providerLabel(provider, 'the source repository')}`}
    >
      {body}
    </a>
  );
}

function DecisionIcon({ decision, size }: { decision: WorkItemDecision; size: number }) {
  if (decision === 'Approved') return <CheckCircle size={size} />;
  if (decision === 'Blocked') return <Ban size={size} />;
  return <XCircle size={size} />;
}

/**
 * Sign-off controls. Approve / Block / Reject each POST to their own endpoint; the option matching
 * the user's current decision is hidden (re-recording the same one is a no-op the API rejects), so
 * what's left are the states they can actually move to.
 */
function DecisionCard({
  detail,
  onChanged,
  onError,
}: {
  detail: WorkItemDetail;
  onChanged: () => Promise<void>;
  onError: (message: string | null) => void;
}) {
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState<WorkItemDecision | null>(null);

  const decide = async (decision: WorkItemDecision) => {
    setBusy(decision);
    onError(null);
    try {
      const args = [detail.workItemKey, detail.product, detail.targetEnv, comment || undefined] as const;
      if (decision === 'Approved') await api.approveWorkItem(...args);
      else if (decision === 'Blocked') await api.blockWorkItem(...args);
      else await api.rejectWorkItem(...args);
      setComment('');
      await onChanged();
      // A signed-off item drops out of the pending queue the counters and bell badge count.
      refreshMyTasks();
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Action failed');
    } finally {
      setBusy(null);
    }
  };

  const mine = detail.myDecision;
  const mineStyle = mine ? decisionStyle(mine) : null;

  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2
        className="text-[11px] font-semibold uppercase tracking-wider mb-4 flex items-center gap-1.5"
        style={{ color: 'var(--text-muted)' }}
      >
        <CheckCircle size={12} /> Sign-off
      </h2>

      {mine && mineStyle && (
        <div
          className="flex items-center gap-2 px-3 py-2 rounded-lg border text-[12px] mb-3"
          style={{ borderColor: mineStyle.color, backgroundColor: mineStyle.bg, color: mineStyle.color }}
        >
          <DecisionIcon decision={mine} size={13} />
          <span className="font-medium">You {mineStyle.label.toLowerCase()} this work item.</span>
          {detail.canApprove && (
            <span style={{ color: 'var(--text-muted)' }}>You can still change it below.</span>
          )}
        </div>
      )}

      {!detail.canApprove ? (
        <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          {detail.blockedReason ?? 'You cannot sign off on this work item.'}
        </p>
      ) : (
        <>
          <textarea
            value={comment}
            onChange={(e) => setComment(e.target.value)}
            placeholder="Optional comment — recorded with the decision..."
            rows={2}
            className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none mb-3"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
            }}
          />
          <div className="flex items-center gap-2 flex-wrap">
            {mine !== 'Approved' && (
              <button
                onClick={() => decide('Approved')}
                disabled={busy !== null}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{ backgroundColor: 'var(--success-solid)', color: '#fff', opacity: busy ? 0.6 : 1 }}
              >
                <CheckCircle size={12} />
                {busy === 'Approved' ? 'Approving…' : 'Approve'}
              </button>
            )}
            {mine !== 'Blocked' && (
              <button
                onClick={() => decide('Blocked')}
                disabled={busy !== null}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{ backgroundColor: 'var(--warning-solid)', color: '#fff', opacity: busy ? 0.6 : 1 }}
                title="Hold this work item back without rejecting the promotion"
              >
                <Ban size={12} />
                {busy === 'Blocked' ? 'Blocking…' : 'Block'}
              </button>
            )}
            {mine !== 'Rejected' && (
              <button
                onClick={() => decide('Rejected')}
                disabled={busy !== null}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{ backgroundColor: 'var(--danger-solid)', color: '#fff', opacity: busy ? 0.6 : 1 }}
                title="Reject this work item — the promotion stays pending"
              >
                <XCircle size={12} />
                {busy === 'Rejected' ? 'Rejecting…' : 'Reject'}
              </button>
            )}
          </div>
          <p className="text-[11px] mt-2.5" style={{ color: 'var(--text-muted)' }}>
            Only <span className="font-medium">Approve</span> releases the promotion.{' '}
            <span className="font-medium">Block</span> (&ldquo;not yet&rdquo;) and{' '}
            <span className="font-medium">Reject</span> (&ldquo;no&rdquo;) both leave the item
            unresolved, which holds the promotion pending without cancelling it — and both are
            reversible. A new version of the promotion clears them and asks again.
          </p>
        </>
      )}
    </div>
  );
}

function DecisionTrail({ detail }: { detail: WorkItemDetail }) {
  if (detail.approvals.length === 0) return null;
  return (
    <div
      className="rounded-xl border p-5"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2
        className="text-[11px] font-semibold uppercase tracking-wider mb-4"
        style={{ color: 'var(--text-muted)' }}
      >
        Decision trail ({detail.approvals.length})
      </h2>
      <div className="space-y-2">
        {detail.approvals.map((a) => {
          const s = decisionStyle(a.decision);
          return (
            <div
              key={a.id}
              className="flex items-start gap-3 p-3 rounded-lg border"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
            >
              <div
                className="w-7 h-7 rounded-full flex items-center justify-center shrink-0 mt-0.5"
                style={{ backgroundColor: s.bg, color: s.color }}
              >
                <DecisionIcon decision={a.decision} size={14} />
              </div>
              <div className="flex-1 min-w-0">
                <div className="flex items-center justify-between gap-2">
                  <span
                    className="inline-flex items-center gap-1.5 text-[13px] font-medium min-w-0"
                    style={{ color: 'var(--text-primary)' }}
                  >
                    <span className="truncate">{a.approverName || a.approverEmail}</span>
                    <CopyEmailButton email={a.approverEmail} />
                  </span>
                  <span className="text-[11px] shrink-0" style={{ color: 'var(--text-muted)' }}>
                    {format(new Date(a.updatedAt ?? a.createdAt), 'MMM d, HH:mm')}
                    {a.updatedAt && (
                      <span
                        className="ml-1"
                        title={`Originally ${format(new Date(a.createdAt), 'MMM d, HH:mm')}`}
                      >
                        (changed)
                      </span>
                    )}
                  </span>
                </div>
                <div className="mt-1">
                  <span className="badge" style={{ backgroundColor: s.bg, color: s.color }}>
                    {s.label}
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
  );
}

/**
 * The work item's thread: free-text discussion interleaved with the decision entries the API writes
 * on every Approve / Block / Reject (and the system entry when a new version resets one). Everything
 * keys on (workItemKey, product, targetEnv), so the thread outlives the candidate that was live when
 * it started.
 *
 * Decision and system entries are tinted by outcome and carry no edit/delete — they are the record of
 * what happened, not someone's remark about it. Only a human comment the caller authored is editable.
 */
function CommentsCard({
  detail,
  currentUserEmail,
  onChange,
}: {
  detail: WorkItemDetail;
  currentUserEmail: string;
  onChange: (next: WorkItemComment[]) => void;
}) {
  const [body, setBody] = useState('');
  const [posting, setPosting] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editBody, setEditBody] = useState('');

  const comments = detail.comments;
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
      const created = await api.addWorkItemComment(
        detail.workItemKey,
        detail.product,
        detail.targetEnv,
        text,
      );
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
      const updated = await api.updateWorkItemComment(commentId, text);
      onChange(comments.map((c) => (c.id === commentId ? updated : c)));
      setEditingId(null);
      setEditBody('');
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Failed to update');
    }
  };

  const remove = async (commentId: string) => {
    try {
      await api.deleteWorkItemComment(commentId);
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
          // An entry the platform wrote — a sign-off, or the system note when a new version reset
          // one. Immutable server-side, so the UI offers no controls for it either.
          const record = c.decision ?? (c.authorEmail.toLowerCase() === 'system' ? 'system' : null);
          const style = c.decision ? decisionStyle(c.decision) : null;
          const isMine =
            !record &&
            !!currentUserEmail &&
            c.authorEmail.toLowerCase() === currentUserEmail.toLowerCase();
          const isEditing = editingId === c.id;
          return (
            <div
              key={c.id}
              className="p-3 rounded-lg border"
              style={{
                borderColor: style?.color ?? 'var(--border-color)',
                backgroundColor: style?.bg ?? 'var(--bg-secondary)',
              }}
            >
              <div className="flex items-center justify-between gap-2 mb-1">
                <span
                  className="text-[13px] font-medium truncate inline-flex items-center gap-1.5"
                  style={{ color: style?.color ?? 'var(--text-primary)' }}
                >
                  {c.decision && <DecisionIcon decision={c.decision} size={12} />}
                  {record === 'system' ? 'System' : c.authorName || c.authorEmail}
                </span>
                <span className="text-[11px] shrink-0" style={{ color: 'var(--text-muted)' }}>
                  {format(new Date(c.createdAt), 'MMM d, HH:mm')}
                  {c.updatedAt && (
                    <span
                      className="ml-1"
                      title={`Edited ${format(new Date(c.updatedAt), 'MMM d, HH:mm')}`}
                    >
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
