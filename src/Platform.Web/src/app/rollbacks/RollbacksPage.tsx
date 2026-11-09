import { useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '@/lib/api';
import type {
  RollbackRequest,
  RollbackStatus,
  RollbackItem,
  RollbackItemStatus,
} from '@/lib/api';
import { useSettingsStore } from '@/stores/settingsStore';
import { useAuthStore } from '@/stores/authStore';
import { format, formatDistanceToNow } from 'date-fns';
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
  SkipForward,
  AlertTriangle,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { CreateRollbackPanel } from './CreateRollbackPanel';
import { useDocumentTitle, scopeTitle } from '@/lib/pageTitle';
import { EnvBadge, EnvLabel } from '@/components/environments/EnvBadge';
import { useEnvControlStyle } from '@/components/environments/useEnvColor';
import { FilterPanel } from '@/components/ui/FilterPanel';
import { useEntityRefresh, useIsBackgroundRefresh } from '@/hooks/useEntityEvents';
import { HelpButton } from '@/components/guide/HelpButton';

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
  Superseded: 'var(--text-muted)',
};

// Items an executor may still act on. Everything else is terminal, however it closed.
const isOpenItem = (item: RollbackItem) => item.status === 'Pending' || item.status === 'RollingBack';

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
  // A link from a failed deployment ("Open rollbacks") lands on its product and environment. With
  // `new=1` the same parameters prefill the create panel instead, and the list starts unfiltered.
  const linkedScope = searchParams.get('new') === '1' ? null : searchParams;
  const [productFilter, setProductFilter] = useState(() => linkedScope?.get('product') ?? '');
  const [targetEnvFilter, setTargetEnvFilter] = useState(() => linkedScope?.get('targetEnv') ?? '');
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

  // `silent` refetches (realtime, after an action) keep the cards mounted and swap the response in by
  // id; only a new filter set shows skeletons.
  const fetchData = ({ silent = false } = {}) => {
    if (!silent) setLoading(true);
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
  const isBackgroundRefresh = useIsBackgroundRefresh();

  useEffect(() => {
    fetchData({ silent: isBackgroundRefresh(`${statusFilter}|${productFilter}|${targetEnvFilter}`) });
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
      fetchData({ silent: true });
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
      fetchData({ silent: true });
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
      fetchData({ silent: true });
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
      fetchData({ silent: true });
    }
  };

  // Admin-only actions on a request that is already approved. Both need a reason, and both let a
  // failure propagate so the card's prompt can show it — the same reasoning as the override.
  const handleAdminCancel = async (id: string, reason: string) => {
    setActioningId(id);
    try {
      await api.cancelRollback(id, reason);
    } finally {
      setActioningId(null);
      fetchData({ silent: true });
    }
  };

  const handleSkipItem = async (id: string, itemId: string, reason: string) => {
    setActioningId(id);
    try {
      await api.skipRollbackItem(id, itemId, reason);
    } finally {
      setActioningId(null);
      fetchData({ silent: true });
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
          <div className="flex items-center gap-1">
            <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
              Rollbacks
            </h1>
            <HelpButton
              page="Rollbacks"
              context={{
                status: statusFilter,
                product: productFilter,
                targetEnv: targetEnvFilter,
                openRequests: active.length,
              }}
            />
          </div>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            Revert services to a prior version, manually or by aligning to a reference environment
          </p>
        </div>
        <button
          onClick={openCreate}
          data-guide-anchor="rollbacks-new-button"
          className="flex items-center gap-1.5 px-3 py-2 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-90 shrink-0"
          style={{ backgroundColor: 'var(--accent)', color: 'var(--accent-fg)' }}
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
          data-guide-anchor="rollbacks-status-filter"
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
            <div data-guide-anchor="rollbacks-open-list">
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
                    onAdminCancel={(reason) => handleAdminCancel(r.id, reason)}
                    onSkipItem={(itemId, reason) => handleSkipItem(r.id, itemId, reason)}
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
            fetchData({ silent: true });
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
  onAdminCancel,
  onSkipItem,
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
  onAdminCancel?: (reason: string) => Promise<void>;
  onSkipItem?: (itemId: string, reason: string) => Promise<void>;
}) {
  const { getDisplayName } = useSettingsStore();
  const isAdmin = useAuthStore((s) => s.user?.isAdmin ?? false);
  // The admin cancel prompt is local to the card: unlike the override, which page-level state keeps
  // to one card at a time, it only ever concerns this request.
  const [adminCancelling, setAdminCancelling] = useState(false);
  const [adminCancelError, setAdminCancelError] = useState<string | null>(null);
  const cfg = STATUS_CONFIG[request.status] ?? STATUS_CONFIG.Pending;
  const StatusIcon = cfg.icon;
  const canApprove = request.canApprove && request.status === 'Pending';
  // Admin-only, and only where the server says the call would succeed.
  const canOverride = request.canOverride && request.status === 'Pending';
  // A Pending request can be cancelled by its creator or an admin (the server decides which).
  const cancellable = request.status === 'Pending';
  // Once approved, only an admin can cancel the request or skip one of its items, and only with a
  // reason. Neither recalls a deploy already under way; both stop the release pipeline from carrying
  // the remaining items, which is how a stuck request gets closed without a database edit.
  const inFlight = request.status === 'Approved' || request.status === 'RollingBack';
  const canAdminCancel = isAdmin && inFlight && !!onAdminCancel;
  const canSkipItems = isAdmin && inFlight && !!onSkipItem;

  const confirmAdminCancel = async (reason: string) => {
    setAdminCancelError(null);
    try {
      await onAdminCancel?.(reason);
      setAdminCancelling(false);
    } catch (e) {
      setAdminCancelError(e instanceof Error ? e.message : 'Failed to cancel the rollback');
    }
  };

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
              <ItemRow
                key={item.id}
                item={item}
                busy={busy}
                onSkip={canSkipItems && isOpenItem(item) ? (reason) => onSkipItem!(item.id, reason) : undefined}
              />
            ))}
          </div>

          {request.reason && (
            <p className="text-[12px] mt-2" style={{ color: 'var(--text-secondary)' }}>
              {request.reason}
            </p>
          )}

          {/* Who cancelled it and why. The reason is mandatory for an approved request, so a
              cancelled in-flight rollback always explains itself here. */}
          {request.status === 'Cancelled' && (request.cancelledByName || request.cancelReason) && (
            <p className="text-[12px] mt-2 flex items-start gap-1.5" style={{ color: 'var(--text-secondary)' }}>
              <Ban size={12} className="shrink-0 mt-0.5" style={{ color: 'var(--text-muted)' }} />
              <span>
                Cancelled
                {request.cancelledByName && <> by <b>{request.cancelledByName}</b></>}
                {request.cancelledAt && <> on {format(new Date(request.cancelledAt), 'MMM d, yyyy HH:mm')}</>}
                {request.cancelReason && <>: {request.cancelReason}</>}
              </span>
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
        {(canApprove || cancellable || canOverride || canAdminCancel) && (
          <div className="flex flex-col gap-1.5 shrink-0">
            {canApprove && onApprove && (
              <button
                onClick={onApprove}
                disabled={busy}
                data-guide-anchor="rollback-approve-button"
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
                data-guide-anchor="rollback-reject-button"
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
                data-guide-anchor="rollback-override-button"
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
            {canAdminCancel && !adminCancelling && (
              <button
                onClick={() => {
                  setAdminCancelling(true);
                  setAdminCancelError(null);
                }}
                disabled={busy}
                className="flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{
                  border: '1px solid var(--danger)',
                  color: 'var(--danger)',
                  opacity: busy ? 0.6 : 1,
                }}
                title="Stop the release carrying the remaining items (admin only, reason required)"
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
        <ReasonPrompt
          label="Why is this rollback bypassing its approval gate? (required)"
          placeholder="e.g. SEV1 INC-4213, approvers unreachable"
          hint={<>Recorded against your name in the audit log and shown on this request as &ldquo;gate overridden&rdquo;.</>}
          confirmLabel="Confirm override"
          busyLabel="Overriding…"
          icon={ShieldAlert}
          solid="var(--warning-solid)"
          busy={busy}
          error={overrideError}
          onConfirm={(reason) => onOverride?.(reason)}
          onDismiss={() => onCancelOverride?.()}
        />
      )}

      {adminCancelling && (
        <ReasonPrompt
          label="Why is this approved rollback being cancelled? (required)"
          placeholder="e.g. environment already moved on, see INC-4213"
          hint={
            <>
              Open items are marked Skipped and the release stops carrying them. A deploy that is
              already running is not recalled. Recorded in the audit log and shown on this request.
            </>
          }
          confirmLabel="Cancel rollback"
          busyLabel="Cancelling…"
          icon={Ban}
          solid="var(--danger-solid)"
          busy={busy}
          error={adminCancelError}
          onConfirm={confirmAdminCancel}
          onDismiss={() => {
            setAdminCancelling(false);
            setAdminCancelError(null);
          }}
        />
      )}
    </div>
  );
}

function ItemRow({
  item,
  busy,
  onSkip,
}: {
  item: RollbackItem;
  busy?: boolean;
  /** Present only when the current user (an admin) may skip this open item of an approved request. */
  onSkip?: (reason: string) => Promise<void>;
}) {
  const color = ITEM_STATUS_COLOR[item.status] ?? 'var(--text-muted)';
  const [skipping, setSkipping] = useState(false);
  const [skipError, setSkipError] = useState<string | null>(null);

  const confirmSkip = async (reason: string) => {
    setSkipError(null);
    try {
      await onSkip?.(reason);
      setSkipping(false);
    } catch (e) {
      setSkipError(e instanceof Error ? e.message : 'Failed to skip this item');
    }
  };

  return (
    <div>
      <div className="flex items-center gap-2 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
        <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
          {item.service}
        </span>
        <span className="font-mono text-[11px]">v{item.fromVersion}</span>
        <ArrowRight size={11} style={{ color: 'var(--text-muted)' }} />
        {/* No target: the item takes the service out of the environment. */}
        {item.remove ? (
          <span className="text-[11px] font-medium" style={{ color: 'var(--danger)' }}>removed</span>
        ) : (
          <span className="font-mono text-[11px]">v{item.toVersion}</span>
        )}
        {/* Links to the deploy that closed the item, when there is one: the landing, the deploy
            already running it at approval, or the deploy that superseded it. */}
        {item.completedDeployEventId ? (
          <Link
            to={`/deployments/events/${item.completedDeployEventId}`}
            className="ml-1 px-1.5 py-0.5 rounded text-[10px] font-medium hover:underline"
            style={{ backgroundColor: 'var(--bg-secondary)', color }}
            title="View the deploy that closed this item"
          >
            {item.status}
          </Link>
        ) : (
          <span
            className="ml-1 px-1.5 py-0.5 rounded text-[10px] font-medium"
            style={{ backgroundColor: 'var(--bg-secondary)', color }}
          >
            {item.status}
          </span>
        )}
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
        {onSkip && !skipping && (
          <button
            onClick={() => {
              setSkipping(true);
              setSkipError(null);
            }}
            disabled={busy}
            className="inline-flex items-center gap-1 text-[11px] font-medium hover:underline disabled:opacity-50"
            style={{ color: 'var(--text-muted)' }}
            title="Stop the release carrying this item (admin only, reason required)"
          >
            <SkipForward size={11} />
            Skip
          </button>
        )}
      </div>

      {/* A failed deploy never completes an item, so this is the only sign the executor tried. */}
      {item.lastFailedAttempt && (
        <p className="text-[11px] mt-0.5" style={{ color: 'var(--danger)' }}>
          <AlertTriangle size={11} className="inline mr-1 -mt-px" />
          <Link to={`/deployments/events/${item.lastFailedAttempt.deployEventId}`} className="hover:underline">
            The deploy of v{item.toVersion || item.fromVersion} failed{' '}
            {formatDistanceToNow(new Date(item.lastFailedAttempt.deployedAt), { addSuffix: true })}
          </Link>
          ; the item stays open until one succeeds.
        </p>
      )}

      {item.note && (
        <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          {item.note}
        </p>
      )}

      {skipping && (
        <ReasonPrompt
          label={`Why skip ${item.service}? (required)`}
          placeholder="e.g. keeping the current version, see INC-4213"
          hint={<>The release stops carrying this item. Recorded in the audit log and shown on the item.</>}
          confirmLabel="Skip item"
          busyLabel="Skipping…"
          icon={SkipForward}
          solid="var(--danger-solid)"
          busy={busy}
          error={skipError}
          onConfirm={confirmSkip}
          onDismiss={() => {
            setSkipping(false);
            setSkipError(null);
          }}
        />
      )}
    </div>
  );
}

/**
 * Inline reason prompt for the admin actions that must explain themselves (override, cancel, skip).
 * Inline rather than a modal, so the request or item it applies to stays on screen.
 */
function ReasonPrompt({
  label,
  placeholder,
  hint,
  confirmLabel,
  busyLabel,
  icon: Icon,
  solid,
  busy,
  error,
  onConfirm,
  onDismiss,
}: {
  label: string;
  placeholder: string;
  hint: React.ReactNode;
  confirmLabel: string;
  busyLabel: string;
  icon: LucideIcon;
  /** Background of the confirm button. */
  solid: string;
  busy?: boolean;
  error?: string | null;
  onConfirm: (reason: string) => void;
  onDismiss: () => void;
}) {
  const [reason, setReason] = useState('');
  const trimmed = reason.trim();
  return (
    <div className="mt-3 pt-3 border-t space-y-2" style={{ borderColor: 'var(--border-color)' }}>
      <label className="block text-[11px] font-medium" style={{ color: 'var(--text-muted)' }}>
        {label}
      </label>
      <input
        type="text"
        value={reason}
        autoFocus
        maxLength={500}
        onChange={(e) => setReason(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && trimmed && !busy) onConfirm(trimmed);
          if (e.key === 'Escape') onDismiss();
        }}
        placeholder={placeholder}
        className="w-full rounded-lg border px-3 py-1.5 text-[13px]"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-secondary)',
          color: 'var(--text-primary)',
        }}
      />
      <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
        {hint}
      </p>
      <div className="flex items-center gap-2">
        <button
          onClick={() => onConfirm(trimmed)}
          disabled={busy || trimmed.length === 0}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity disabled:opacity-50"
          style={{ backgroundColor: solid, color: '#fff' }}
        >
          <Icon size={12} />
          {busy ? busyLabel : confirmLabel}
        </button>
        <button
          onClick={onDismiss}
          className="px-3 py-1.5 rounded-lg text-[12px] font-medium"
          style={{ color: 'var(--text-muted)' }}
        >
          Cancel
        </button>
      </div>
      {error && (
        <p className="text-[12px]" style={{ color: 'var(--danger)' }}>
          {error}
        </p>
      )}
    </div>
  );
}
