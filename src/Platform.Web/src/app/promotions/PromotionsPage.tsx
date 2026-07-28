import { useEffect, useState, useMemo } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api } from '@/lib/api';
import type { PromotionCandidate, PromotionStatus } from '@/lib/api';
import { resolveReferenceHref } from '@/lib/refUrl';
import { workItemDetailPath } from '@/lib/workItem';
import { roleDisplay } from '@/lib/roleLabel';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { useEnvControlStyle } from '@/components/environments/useEnvColor';
import { useSettingsStore } from '@/stores/settingsStore';
import { formatDistanceToNow } from 'date-fns';
import {
  ArrowRight,
  Clock,
  CheckCircle,
  XCircle,
  Rocket,
  GitPullRequest,
  Ticket,
  ExternalLink,
} from 'lucide-react';

/**
 * Per-candidate work-item signoff progress for the list. Computed lazily for the
 * pending rows only — non-pending candidates show "—". The list API returns the
 * candidate's own sourceEventReferences (work-items + others); we filter to
 * work-items, then call /work-items/{key}?... for each to get approval state.
 *
 * Cap at the visible Pending set per render, which is bounded by the page's
 * filter so this stays well-behaved in practice.
 */
interface WorkItemProgress {
  total: number;
  approved: number;
  rejected: number;
  /** Held back by a Blocked decision — neither approved nor a veto. */
  blocked: number;
  loading: boolean;
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

export function PromotionsPage() {
  const getDisplayName = useSettingsStore((s) => s.getDisplayName);
  // The page is pending-by-default: `candidates` holds only Pending promotions.
  // Resolved (Approved/Deploying/Deployed/Rejected) promotions are never fetched
  // until the user explicitly opens the resolved section (lazy-loaded below).
  const [candidates, setCandidates] = useState<PromotionCandidate[]>([]);
  const [loading, setLoading] = useState(true);
  const [productFilter, setProductFilter] = useState('');
  const [serviceFilter, setServiceFilter] = useState('');
  const [targetEnvFilter, setTargetEnvFilter] = useState('');
  const [referenceFilter, setReferenceFilter] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulkLoading, setBulkLoading] = useState(false);
  const [workItemProgress, setWorkItemProgress] = useState<Record<string, WorkItemProgress>>({});
  // View over the promotions set:
  //  - 'pending'         : all Pending (loaded set)
  //  - 'mine'            : Pending the current user can act on (per-candidate `canApprove`), no refetch
  //  - 'awaiting-deploy' : Approved but not yet deployed — its own fetch (status=Approved)
  const [view, setView] = useState<'pending' | 'mine' | 'awaiting-deploy'>('pending');
  // Approved-awaiting-deploy set. Fetched eagerly (alongside Pending) so the tab
  // badge shows a live count without the user having to open the tab first.
  const [awaitingDeploy, setAwaitingDeploy] = useState<PromotionCandidate[]>([]);
  const [awaitingDeployLoading, setAwaitingDeployLoading] = useState(true);
  // Resolved section — lazy. Only fetched when the user opens it.
  const [resolved, setResolved] = useState<PromotionCandidate[]>([]);
  const [resolvedShown, setResolvedShown] = useState(false);
  const [resolvedLoading, setResolvedLoading] = useState(false);
  const targetEnvSelectStyle = useEnvControlStyle(targetEnvFilter);

  // Secondary filters shared by both the pending fetch and the resolved fetch.
  const filterParams = () => {
    const params: Record<string, string> = {};
    if (productFilter) params.product = productFilter;
    if (serviceFilter) params.service = serviceFilter;
    if (targetEnvFilter) params.targetEnv = targetEnvFilter;
    if (referenceFilter) params.reference = referenceFilter;
    return params;
  };

  const fetchData = () => {
    setLoading(true);
    api
      .listPromotions({ status: 'Pending', ...filterParams() })
      .then((data) => setCandidates(data.candidates || []))
      .catch(() => setCandidates([]))
      .finally(() => setLoading(false));
  };

  // Approved-but-not-yet-deployed. Separate fetch (single-status ⇒ backend allows up
  // to 200) so the count is available for the tab badge before the tab is opened.
  const fetchAwaitingDeploy = () => {
    setAwaitingDeployLoading(true);
    api
      .listPromotions({ status: 'Approved', ...filterParams() })
      .then((data) => setAwaitingDeploy(data.candidates || []))
      .catch(() => setAwaitingDeploy([]))
      .finally(() => setAwaitingDeployLoading(false));
  };

  useEffect(() => {
    fetchData();
    fetchAwaitingDeploy();
    // A filter change invalidates any loaded resolved set — collapse it so it
    // reloads fresh (with the new filters) if the user reopens it.
    setResolvedShown(false);
    setResolved([]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [productFilter, serviceFilter, targetEnvFilter, referenceFilter]);

  const loadResolved = () => {
    setResolvedShown(true);
    setResolvedLoading(true);
    api
      .listPromotions(filterParams())
      .then((data) => setResolved((data.candidates || []).filter((c) => c.status !== 'Pending')))
      .catch(() => setResolved([]))
      .finally(() => setResolvedLoading(false));
  };

  const pending = useMemo(() => candidates.filter((c) => c.status === 'Pending'), [candidates]);

  // Lazy work-item-progress fetch for Pending rows only. Non-pending rows show "—"
  // so we never spend an HTTP round-trip on them. We fan out concurrently per
  // candidate and per work item, capped by the natural Pending bound (small in
  // practice). A cancellation guard avoids overwriting state when the candidate
  // list churns mid-flight (e.g. a filter changes).
  useEffect(() => {
    let cancelled = false;
    (async () => {
      for (const c of pending) {
        const tickets = (c.sourceEventReferences ?? []).filter(
          (r) => r.type === 'work-item' && (r.key ?? '').trim().length > 0,
        );
        if (tickets.length === 0) {
          setWorkItemProgress((prev) => ({
            ...prev,
            [c.id]: { total: 0, approved: 0, rejected: 0, blocked: 0, loading: false },
          }));
          continue;
        }
        // Mark the row as loading once per candidate so the cell can show a hint.
        setWorkItemProgress((prev) => ({
          ...prev,
          [c.id]: prev[c.id] ?? {
            total: tickets.length,
            approved: 0,
            rejected: 0,
            blocked: 0,
            loading: true,
          },
        }));
        try {
          const ctxs = await Promise.all(
            tickets.map((t) =>
              api
                .getWorkItemContext(t.key ?? '', c.product, c.targetEnv)
                .catch(() => null),
            ),
          );
          if (cancelled) return;
          let approved = 0;
          let rejected = 0;
          let blocked = 0;
          for (const ctx of ctxs) {
            const decision = ctx?.approvals?.[0]?.decision;
            if (decision === 'Approved') approved++;
            else if (decision === 'Rejected') rejected++;
            else if (decision === 'Blocked') blocked++;
          }
          setWorkItemProgress((prev) => ({
            ...prev,
            [c.id]: {
              total: tickets.length,
              approved,
              rejected,
              blocked,
              loading: false,
            },
          }));
        } catch {
          if (cancelled) return;
          setWorkItemProgress((prev) => ({
            ...prev,
            [c.id]: { total: tickets.length, approved: 0, rejected: 0, blocked: 0, loading: false },
          }));
        }
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pending.map((c) => c.id).join(',')]);

  // Known target envs from the currently-loaded candidate set, for the dropdown. Keeping the
  // current filter selection in the list even if nothing matches so the user can clear it.
  const targetEnvOptions = useMemo(() => {
    const set = new Set<string>();
    for (const c of candidates) if (c.targetEnv) set.add(c.targetEnv);
    if (targetEnvFilter) set.add(targetEnvFilter);
    return Array.from(set).sort();
  }, [candidates, targetEnvFilter]);

  const productOptions = useMemo(() => {
    const set = new Set<string>();
    for (const c of candidates) if (c.product) set.add(c.product);
    if (productFilter) set.add(productFilter);
    return Array.from(set).sort();
  }, [candidates, productFilter]);

  const approvablePending = useMemo(() => pending.filter((c) => c.canApprove), [pending]);
  const displayedPending = view === 'mine' ? approvablePending : pending;
  const allApprovableSelected =
    approvablePending.length > 0 && approvablePending.every((c) => selected.has(c.id));

  const toggleSelect = (id: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const toggleSelectAll = () => {
    if (allApprovableSelected) {
      setSelected(new Set());
    } else {
      setSelected(new Set(approvablePending.map((c) => c.id)));
    }
  };

  const handleBulkApprove = async () => {
    if (selected.size === 0) return;
    setBulkLoading(true);
    try {
      await api.bulkApprovePromotions(Array.from(selected));
      setSelected(new Set());
      fetchData();
      // Approved rows leave Pending and land in the awaiting-deploy set — refresh it too.
      fetchAwaitingDeploy();
    } catch {
      // silently refresh
      fetchData();
      fetchAwaitingDeploy();
    } finally {
      setBulkLoading(false);
    }
  };

  return (
    <div className="space-y-6">
      {/* Header */}
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Promotions
        </h1>
        <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
          Review and approve version promotions across environments
        </p>
      </div>

      {/* Secondary filters */}
      <div className="flex items-center gap-3 flex-wrap">
        <select
          value={productFilter}
          onChange={(e) => setProductFilter(e.target.value)}
          className="rounded-lg border px-3 py-1.5 text-[13px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value="">All products</option>
          {productOptions.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>
        <input
          type="text"
          placeholder="Service search..."
          value={serviceFilter}
          onChange={(e) => setServiceFilter(e.target.value)}
          className="rounded-lg border px-3 py-1.5 text-[13px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        />
        {/* When a target env is selected the control itself takes that environment's colour,
            so an active env filter is visible without reading the dropdown. */}
        <select
          value={targetEnvFilter}
          onChange={(e) => setTargetEnvFilter(e.target.value)}
          className="rounded-lg border px-3 py-1.5 text-[13px] font-medium"
          style={targetEnvSelectStyle}
        >
          <option value="">All target envs</option>
          {targetEnvOptions.map((env) => (
            <option key={env} value={env}>
              {getDisplayName(env)}
            </option>
          ))}
        </select>
        <input
          type="text"
          placeholder="Reference (PR, work item, commit...)"
          value={referenceFilter}
          onChange={(e) => setReferenceFilter(e.target.value)}
          className="rounded-lg border px-3 py-1.5 text-[13px] min-w-[240px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        />
      </div>

      {/* Segmented control: all pending vs. only what the current user can approve. */}
      <div className="flex items-center gap-2">
        {([
          { key: 'pending', label: 'All pending', count: pending.length, showBadge: false },
          { key: 'mine', label: 'Awaiting my approval', count: approvablePending.length, showBadge: true },
          { key: 'awaiting-deploy', label: 'Approved · awaiting deploy', count: awaitingDeploy.length, showBadge: true },
        ] as const).map((tab) => {
          const active = view === tab.key;
          return (
            <button
              key={tab.key}
              type="button"
              onClick={() => setView(tab.key)}
              aria-pressed={active}
              className="flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[13px] font-medium transition-colors"
              style={{
                borderColor: active ? 'var(--accent)' : 'var(--border-color)',
                backgroundColor: active ? 'var(--accent-bg)' : 'var(--bg-primary)',
                color: active ? 'var(--accent)' : 'var(--text-secondary)',
              }}
            >
              {tab.label}
              {tab.showBadge && tab.count > 0 && (
                <span
                  className="ml-0.5 px-1.5 rounded-full text-[11px] font-semibold"
                  style={{
                    backgroundColor: active ? 'var(--accent)' : 'var(--warning-bg)',
                    color: active ? '#fff' : 'var(--warning)',
                  }}
                >
                  {tab.count}
                </span>
              )}
            </button>
          );
        })}
      </div>

      {loading && view !== 'awaiting-deploy' ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="skeleton h-24" />
          ))}
        </div>
      ) : (
        <div className="space-y-6">
          {view === 'awaiting-deploy' ? (
            /* Approved but not yet deployed. Browse-only: no bulk-approve, no per-row
               approve action (already approved) — this is a tracking view. */
            awaitingDeployLoading ? (
              <div className="space-y-3">
                {[1, 2, 3].map((i) => (
                  <div key={i} className="skeleton h-24" />
                ))}
              </div>
            ) : awaitingDeploy.length > 0 ? (
              <div>
                <h2
                  className="text-[11px] font-semibold uppercase tracking-wider mb-3"
                  style={{ color: 'var(--text-muted)' }}
                >
                  Approved · awaiting deploy ({awaitingDeploy.length})
                </h2>
                <div className="space-y-2">
                  {awaitingDeploy.map((c) => (
                    <CandidateCard key={c.id} candidate={c} />
                  ))}
                </div>
              </div>
            ) : (
              <div
                className="flex flex-col items-center justify-center py-16 rounded-xl border"
                style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
              >
                <div
                  className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
                  style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
                >
                  <Rocket size={24} />
                </div>
                <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>
                  Nothing awaiting deploy
                </p>
                <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
                  Approved promotions not yet deployed will appear here.
                </p>
              </div>
            )
          ) : /* Pending list (or "awaiting my approval" when that tab is active) */
          displayedPending.length > 0 ? (
            <div>
              <div className="flex items-center justify-between mb-3">
                <div className="flex items-center gap-3">
                  {/* Bulk-select is opt-in: only offered in the "Awaiting my approval" view,
                     where every row is something you can act on. The default list stays
                     action-per-row (Review →) without checkbox clutter. */}
                  {view === 'mine' && approvablePending.length > 0 && (
                    <input
                      type="checkbox"
                      checked={allApprovableSelected}
                      onChange={toggleSelectAll}
                      className="rounded"
                    />
                  )}
                  <h2
                    className="text-[11px] font-semibold uppercase tracking-wider"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    {view === 'mine' ? 'Awaiting my approval' : 'All pending'} ({displayedPending.length})
                  </h2>
                </div>
                {view === 'mine' && selected.size > 0 && (
                  <button
                    onClick={handleBulkApprove}
                    disabled={bulkLoading}
                    className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                    style={{
                      backgroundColor: 'var(--success-solid)',
                      color: '#fff',
                      opacity: bulkLoading ? 0.6 : 1,
                    }}
                  >
                    <CheckCircle size={12} />
                    {bulkLoading ? 'Approving...' : `Approve selected (${selected.size})`}
                  </button>
                )}
              </div>
              <div className="space-y-2">
                {displayedPending.map((c) => (
                  <CandidateCard
                    key={c.id}
                    candidate={c}
                    urgent
                    selectable={view === 'mine' && c.canApprove}
                    selected={selected.has(c.id)}
                    onToggleSelect={() => toggleSelect(c.id)}
                    workItemProgress={workItemProgress[c.id]}
                    awaitingCue={view !== 'mine'}
                  />
                ))}
              </div>
            </div>
          ) : view === 'mine' ? (
            <div
              className="flex flex-col items-center justify-center py-16 rounded-xl border"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <div
                className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
                style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
              >
                <CheckCircle size={24} />
              </div>
              <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>
                Nothing awaiting your approval
              </p>
              <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
                Promotions you can approve will appear here.
              </p>
            </div>
          ) : (
            <div
              className="flex flex-col items-center justify-center py-16 rounded-xl border"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <div
                className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
                style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
              >
                <GitPullRequest size={24} />
              </div>
              <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>
                No pending promotions
              </p>
              <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
                Pending promotions awaiting approval will appear here.
              </p>
            </div>
          )}

          {/* Resolved promotions — lazy-loaded only when the user asks. */}
          {!resolvedShown ? (
            <button
              type="button"
              onClick={loadResolved}
              className="text-[13px] font-medium transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Show resolved promotions
            </button>
          ) : (
            <div>
              <div className="flex items-center justify-between mb-3">
                <h2
                  className="text-[11px] font-semibold uppercase tracking-wider"
                  style={{ color: 'var(--text-muted)' }}
                >
                  Resolved ({resolved.length})
                </h2>
                <button
                  type="button"
                  onClick={() => {
                    setResolvedShown(false);
                    setResolved([]);
                  }}
                  className="text-[12px] font-medium transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-muted)' }}
                >
                  Hide resolved
                </button>
              </div>
              {resolvedLoading ? (
                <div className="space-y-3">
                  {[1, 2, 3].map((i) => (
                    <div key={i} className="skeleton h-24" />
                  ))}
                </div>
              ) : resolved.length > 0 ? (
                <div className="space-y-2">
                  {resolved.map((c) => (
                    <CandidateCard key={c.id} candidate={c} compact />
                  ))}
                </div>
              ) : (
                <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                  No resolved promotions.
                </p>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function CandidateCard({
  candidate,
  urgent,
  selectable,
  selected,
  onToggleSelect,
  workItemProgress,
  awaitingCue,
  compact,
}: {
  candidate: PromotionCandidate;
  urgent?: boolean;
  selectable?: boolean;
  selected?: boolean;
  onToggleSelect?: () => void;
  workItemProgress?: WorkItemProgress;
  /** Show the "Awaiting your approval" cue when the user can act (used in the all-pending view). */
  awaitingCue?: boolean;
  /** Compact reference row for the resolved (browse-only) list: drops work-item chips,
     people chips, and signoff progress; keeps service, version/env, status, time, View. */
  compact?: boolean;
}) {
  const navigate = useNavigate();
  const cfg = STATUS_CONFIG[candidate.status] ?? STATUS_CONFIG.Pending;
  const StatusIcon = cfg.icon;
  // Inline "+N more" expansion for the chip rows — collapsed by default so a bundle
  // with many work-items / participants doesn't turn the card into a wall of chips.
  const [showAllTickets, setShowAllTickets] = useState(false);
  const [showAllPeople, setShowAllPeople] = useState(false);
  const MAX_TICKETS = 5;
  const MAX_PEOPLE = 5;

  return (
    <div
      className="card-hover rounded-xl border p-4 cursor-pointer flex items-start gap-3"
      style={{
        borderColor: urgent ? cfg.color + '40' : 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        borderLeft: candidate.canApprove ? `3px solid var(--warning)` : undefined,
      }}
      onClick={() => navigate(`/promotions/${candidate.id}`)}
    >
      {selectable && (
        <input
          type="checkbox"
          checked={selected}
          onClick={(e) => e.stopPropagation()}
          onChange={onToggleSelect}
          className="rounded mt-1 shrink-0"
        />
      )}
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2 mb-1">
          <h3 className="text-[14px] font-semibold truncate" style={{ color: 'var(--text-primary)' }}>
            {candidate.product} / {candidate.service}
          </h3>
          {/* The status badge only carries information once status varies (the resolved list).
             In the all-pending list it's constant noise, so drop it there and surface the
             actionable "Awaiting your approval" cue instead. */}
          {candidate.status !== 'Pending' && (
            <span className="badge" style={{ backgroundColor: cfg.bg, color: cfg.color }}>
              <StatusIcon size={10} />
              {candidate.status}
            </span>
          )}
          {awaitingCue && candidate.canApprove && (
            <span className="badge" style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}>
              <Clock size={10} />
              Awaiting your approval
            </span>
          )}
        </div>
        <div className="flex items-center gap-2 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          <EnvBadge env={candidate.sourceEnv} suffix={`(${candidate.version})`} />
          <ArrowRight size={12} style={{ color: 'var(--text-muted)' }} />
          <EnvBadge
            env={candidate.targetEnv}
            suffix={`(${candidate.targetCurrentVersion ?? 'new'})`}
            title={
              candidate.targetCurrentVersion
                ? `Replaces v${candidate.targetCurrentVersion} currently in ${candidate.targetEnv}`
                : `First deploy to ${candidate.targetEnv}`
            }
          />
        </div>
        <div className="flex items-center gap-4 mt-2 text-[11px]" style={{ color: 'var(--text-muted)' }}>
          <span className="flex items-center gap-1">
            <Clock size={10} />
            {formatDistanceToNow(new Date(candidate.createdAt), { addSuffix: true })}
          </span>
          {!compact && <WorkItemsBadge candidate={candidate} progress={workItemProgress} />}
        </div>
        {/* Work items — key + optional title. The chip opens the work-item detail page (sign-off,
           comments, people); the icon beside it opens the ticket in its tracker. To narrow the list
           to one work item instead, use the reference filter at the top of the page. */}
        {!compact && (() => {
          const tickets = (candidate.sourceEventReferences ?? []).filter(
            (r) => r.type === 'work-item' && (r.key ?? '').trim().length > 0,
          );
          if (tickets.length === 0) return null;
          const visibleTickets = showAllTickets ? tickets : tickets.slice(0, MAX_TICKETS);
          const hiddenTickets = tickets.length - visibleTickets.length;
          return (
            <div className="flex items-center gap-1.5 flex-wrap mt-2">
              {visibleTickets.map((ref, i) => {
                const workItemKey = ref.key ?? '';
                const href = resolveReferenceHref({
                  type: ref.type,
                  url: ref.url ?? undefined,
                  provider: ref.provider ?? undefined,
                  revision: ref.revision ?? undefined,
                });
                const chipLabel = ref.title ? `${ref.key} — ${ref.title}` : ref.key!;
                return (
                  <span key={`wi-${i}`} className="inline-flex items-center gap-1 text-[10px]">
                    <Link
                      to={workItemDetailPath(workItemKey, candidate.product, candidate.targetEnv)}
                      onClick={(e) => e.stopPropagation()}
                      className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded transition-opacity hover:opacity-80"
                      style={{
                        backgroundColor: 'var(--bg-secondary)',
                        color: 'var(--text-secondary)',
                        maxWidth: 200,
                      }}
                      title={ref.title ? `${ref.key} — ${ref.title}` : `Open ${workItemKey} details`}
                    >
                      <Ticket size={10} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
                      <span className="font-medium truncate">{chipLabel}</span>
                    </Link>
                    {href && (
                      <a
                        href={href}
                        target="_blank"
                        rel="noopener noreferrer"
                        onClick={(e) => e.stopPropagation()}
                        style={{ color: 'var(--text-muted)' }}
                        className="transition-opacity hover:opacity-80"
                        title={`Open ${workItemKey} in ${ref.provider ?? 'the tracker'}`}
                        aria-label={`Open ${workItemKey} in ${ref.provider ?? 'the tracker'}`}
                      >
                        <ExternalLink size={10} />
                      </a>
                    )}
                  </span>
                );
              })}
              {hiddenTickets > 0 && (
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    setShowAllTickets(true);
                  }}
                  className="text-[10px] font-medium px-1.5 py-0.5 rounded transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-muted)', backgroundColor: 'var(--bg-secondary)' }}
                >
                  +{hiddenTickets} more
                </button>
              )}
            </div>
          );
        })()}
        {/* People — reference-level (from work items) + promotion root */}
        {!compact && (() => {
          type Chip = {
            role: string;
            displayName?: string | null;
            email?: string | null;
            /** Set for reference-level participants; absent for promotion-root ones */
            via?: string;
            fromPromotion?: boolean;
          };
          const chips: Chip[] = [];

          // Participants scoped to a work-item reference (QA on OBS-265, author of PR, etc.)
          for (const ref of candidate.sourceEventReferences ?? []) {
            if (ref.type !== 'work-item') continue;
            const refLabel = ref.key ?? ref.type;
            for (const p of ref.participants ?? []) {
              chips.push({ role: p.role, displayName: p.displayName, email: p.email, via: refLabel });
            }
          }

          // Promotion-root participants (assigned directly on the promotion candidate)
          for (const p of candidate.participants ?? []) {
            chips.push({ role: p.role, displayName: p.displayName, email: p.email, fromPromotion: true });
          }

          if (chips.length === 0) return null;

          // Dedupe by person: the same person is often Assignee/Reporter across many
          // tickets in a bundle, which otherwise repeats their chip a dozen times. Collapse
          // to one chip per identity (email ?? display name) with their roles aggregated;
          // the per-ticket "via" attribution moves into the tooltip (full breakdown lives
          // on the detail page).
          interface Person {
            name: string;
            email?: string | null;
            roles: string[];
            vias: string[];
            fromPromotion: boolean;
          }
          const byPerson = new Map<string, Person>();
          for (const c of chips) {
            const name = c.displayName ?? c.email ?? '—';
            const key = (c.email ?? c.displayName ?? name).toLowerCase();
            const entry = byPerson.get(key) ?? {
              name,
              email: c.email,
              roles: [],
              vias: [],
              fromPromotion: false,
            };
            const role = roleDisplay({ role: c.role });
            if (!entry.roles.includes(role)) entry.roles.push(role);
            if (c.via && !entry.vias.includes(c.via)) entry.vias.push(c.via);
            if (c.fromPromotion) entry.fromPromotion = true;
            byPerson.set(key, entry);
          }
          const people = Array.from(byPerson.values());
          const visiblePeople = showAllPeople ? people : people.slice(0, MAX_PEOPLE);
          const hiddenPeople = people.length - visiblePeople.length;

          return (
            <div className="flex items-center gap-1.5 flex-wrap mt-1.5">
              {visiblePeople.map((p, i) => {
                const rolesLabel = p.roles.join(', ');
                return (
                  <span
                    key={`p-${p.email ?? p.name}-${i}`}
                    className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px]"
                    style={{
                      backgroundColor: p.fromPromotion ? 'var(--accent-bg)' : 'var(--bg-secondary)',
                      color: 'var(--text-secondary)',
                      border: p.fromPromotion ? '1px solid color-mix(in srgb, var(--accent) 30%, transparent)' : undefined,
                    }}
                    title={[
                      `${rolesLabel}: ${p.name}`,
                      p.vias.length > 0 ? `via ${p.vias.join(', ')}` : null,
                      p.email ?? null,
                    ].filter(Boolean).join(' · ')}
                  >
                    <span style={{ color: 'var(--text-muted)' }}>{rolesLabel}:</span>
                    <span className="font-medium">{p.name}</span>
                  </span>
                );
              })}
              {hiddenPeople > 0 && (
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    setShowAllPeople(true);
                  }}
                  className="text-[10px] font-medium px-1.5 py-0.5 rounded transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-muted)', backgroundColor: 'var(--bg-secondary)' }}
                >
                  +{hiddenPeople} more
                </button>
              )}
            </div>
          );
        })()}
      </div>
      {/* Explicit per-row action. The whole card is the click target (navigates to detail);
         this is the visible CTA so the row reads as an action, not a static record. A right
         chevron (not ↗) — it stays in-app. */}
      <span
        className="shrink-0 self-center inline-flex items-center gap-1 text-[12px] font-medium"
        style={{ color: candidate.canApprove ? 'var(--accent)' : 'var(--text-muted)' }}
      >
        {candidate.canApprove ? 'Review' : 'View'}
        <ArrowRight size={14} />
      </span>
    </div>
  );
}

/**
 * Inline work-item-progress indicator for the list. The list response surfaces
 * the candidate's own work-item refs (sourceEventReferences) but not approval
 * state, so the parent fetches /work-items/{key}?... lazily for Pending rows
 * only. Non-pending rows render "—" so historical state isn't fetched.
 */
function WorkItemsBadge({
  candidate,
  progress,
}: {
  candidate: PromotionCandidate;
  progress: WorkItemProgress | undefined;
}) {
  const bundleSize = (candidate.sourceEventReferences ?? []).filter(
    (r) => r.type === 'work-item',
  ).length;
  if (bundleSize === 0) {
    return (
      <span
        className="inline-flex items-center gap-1"
        style={{ color: 'var(--text-muted)' }}
        title="This promotion has no work items"
      >
        <Ticket size={10} />
        No work items
      </span>
    );
  }
  if (candidate.status !== 'Pending' || !progress) {
    return (
      <span
        className="inline-flex items-center gap-1"
        title={`${bundleSize} work-item(s)`}
      >
        <Ticket size={10} />
        {bundleSize}
      </span>
    );
  }
  if (progress.loading) {
    return (
      <span className="inline-flex items-center gap-1" title="Loading work item state…">
        <Ticket size={10} />
        {progress.approved}/{progress.total}
        <ProgressBar approved={progress.approved} total={progress.total} />
      </span>
    );
  }
  // Blocked items are called out in the label: without it a stalled bundle looks identical to one
  // nobody has looked at yet, which is the opposite of the truth.
  const label = progress.approved === 0
    ? progress.blocked > 0
      ? `${progress.blocked} blocked`
      : 'Awaiting'
    : progress.blocked > 0
      ? `${progress.approved}/${progress.total} approved · ${progress.blocked} blocked`
      : `${progress.approved}/${progress.total} approved`;
  const undecided = progress.total - progress.approved - progress.rejected - progress.blocked;
  return (
    <span
      className="inline-flex items-center gap-1.5"
      title={[
        `${progress.approved} approved`,
        progress.blocked > 0 ? `${progress.blocked} blocked` : null,
        progress.rejected > 0 ? `${progress.rejected} rejected` : null,
        `${undecided} pending`,
      ]
        .filter(Boolean)
        .join(', ')}
    >
      <Ticket size={10} />
      {label}
      <ProgressBar
        approved={progress.approved}
        total={progress.total}
        rejected={progress.rejected}
        blocked={progress.blocked}
      />
    </span>
  );
}

function ProgressBar({
  approved,
  total,
  rejected = 0,
  blocked = 0,
}: {
  approved: number;
  total: number;
  rejected?: number;
  blocked?: number;
}) {
  if (total === 0) return null;
  const approvedPct = (approved / total) * 100;
  const blockedPct = (blocked / total) * 100;
  const rejectedPct = (rejected / total) * 100;
  return (
    <span
      className="inline-block rounded-full overflow-hidden"
      style={{
        width: 36,
        height: 4,
        backgroundColor: 'var(--bg-secondary)',
        border: '1px solid var(--border-color)',
      }}
    >
      <span
        className="inline-block align-top h-full"
        style={{ width: `${approvedPct}%`, backgroundColor: 'var(--success)' }}
      />
      {blockedPct > 0 && (
        <span
          className="inline-block align-top h-full"
          style={{ width: `${blockedPct}%`, backgroundColor: 'var(--warning)' }}
        />
      )}
      {rejectedPct > 0 && (
        <span
          className="inline-block align-top h-full"
          style={{ width: `${rejectedPct}%`, backgroundColor: 'var(--danger)' }}
        />
      )}
    </span>
  );
}
