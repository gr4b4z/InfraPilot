import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { ApprovalRequest } from '@/lib/types';
import { api } from '@/lib/api';
import { formatDistanceToNow } from 'date-fns';
import { Clock, CheckCircle, XCircle, AlertTriangle, Shield, ArrowUpRight } from 'lucide-react';

export function ApprovalsPage() {
  const [approvals, setApprovals] = useState<ApprovalRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const navigate = useNavigate();

  useEffect(() => {
    api.getApprovals()
      .then((data) => setApprovals(data.items || []))
      .catch(() => setApprovals([]))
      .finally(() => setLoading(false));
  }, []);

  const pending = approvals.filter((a) => a.status === 'Pending');
  const resolved = approvals.filter((a) => a.status !== 'Pending');

  return (
    <div className="space-y-6">
      {/* Header */}
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Approvals
        </h1>
        <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
          Review and act on pending approval requests
        </p>
      </div>

      {/* Stats */}
      <div className="grid grid-cols-3 gap-3">
        {[
          { label: 'Pending', value: pending.length, icon: Clock, color: 'var(--warning)', bg: 'var(--warning-bg)' },
          { label: 'Approved', value: approvals.filter(a => a.status === 'Approved').length, icon: CheckCircle, color: 'var(--success)', bg: 'var(--success-bg)' },
          { label: 'Rejected', value: approvals.filter(a => a.status === 'Rejected').length, icon: XCircle, color: 'var(--danger)', bg: 'var(--danger-bg)' },
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
              <p className="text-lg font-semibold leading-none" style={{ color: 'var(--text-primary)' }}>{s.value}</p>
              <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>{s.label}</p>
            </div>
          </div>
        ))}
      </div>

      {loading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => <div key={i} className="skeleton h-24" />)}
        </div>
      ) : approvals.length === 0 ? (
        <div
          className="flex flex-col items-center justify-center py-20 rounded-xl border"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <div
            className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
          >
            <Shield size={24} />
          </div>
          <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>No pending approvals</p>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            Approval requests will appear here when they need your action
          </p>
        </div>
      ) : (
        <div className="space-y-6">
          {/* Pending section */}
          {pending.length > 0 && (
            <div>
              <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-3" style={{ color: 'var(--text-muted)' }}>
                Awaiting Your Review ({pending.length})
              </h2>
              <div className="space-y-2">
                {pending.map((approval) => (
                  <ApprovalCard key={approval.id} approval={approval} urgent />
                ))}
              </div>
            </div>
          )}

          {/* Resolved section */}
          {resolved.length > 0 && (
            <div>
              <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-3" style={{ color: 'var(--text-muted)' }}>
                Resolved ({resolved.length})
              </h2>
              <div className="space-y-2">
                {resolved.map((approval) => (
                  <ApprovalCard key={approval.id} approval={approval} />
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function ApprovalCard({ approval, urgent }: { approval: ApprovalRequest; urgent?: boolean }) {
  const navigate = useNavigate();

  const statusConfig = {
    Pending: { icon: Clock, color: 'var(--warning)', bg: 'var(--warning-bg)', label: 'Pending' },
    Approved: { icon: CheckCircle, color: 'var(--success)', bg: 'var(--success-bg)', label: 'Approved' },
    Rejected: { icon: XCircle, color: 'var(--danger)', bg: 'var(--danger-bg)', label: 'Rejected' },
    ChangesRequested: { icon: AlertTriangle, color: 'var(--warning)', bg: 'var(--warning-bg)', label: 'Changes Requested' },
  }[approval.status] || { icon: Clock, color: 'var(--text-muted)', bg: 'var(--bg-secondary)', label: approval.status };

  const StatusIcon = statusConfig.icon;

  return (
    <div
      onClick={() => navigate(`/approvals/${approval.id}`)}
      className="card-hover rounded-xl border p-4 cursor-pointer"
      style={{
        borderColor: urgent ? statusConfig.color + '40' : 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        borderLeft: urgent ? `3px solid ${statusConfig.color}` : undefined,
      }}
    >
      <div className="flex items-start justify-between gap-4">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <h3 className="text-[14px] font-semibold truncate" style={{ color: 'var(--text-primary)' }}>
              {approval.serviceRequest?.catalogItem?.name || 'Service request'}
            </h3>
            <span className="badge" style={{ backgroundColor: statusConfig.bg, color: statusConfig.color }}>
              <StatusIcon size={10} />
              {statusConfig.label}
            </span>
          </div>
          <p className="text-[12px]" style={{ color: 'var(--text-secondary)' }}>
            Requested by <strong>{approval.serviceRequest?.requesterName}</strong>
          </p>
          <div className="flex items-center gap-4 mt-2 text-[11px]" style={{ color: 'var(--text-muted)' }}>
            <span>Strategy: {approval.strategy}</span>
            <span>Decisions: {approval.decisions?.length || 0}</span>
            {approval.timeoutAt && (
              <span className="flex items-center gap-1">
                <Clock size={10} />
                Timeout {formatDistanceToNow(new Date(approval.timeoutAt), { addSuffix: true })}
              </span>
            )}
          </div>
        </div>
        <ArrowUpRight size={16} style={{ color: 'var(--text-muted)' }} className="shrink-0 mt-1" />
      </div>
    </div>
  );
}
