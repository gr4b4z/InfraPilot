import { useEffect, useState, useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '@/lib/api';
import type { PromotionCandidate, PromotionStatus } from '@/lib/api';
import { resolveReferenceHref } from '@/lib/refUrl';
import { roleDisplay } from '@/lib/roleLabel';
import { formatDistanceToNow } from 'date-fns';
import {
  ArrowRight,
  Clock,
  CheckCircle,
  XCircle,
  Rocket,
  ArrowUpRight,
  GitPullRequest,
  GitBranch,
  Ticket,
  Workflow,
  ExternalLink,
} from 'lucide-react';

/**
 * Per-candidate ticket signoff progress for the list. Computed lazily for the
 * pending rows only — non-pending candidates show "—". The list API returns the
 * candidate's own sourceEventReferences (work-items + others); we filter to
 * work-items, then call /work-items/{key}?... for each to get approval state.
 *
 * Cap at the visible Pending set per render, which is bounded by the page's
 * filter so this stays well-behaved in practice.
 */
interface TicketProgress {
  total: number;
  approved: number;
  rejected: number;
  loading: boolean;
}

const REFERENCE_ICONS: Record<string, typeof ExternalLink> = {
  pipeline: Workflow,
  repository: GitBranch,
  'pull-request': GitPullRequest,
  'work-item': Ticket,
};

function referenceChipLabel(type: string, key: string | null | undefined): string {
  if (!key) return type;
  switch (type) {
    case 'pull-request':
      return `#${key}`;
    default:
      return key;
  }
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

const STATUS_OPTIONS: Array<{ label: string; value: string }> = [
  { label: 'All', value: '' },
  { label: 'Pending', value: 'Pending' },
  { label: 'Approved', value: 'Approved' },
  { label: 'Deploying', value: 'Deploying' },
  { label: 'Deployed', value: 'Deployed' },
  { label: 'Rejected', value: 'Rejected' },
];

export function PromotionsPage() {
  const [candidates, setCandidates] = useState<PromotionCandidate[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [productFilter, setProductFilter] = useState('');
  const [serviceFilter, setServiceFilter] = useState('');
  const [targetEnvFilter, setTargetEnvFilter] = useState('');
  const [referenceFilter, setReferenceFilter] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulkLoading, setBulkLoading] = useState(false);
  const [ticketProgress, setTicketProgress] = useState<Record<string, TicketProgress>>({});

  const fetchData = () => {
    setLoading(true);
    const params: Record<string, string> = {};
    if (statusFilter) params.status = statusFilter;
    if (productFilter) params.product = productFilter;
    if (serviceFilter) params.service = serviceFilter;
    if (targetEnvFilter) params.targetEnv = targetEnvFilter;
    if (referenceFilter) params.reference = referenceFilter;
    api
      .listPromotions(params)
      .then((data) => setCandidates(data.candidates || []))
      .catch(() => setCandidates([]))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    fetchData();
  }, [statusFilter, productFilter, serviceFilter, targetEnvFilter, referenceFilter]);

  const pending = useMemo(() => candidates.filter((c) => c.status === 'Pending'), [candidates]);
  const nonPending = useMemo(() => candidates.filter((c) => c.status !== 'Pending'), [candidates]);

  // Lazy ticket-progress fetch for Pending rows only. Non-pending rows show "—"
  // so we never spend an HTTP round-trip on them. We fan out concurrently per
  // candidate and per ticket, capped by the natural Pending bound (small in
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
          setTicketProgress((prev) => ({
            ...prev,
            [c.id]: { total: 0, approved: 0, rejected: 0, loading: false },
          }));
          continue;
        }
        // Mark the row as loading once per candidate so the cell can show a hint.
        setTicketProgress((prev) => ({
          ...prev,
          [c.id]: prev[c.id] ?? {
            total: tickets.length,
            approved: 0,
            rejected: 0,
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
          for (const ctx of ctxs) {
            const decision = ctx?.approvals?.[0]?.decision;
            if (decision === 'Approved') approved++;
            else if (decision === 'Rejected') rejected++;
          }
          setTicketProgress((prev) => ({
            ...prev,
            [c.id]: {
              total: tickets.length,
              approved,
              rejected,
              loading: false,
            },
          }));
        } catch {
          if (cancelled) return;
          setTicketProgress((prev) => ({
            ...prev,
            [c.id]: { total: tickets.length, approved: 0, rejected: 0, loading: false },
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
    } catch {
      // silently refresh
      fetchData();
    } finally {
      setBulkLoading(false);
    }
  };

  const statCounts = useMemo(() => {
    const counts: Record<string, number> = {
      Pending: 0,
      Approved: 0,
      Deploying: 0,
      Deployed: 0,
      Rejected: 0,
    };
    for (const c of candidates) {
      if (counts[c.status] !== undefined) counts[c.status]++;
    }
    return counts;
  }, [candidates]);

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

      {/* Stats */}
      <div className="grid grid-cols-5 gap-3">
        {[
          { label: 'Pending', color: 'var(--warning)', bg: 'var(--warning-bg)', icon: Clock },
          { label: 'Approved', color: 'var(--info)', bg: 'var(--info-bg)', icon: CheckCircle },
          { label: 'Deploying', color: 'var(--accent)', bg: 'var(--accent-bg)', icon: Rocket },
          { label: 'Deployed', color: 'var(--success)', bg: 'var(--success-bg)', icon: CheckCircle },
          { label: 'Rejected', color: 'var(--danger)', bg: 'var(--danger-bg)', icon: XCircle },
        ].map((s) => (
          <div
            key={s.label}
            className="flex items-center gap-3 p-3.5 rounded-xl border"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
          >
            <div
              className="w-9 h-9 rounded-lg flex items-center justify-center shrink-0"
              style={{ backgroundColor: s.bg, color: s.color }}
            >
              <s.icon size={16} />
            </div>
            <div>
              <p className="text-lg font-semibold leading-none" style={{ color: 'var(--text-primary)' }}>
                {statCounts[s.label] ?? 0}
              </p>
              <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>{s.label}</p>
            </div>
          </div>
        ))}
      </div>

      {/* Filters */}
      <div className="flex items-center gap-3 flex-wrap">
        <select
          value={statusFilter}
          onChange={(e) => setStatusFilter(e.target.value)}
          className="rounded-lg border px-3 py-1.5 text-[13px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          {STATUS_OPTIONS.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
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
        <select
          value={targetEnvFilter}
          onChange={(e) => setTargetEnvFilter(e.target.value)}
          className="rounded-lg border px-3 py-1.5 text-[13px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value="">All target envs</option>
          {targetEnvOptions.map((env) => (
            <option key={env} value={env}>
              {env}
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

      {loading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="skeleton h-24" />
          ))}
        </div>
      ) : candidates.length === 0 ? (
        <div
          className="flex flex-col items-center justify-center py-20 rounded-xl border"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <div
            className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
          >
            <GitPullRequest size={24} />
          </div>
          <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>
            No promotion candidates
          </p>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            Promotion candidates will appear here when new versions are ready to move between environments
          </p>
        </div>
      ) : (
        <div className="space-y-6">
          {/* Pending section */}
          {pending.length > 0 && (
            <div>
              <div className="flex items-center justify-between mb-3">
                <div className="flex items-center gap-3">
                  {approvablePending.length > 0 && (
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
                    Pending ({pending.length})
                  </h2>
                </div>
                {selected.size > 0 && (
                  <button
                    onClick={handleBulkApprove}
                    disabled={bulkLoading}
                    className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                    style={{
                      backgroundColor: 'var(--success)',
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
                {pending.map((c) => (
                  <CandidateCard
                    key={c.id}
                    candidate={c}
                    urgent
                    selectable={c.canApprove}
                    selected={selected.has(c.id)}
                    onToggleSelect={() => toggleSelect(c.id)}
                    onFilterByReference={setReferenceFilter}
                    ticketProgress={ticketProgress[c.id]}
                  />
                ))}
              </div>
            </div>
          )}

          {/* Non-pending section */}
          {nonPending.length > 0 && (
            <div>
              <h2
                className="text-[11px] font-semibold uppercase tracking-wider mb-3"
                style={{ color: 'var(--text-muted)' }}
              >
                Resolved ({nonPending.length})
              </h2>
              <div className="space-y-2">
                {nonPending.map((c) => (
                  <CandidateCard
                    key={c.id}
                    candidate={c}
                    onFilterByReference={setReferenceFilter}
                  />
                ))}
              </div>
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
  onFilterByReference,
  ticketProgress,
}: {
  candidate: PromotionCandidate;
  urgent?: boolean;
  selectable?: boolean;
  selected?: boolean;
  onToggleSelect?: () => void;
  onFilterByReference?: (key: string) => void;
  ticketProgress?: TicketProgress;
}) {
  const navigate = useNavigate();
  const cfg = STATUS_CONFIG[candidate.status] ?? STATUS_CONFIG.Pending;
  const StatusIcon = cfg.icon;

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
          <span className="badge" style={{ backgroundColor: cfg.bg, color: cfg.color }}>
            <StatusIcon size={10} />
            {candidate.status}
          </span>
        </div>
        <div className="flex items-center gap-2 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          <span className="font-medium">{candidate.sourceEnv}</span>
          <ArrowRight size={12} style={{ color: 'var(--text-muted)' }} />
          <span className="font-medium">{candidate.targetEnv}</span>
          <span
            className="ml-2 px-1.5 py-0.5 rounded text-[11px] font-mono"
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
        </div>
        <div className="flex items-center gap-4 mt-2 text-[11px]" style={{ color: 'var(--text-muted)' }}>
          <span className="flex items-center gap-1">
            <Clock size={10} />
            {formatDistanceToNow(new Date(candidate.createdAt), { addSuffix: true })}
          </span>
          <TicketsBadge candidate={candidate} progress={ticketProgress} />
        </div>
        {/* Work-item tickets — key + optional title, click-to-filter + external link */}
        {(() => {
          const tickets = (candidate.sourceEventReferences ?? []).filter(
            (r) => r.type === 'work-item' && (r.key ?? '').trim().length > 0,
          );
          if (tickets.length === 0 && candidate.inheritedCount === 0) return null;
          return (
            <div className="flex items-center gap-1.5 flex-wrap mt-2">
              {tickets.map((ref, i) => {
                const filterKey = ref.key ?? '';
                const href = resolveReferenceHref({
                  type: ref.type,
                  url: ref.url ?? undefined,
                  provider: ref.provider ?? undefined,
                  revision: ref.revision ?? undefined,
                });
                const chipLabel = ref.title ? `${ref.key} — ${ref.title}` : ref.key!;
                return (
                  <span key={`wi-${i}`} className="inline-flex items-center gap-1 text-[10px]">
                    <button
                      type="button"
                      onClick={(e) => {
                        e.stopPropagation();
                        if (filterKey && onFilterByReference) onFilterByReference(filterKey);
                      }}
                      className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded transition-opacity hover:opacity-80"
                      style={{
                        backgroundColor: 'var(--bg-secondary)',
                        color: 'var(--text-secondary)',
                        cursor: filterKey ? 'pointer' : 'default',
                        maxWidth: 200,
                      }}
                      title={ref.title ? `${ref.key} — ${ref.title}` : `Filter by ${filterKey}`}
                    >
                      <Ticket size={10} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
                      <span className="font-medium truncate">{chipLabel}</span>
                    </button>
                    {href && (
                      <a
                        href={href}
                        target="_blank"
                        rel="noopener noreferrer"
                        onClick={(e) => e.stopPropagation()}
                        style={{ color: 'var(--text-muted)' }}
                        className="transition-opacity hover:opacity-80"
                        title="Open ticket"
                      >
                        <ExternalLink size={10} />
                      </a>
                    )}
                  </span>
                );
              })}
              {candidate.inheritedCount > 0 && (
                <span
                  className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium"
                  style={{
                    backgroundColor: 'var(--bg-secondary)',
                    color: 'var(--text-muted)',
                    border: '1px dashed var(--border-color)',
                  }}
                  title={`${candidate.inheritedCount} refs/people inherited from superseded predecessors — open details to view`}
                >
                  +{candidate.inheritedCount} inherited
                </span>
              )}
            </div>
          );
        })()}
        {/* People — reference-level (from work-item tickets) + promotion root */}
        {(() => {
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
          return (
            <div className="flex items-center gap-1.5 flex-wrap mt-1.5">
              {chips.map((p, i) => (
                <span
                  key={`p-${p.role}-${p.via ?? 'root'}-${i}`}
                  className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px]"
                  style={{
                    backgroundColor: p.fromPromotion ? 'var(--accent-bg)' : 'var(--bg-secondary)',
                    color: 'var(--text-secondary)',
                    border: p.fromPromotion ? '1px solid color-mix(in srgb, var(--accent) 30%, transparent)' : undefined,
                  }}
                  title={[
                    `${roleDisplay(p)}: ${p.displayName ?? p.email ?? '—'}`,
                    p.via ? `via ${p.via}` : null,
                    p.email ?? null,
                  ].filter(Boolean).join(' · ')}
                >
                  <span style={{ color: 'var(--text-muted)' }}>{roleDisplay(p)}:</span>
                  <span className="font-medium">{p.displayName ?? p.email ?? '—'}</span>
                  {p.via && (
                    <span style={{ color: 'var(--text-muted)' }}>· {p.via}</span>
                  )}
                </span>
              ))}
            </div>
          );
        })()}
      </div>
      <ArrowUpRight size={16} style={{ color: 'var(--text-muted)' }} className="shrink-0 mt-1" />
    </div>
  );
}

/**
 * Inline ticket-progress indicator for the list. The list response surfaces
 * the candidate's own work-item refs (sourceEventReferences) but not approval
 * state, so the parent fetches /work-items/{key}?... lazily for Pending rows
 * only. Non-pending rows render "—" so historical state isn't fetched.
 */
function TicketsBadge({
  candidate,
  progress,
}: {
  candidate: PromotionCandidate;
  progress: TicketProgress | undefined;
}) {
  const bundleSize = (candidate.sourceEventReferences ?? []).filter(
    (r) => r.type === 'work-item',
  ).length;
  if (bundleSize === 0) {
    return (
      <span
        className="inline-flex items-center gap-1"
        title="No work-items in this candidate's bundle"
      >
        <Ticket size={10} />—
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
      <span className="inline-flex items-center gap-1" title="Loading ticket state…">
        <Ticket size={10} />
        {progress.approved}/{progress.total}
        <ProgressBar approved={progress.approved} total={progress.total} />
      </span>
    );
  }
  const label = progress.approved === 0
    ? 'Awaiting'
    : `${progress.approved}/${progress.total} approved`;
  return (
    <span
      className="inline-flex items-center gap-1.5"
      title={
        progress.rejected > 0
          ? `${progress.approved} approved, ${progress.rejected} rejected, ${progress.total - progress.approved - progress.rejected} pending`
          : `${progress.approved} of ${progress.total} approved`
      }
    >
      <Ticket size={10} />
      {label}
      <ProgressBar
        approved={progress.approved}
        total={progress.total}
        rejected={progress.rejected}
      />
    </span>
  );
}

function ProgressBar({
  approved,
  total,
  rejected = 0,
}: {
  approved: number;
  total: number;
  rejected?: number;
}) {
  if (total === 0) return null;
  const approvedPct = (approved / total) * 100;
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
      {rejectedPct > 0 && (
        <span
          className="inline-block align-top h-full"
          style={{ width: `${rejectedPct}%`, backgroundColor: 'var(--danger)' }}
        />
      )}
    </span>
  );
}
