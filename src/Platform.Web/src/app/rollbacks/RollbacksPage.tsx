import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api } from '@/lib/api';
import type {
  RollbackRequest,
  RollbackStatus,
  RollbackItem,
  RollbackItemStatus,
} from '@/lib/api';
import { useSettingsStore } from '@/stores/settingsStore';
import { formatDistanceToNow } from 'date-fns';
import {
  Clock,
  CheckCircle,
  XCircle,
  Undo2,
  ArrowRight,
  User,
  Plus,
  Ban,
  ShieldAlert,
} from 'lucide-react';
import { CreateRollbackPanel } from './CreateRollbackPanel';
import { useDocumentTitle, scopeTitle } from '@/lib/pageTitle';
import { EnvBadge, EnvLabel } from '@/components/environments/EnvBadge';
import { useEnvControlStyle } from '@/components/environments/useEnvColor';
import { FilterPanel } from '@/components/ui/FilterPanel';
import { useEntityRefresh } from '@/hooks/useEntityEvents';

const STATUS_CONFIG: Record<
  RollbackStatus,
  { icon: typeof Clock; color: string; bg: string }
> = {
  Pending: { icon: Clock, color: 'var(--warning)', bg: 'var(--warning-bg)' },
  Approved: { icon: CheckCircle, color: 'var(--info)', bg: 'var(--info-bg)' },
  RollingBack: { icon: Undo2, color: 'var(--accent)', bg: 'var(--accent-bg)' },
  RolledBack: { icon: CheckCircle, color: 'var(--success)', bg: 'var(--success-bg)' },
  Rejected: { icon: XCircle, color: 'var(--danger)', bg: 'var(--danger-bg)' },
  Cancelled: { icon: Ban, color: 'var(--text-muted)', bg: 'var(--bg-secondary)' },
};

const ITEM_STATUS_COLOR: Record<RollbackItemStatus, string> = {
  Pending: 'var(--text-muted)',
  RollingBack: 'var(--accent)',
  RolledBack: 'var(--success)',
  Failed: 'var(--danger)',
  Skipped: 'var(--text-muted)',
};

const STATUS_OPTIONS: Array<{ label: string; value: string }> = [
  { label: 'All', value: '' },
  { label: 'Pending', value: 'Pending' },
  { label: 'Approved', value: 'Approved' },
  { label: 'Rolling back', value: 'RollingBack' },
  { label: 'Rolled back', value: 'RolledBack' },
  { label: 'Rejected', value: 'Rejected' },
  { label: 'Cancelled', value: 'Cancelled' },
];

export function RollbacksPage() {
  const getOrderedEnvironments = useSettingsStore((s) => s.getOrderedEnvironments);
  const [searchParams, setSearchParams] = useSearchParams();
  const [requests, setRequests] = useState<RollbackRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [productFilter, setProductFilter] = useState('');
  const [targetEnvFilter, setTargetEnvFilter] = useState('');
  const [actioningId, setActioningId] = useState<string | null>(null);
  // Which card has its override reason prompt open, and the last override failure.
  const [overridingId, setOverridingId] = useState<string | null>(null);
  const [overrideError, setOverrideError] = useState<string | null>(null);

  // The create panel opens when ?new=1 is present (deep-linked from a deploy
  // event detail), or via the "New rollback" button.
  const showCreate = searchParams.get('new') === '1';
  const prefill = useMemo(
    () => ({
      product: searchParams.get('product') ?? '',
      targetEnv: searchParams.get('targetEnv') ?? '',
      service: searchParams.get('service') ?? '',
    }),
    [searchParams],
  );

  // `?new=1` is the one piece of state here that's genuinely in the URL — it's how a deploy event
  // deep-links into the create panel, and a link that opens a form should say so. The list's own
  // filters are local state, reported because they're what the tab is showing.
  useDocumentTitle(
    showCreate
      ? [
          'New rollback',
          scopeTitle({
            product: prefill.product,
            service: prefill.service,
            targetEnv: prefill.targetEnv,
          }),
          'Rollbacks',
        ]
      : [
          // The empty status is the "All" option — nothing to report.
          statusFilter ? STATUS_OPTIONS.find((o) => o.value === statusFilter)?.label : null,
          scopeTitle({ product: productFilter, targetEnv: targetEnvFilter }),
          'Rollbacks',
        ],
  );

  const fetchData = () => {
    setLoading(true);
    const params: Record<string, string> = {};
    if (statusFilter) params.status = statusFilter;
    if (productFilter) params.product = productFilter;
    if (targetEnvFilter) params.targetEnv = targetEnvFilter;
    api
      .listRollbacks(params)
      .then((data) => setRequests(data.requests || []))
      .catch(() => setRequests([]))
      .finally(() => setLoading(false));
  };

  // Rollback items move through RollingBack → RolledBack/Failed server-side as completions
  // are matched — without push this list goes stale mid-rollback, its most-watched moment.
  const rollbacksTick = useEntityRefresh(['rollback']);

  useEffect(() => {
    fetchData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [statusFilter, productFilter, targetEnvFilter, rollbacksTick]);

  const openCreate = () => {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.set('new', '1');
      return next;
    });
  };

  const closeCreate = () => {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.delete('new');
      next.delete('product');
      next.delete('targetEnv');
      next.delete('service');
      return next;
    }, { replace: true });
  };

  const handleApprove = async (id: string) => {
    setActioningId(id);
    try {
      await api.approveRollback(id);
    } catch {
      /* refetch reflects the real state regardless */
    } finally {
      setActioningId(null);
      fetchData();
    }
  };

  const handleReject = async (id: string) => {
    setActioningId(id);
    try {
      await api.rejectRollback(id);
    } catch {
      /* refetch */
    } finally {
      setActioningId(null);
      fetchData();
    }
  };

  const handleCancel = async (id: string) => {
    setActioningId(id);
    try {
      await api.cancelRollback(id);
    } catch {
      /* backend authorises; refetch reflects the outcome */
    } finally {
      setActioningId(null);
      fetchData();
    }
  };

  // Admin gate override. The reason is mandatory server-side and is recorded on the approval row, so
  // it is prompted for here rather than sent blank — and the error is surfaced instead of swallowed,
  // because unlike approve/reject a refetch alone would leave the operator guessing why nothing moved.
  const handleOverride = async (id: string, reason: string) => {
    setActioningId(id);
    setOverrideError(null);
    try {
      await api.overrideRollbackApproval(id, reason);
      setOverridingId(null);
    } catch (e) {
      setOverrideError(e instanceof Error ? e.message : 'Failed to override the approval gate');
    } finally {
      setActioningId(null);
      fetchData();
    }
  };

  const productOptions = useMemo(() => {
    const set = new Set<string>();
    for (const r of requests) if (r.product) set.add(r.product);
    if (productFilter) set.add(productFilter);
    return Array.from(set).sort();
  }, [requests, productFilter]);

  // Configured order, not alphabetical: environments read as a pipeline (dev → staging → prod), and
  // that sequence is more useful in a picker than the alphabet.
  const targetEnvOptions = useMemo(() => {
    const set = new Set<string>();
    for (const r of requests) if (r.targetEnv) set.add(r.targetEnv);
    if (targetEnvFilter) set.add(targetEnvFilter);
    return getOrderedEnvironments(Array.from(set));
  }, [requests, targetEnvFilter, getOrderedEnvironments]);

  const targetEnvSelectStyle = useEnvControlStyle(targetEnvFilter);
  const getDisplayName = useSettingsStore((s) => s.getDisplayName);

  const active = useMemo(
    () => requests.filter((r) => r.status === 'Pending' || r.status === 'Approved' || r.status === 'RollingBack'),
    [requests],
  );
  const resolved = useMemo(
    () => requests.filter((r) => r.status === 'RolledBack' || r.status === 'Rejected' || r.status === 'Cancelled'),
    [requests],
  );

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            Rollbacks
          </h1>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            Revert services to a prior version, manually or by aligning to a reference environment
          </p>
        </div>
        <button
          onClick={openCreate}
          className="flex items-center gap-1.5 px-3 py-2 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-90 shrink-0"
          style={{ backgroundColor: 'var(--accent)', color: '#fff' }}
        >
          <Plus size={14} />
          New rollback
        </button>
      </div>

      {/* Filters */}
      <FilterPanel
        activeCount={[statusFilter, productFilter, targetEnvFilter].filter(Boolean).length}
      >
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
        {/* Carries the selected environment's colour, matching the badges on the rows below. */}
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
      </FilterPanel>

      {loading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="skeleton h-24" />
          ))}
        </div>
      ) : requests.length === 0 ? (
        <div
          className="flex flex-col items-center justify-center py-20 rounded-xl border"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <div
            className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
          >
            <Undo2 size={24} />
          </div>
          <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>
            No rollback requests
          </p>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            Start a rollback to revert a service to an earlier version
          </p>
        </div>
      ) : (
        <div className="space-y-6">
          {active.length > 0 && (
            <div>
              <h2
                className="text-[11px] font-semibold uppercase tracking-wider mb-3"
                style={{ color: 'var(--text-muted)' }}
              >
                Active ({active.length})
              </h2>
              <div className="space-y-2">
                {active.map((r) => (
                  <RollbackCard
                    key={r.id}
                    request={r}
                    busy={actioningId === r.id}
                    onApprove={() => handleApprove(r.id)}
                    onReject={() => handleReject(r.id)}
                    onCancel={() => handleCancel(r.id)}
                    overriding={overridingId === r.id}
                    overrideError={overridingId === r.id ? overrideError : null}
                    onStartOverride={() => {
                      setOverridingId(r.id);
                      setOverrideError(null);
                    }}
                    onCancelOverride={() => {
                      setOverridingId(null);
                      setOverrideError(null);
                    }}
                    onOverride={(reason) => handleOverride(r.id, reason)}
                  />
                ))}
              </div>
            </div>
          )}
          {resolved.length > 0 && (
            <div>
              <h2
                className="text-[11px] font-semibold uppercase tracking-wider mb-3"
                style={{ color: 'var(--text-muted)' }}
              >
                Resolved ({resolved.length})
              </h2>
              <div className="space-y-2">
                {resolved.map((r) => (
                  <RollbackCard key={r.id} request={r} />
                ))}
              </div>
            </div>
          )}
        </div>
      )}

      {showCreate && (
        <CreateRollbackPanel
          prefill={prefill}
          onClose={closeCreate}
          onCreated={() => {
            closeCreate();
            fetchData();
          }}
        />
      )}
    </div>
  );
}

function RollbackCard({
  request,
  busy,
  onApprove,
  onReject,
  onCancel,
  overriding,
  overrideError,
  onStartOverride,
  onCancelOverride,
  onOverride,
}: {
  request: RollbackRequest;
  busy?: boolean;
  onApprove?: () => void;
  onReject?: () => void;
  onCancel?: () => void;
  overriding?: boolean;
  overrideError?: string | null;
  onStartOverride?: () => void;
  onCancelOverride?: () => void;
  onOverride?: (reason: string) => void;
}) {
  const { getDisplayName } = useSettingsStore();
  const [reason, setReason] = useState('');
  const cfg = STATUS_CONFIG[request.status] ?? STATUS_CONFIG.Pending;
  const StatusIcon = cfg.icon;
  const canApprove = request.canApprove && request.status === 'Pending';
  // Admin-only, and only where the server says the call would succeed.
  const canOverride = request.canOverride && request.status === 'Pending';
  // Only Pending requests can be cancelled — once Approved, the webhook has fired and the
  // executor is acting, so there's nothing we can meaningfully recall.
  const cancellable = request.status === 'Pending';

  return (
    <div
      className="rounded-xl border p-4"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        borderLeft: canApprove ? '3px solid var(--warning)' : undefined,
      }}
    >
      <div className="flex items-start gap-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1 flex-wrap">
            <h3 className="text-[14px] font-semibold truncate" style={{ color: 'var(--text-primary)' }}>
              {request.product}
            </h3>
            <EnvBadge env={request.targetEnv} title={`Rolling back in ${getDisplayName(request.targetEnv)}`} />
            <span className="badge" style={{ backgroundColor: cfg.bg, color: cfg.color }}>
              <StatusIcon size={10} />
              {request.status}
            </span>
            <span
              className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium"
              style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
              title={
                request.mode === 'Align'
                  ? `Align to ${request.referenceEnv ?? 'reference env'}`
                  : 'Manual version selection'
              }
            >
              {request.mode === 'Align' && request.referenceEnv ? (
                <>
                  Align → <EnvLabel env={request.referenceEnv} className="font-semibold" />
                </>
              ) : (
                request.mode
              )}
            </span>
            {/* An approval that bypassed the gate must not read like a normal one. */}
            {request.approvalOverridden && (
              <span
                className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium"
                style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
                title="An admin overrode the approval gate for this rollback"
              >
                <ShieldAlert size={10} />
                Gate overridden
              </span>
            )}
          </div>

          {/* Items */}
          <div className="space-y-1 mt-2">
            {request.items.map((item) => (
              <ItemRow key={item.id} item={item} />
            ))}
          </div>

          {request.reason && (
            <p className="text-[12px] mt-2" style={{ color: 'var(--text-secondary)' }}>
              {request.reason}
            </p>
          )}

          <div className="flex items-center gap-4 mt-2 text-[11px]" style={{ color: 'var(--text-muted)' }}>
            <span className="flex items-center gap-1">
              <User size={10} />
              {request.createdByName || request.createdBy}
            </span>
            <span className="flex items-center gap-1">
              <Clock size={10} />
              {formatDistanceToNow(new Date(request.createdAt), { addSuffix: true })}
            </span>
          </div>
        </div>

        {/* Actions */}
        {(canApprove || cancellable || canOverride) && (
          <div className="flex flex-col gap-1.5 shrink-0">
            {canApprove && onApprove && (
              <button
                onClick={onApprove}
                disabled={busy}
                className="flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{ backgroundColor: 'var(--success-solid)', color: '#fff', opacity: busy ? 0.6 : 1 }}
              >
                <CheckCircle size={12} />
                Approve
              </button>
            )}
            {canApprove && onReject && (
              <button
                onClick={onReject}
                disabled={busy}
                className="flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{ backgroundColor: 'var(--danger-bg)', color: 'var(--danger)', opacity: busy ? 0.6 : 1 }}
              >
                <XCircle size={12} />
                Reject
              </button>
            )}
            {/* Last in the stack: a bypass should be the least reachable action on the card. */}
            {canOverride && onStartOverride && !overriding && (
              <button
                onClick={onStartOverride}
                disabled={busy}
                className="flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{
                  border: '1px solid var(--warning)',
                  color: 'var(--warning)',
                  opacity: busy ? 0.6 : 1,
                }}
                title="Approve without satisfying the gate (admin only, reason required)"
              >
                <ShieldAlert size={12} />
                Override gate
              </button>
            )}
            {cancellable && onCancel && (
              <button
                onClick={onCancel}
                disabled={busy}
                className="flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{
                  border: '1px solid var(--border-color)',
                  color: 'var(--text-muted)',
                  opacity: busy ? 0.6 : 1,
                }}
              >
                <Ban size={12} />
                Cancel
              </button>
            )}
          </div>
        )}
      </div>

      {/* Override reason prompt. Inline rather than a modal so the request it applies to stays on
          screen, and the reason is recorded verbatim on the approval row. */}
      {overriding && (
        <div
          className="mt-3 pt-3 border-t space-y-2"
          style={{ borderColor: 'var(--border-color)' }}
        >
          <label className="block text-[11px] font-medium" style={{ color: 'var(--text-muted)' }}>
            Why is this rollback bypassing its approval gate? (required)
          </label>
          <input
            type="text"
            value={reason}
            autoFocus
            onChange={(e) => setReason(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && reason.trim()) onOverride?.(reason.trim());
              if (e.key === 'Escape') onCancelOverride?.();
            }}
            placeholder="e.g. SEV1 INC-4213, approvers unreachable"
            className="w-full rounded-lg border px-3 py-1.5 text-[13px]"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
            }}
          />
          <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            Recorded against your name in the audit log and shown on this request as
            &ldquo;gate overridden&rdquo;.
          </p>
          <div className="flex items-center gap-2">
            <button
              onClick={() => onOverride?.(reason.trim())}
              disabled={busy || reason.trim().length === 0}
              className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity disabled:opacity-50"
              style={{ backgroundColor: 'var(--warning)', color: '#fff' }}
            >
              <ShieldAlert size={12} />
              {busy ? 'Overriding…' : 'Confirm override'}
            </button>
            <button
              onClick={onCancelOverride}
              className="px-3 py-1.5 rounded-lg text-[12px] font-medium"
              style={{ color: 'var(--text-muted)' }}
            >
              Cancel
            </button>
          </div>
          {overrideError && (
            <p className="text-[12px]" style={{ color: 'var(--danger)' }}>
              {overrideError}
            </p>
          )}
        </div>
      )}
    </div>
  );
}

function ItemRow({ item }: { item: RollbackItem }) {
  const color = ITEM_STATUS_COLOR[item.status] ?? 'var(--text-muted)';
  return (
    <div className="flex items-center gap-2 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
      <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
        {item.service}
      </span>
      <span className="font-mono text-[11px]">v{item.fromVersion}</span>
      <ArrowRight size={11} style={{ color: 'var(--text-muted)' }} />
      <span className="font-mono text-[11px]">v{item.toVersion}</span>
      <span
        className="ml-1 px-1.5 py-0.5 rounded text-[10px] font-medium"
        style={{ backgroundColor: 'var(--bg-secondary)', color }}
      >
        {item.status}
      </span>
      {item.externalRunUrl && (
        <a
          href={item.externalRunUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="text-[11px] hover:underline"
          style={{ color: 'var(--accent)' }}
        >
          run
        </a>
      )}
    </div>
  );
}
