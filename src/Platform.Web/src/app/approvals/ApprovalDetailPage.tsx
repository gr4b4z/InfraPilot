import { useParams, Link } from 'react-router-dom';
import { useEffect, useState } from 'react';
import type { ApprovalRequest } from '@/lib/types';
import { ApprovalActions } from '@/components/approvals/ApprovalActions';
import { ApprovalProgress } from '@/components/approvals/ApprovalProgress';
import { StatusBadge } from '@/components/requests/StatusBadge';
import { api } from '@/lib/api';
import { format, formatDistanceToNow } from 'date-fns';
import {
  ArrowLeft,
  Clock,
  Users,
  Shield,
  AlertTriangle,
  CheckCircle,
  XCircle,
  Info,
} from 'lucide-react';

export function ApprovalDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [approval, setApproval] = useState<ApprovalRequest | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [actionLoading, setActionLoading] = useState(false);
  const [actionDone, setActionDone] = useState<string | null>(null);

  const fetchApproval = () => {
    api.getApproval(id!)
      .then((data) => setApproval(data.approval))
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    fetchApproval();
  }, [id]);

  const handleAction = async (action: 'approve' | 'reject' | 'request-changes', comment: string) => {
    setActionLoading(true);
    try {
      if (action === 'approve') {
        await api.approveRequest(id!, comment || undefined);
      } else if (action === 'reject') {
        await api.rejectRequest(id!, comment);
      } else {
        await api.requestChanges(id!, comment);
      }
      setActionDone(action === 'approve' ? 'Approved' : action === 'reject' ? 'Rejected' : 'Changes Requested');
      fetchApproval();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Action failed');
    } finally {
      setActionLoading(false);
    }
  };

  if (loading) {
    return (
      <div className="max-w-3xl mx-auto space-y-4">
        <div className="skeleton h-8 w-48" />
        <div className="skeleton h-64" />
      </div>
    );
  }

  if (error && !approval) {
    return (
      <div className="flex flex-col items-center justify-center h-64 gap-2">
        <AlertTriangle size={24} style={{ color: 'var(--danger)' }} />
        <p className="text-[14px] font-medium" style={{ color: 'var(--danger)' }}>{error}</p>
        <Link to="/approvals" className="text-[13px] font-medium" style={{ color: 'var(--accent)' }}>Back to approvals</Link>
      </div>
    );
  }

  if (!approval) return null;

  const isPending = approval.status === 'Pending';
  const request = approval.serviceRequest;
  const approvedCount = approval.decisions?.filter((d) => d.decision === 'Approved').length || 0;
  const totalDecisions = approval.decisions?.length || 0;
  const quorumRequired = approval.quorumCount || (approval.strategy === 'Any' ? 1 : totalDecisions || 1);

  let inputs: Record<string, unknown> = {};
  try {
    if (request?.inputsJson) {
      inputs = typeof request.inputsJson === 'string' ? JSON.parse(request.inputsJson) : request.inputsJson;
    }
  } catch { /* ignore */ }

  return (
    <div className="max-w-3xl mx-auto space-y-6">
      {/* Breadcrumb */}
      <Link
        to="/approvals"
        className="inline-flex items-center gap-1.5 text-[12px] font-medium transition-colors hover:text-[var(--accent)]"
        style={{ color: 'var(--text-muted)' }}
      >
        <ArrowLeft size={14} /> Back to approvals
      </Link>

      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            {request?.catalogItem?.name || 'Approval Review'}
          </h1>
          <div className="flex items-center gap-3 mt-1.5 text-[12px]" style={{ color: 'var(--text-muted)' }}>
            <span>Requested by <span style={{ color: 'var(--text-secondary)' }}>{request?.requesterName}</span></span>
            {request?.createdAt && (
              <span>{formatDistanceToNow(new Date(request.createdAt), { addSuffix: true })}</span>
            )}
          </div>
        </div>
        {request && <StatusBadge status={request.status} />}
      </div>

      {/* Success banner */}
      {actionDone && (
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{
            backgroundColor: actionDone === 'Approved' ? 'var(--success-bg)' : actionDone === 'Rejected' ? 'var(--danger-bg)' : 'var(--warning-bg)',
            borderColor: actionDone === 'Approved' ? 'var(--success)' : actionDone === 'Rejected' ? 'var(--danger)' : 'var(--warning)',
            color: actionDone === 'Approved' ? 'var(--success)' : actionDone === 'Rejected' ? 'var(--danger)' : 'var(--warning)',
          }}
        >
          {actionDone === 'Approved' ? <CheckCircle size={18} /> : actionDone === 'Rejected' ? <XCircle size={18} /> : <AlertTriangle size={18} />}
          <span className="text-[13px] font-medium">
            {actionDone === 'Approved' ? 'You approved this request.' : actionDone === 'Rejected' ? 'You rejected this request.' : 'You requested changes for this request.'}
          </span>
        </div>
      )}

      {/* Error banner */}
      {error && approval && (
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{ backgroundColor: 'var(--danger-bg)', borderColor: 'var(--danger)', color: 'var(--danger)' }}
        >
          <AlertTriangle size={18} />
          <span className="text-[13px] font-medium">{error}</span>
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left column */}
        <div className="lg:col-span-2 space-y-4">
          {/* Request parameters */}
          {Object.keys(inputs).length > 0 && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-4" style={{ color: 'var(--text-muted)' }}>
                Request Parameters
              </h2>
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                {Object.entries(inputs).map(([key, value]) => (
                  <div key={key}>
                    <span className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
                      {key.replace(/_/g, ' ')}
                    </span>
                    <p className="text-[13px] font-medium mt-0.5" style={{ color: 'var(--text-primary)' }}>
                      {typeof value === 'boolean' ? (value ? 'Yes' : 'No') : String(value ?? '\u2014')}
                    </p>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Approval actions */}
          {isPending && !actionDone && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-4" style={{ color: 'var(--text-muted)' }}>
                Your Decision
              </h2>
              <ApprovalActions
                onApprove={(comment) => handleAction('approve', comment)}
                onReject={(comment) => handleAction('reject', comment)}
                onRequestChanges={(comment) => handleAction('request-changes', comment)}
                loading={actionLoading}
              />
            </div>
          )}

          {/* Previous decisions */}
          {approval.decisions && approval.decisions.length > 0 && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-4" style={{ color: 'var(--text-muted)' }}>
                Decisions ({approval.decisions.length})
              </h2>
              <div className="space-y-2">
                {approval.decisions.map((d) => (
                  <div
                    key={d.id}
                    className="flex items-start gap-3 p-3 rounded-lg border"
                    style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
                  >
                    <div
                      className="w-7 h-7 rounded-full flex items-center justify-center shrink-0 mt-0.5"
                      style={{
                        backgroundColor: d.decision === 'Approved' ? 'var(--success-bg)' : d.decision === 'Rejected' ? 'var(--danger-bg)' : 'var(--warning-bg)',
                        color: d.decision === 'Approved' ? 'var(--success)' : d.decision === 'Rejected' ? 'var(--danger)' : 'var(--warning)',
                      }}
                    >
                      {d.decision === 'Approved' ? <CheckCircle size={14} /> : d.decision === 'Rejected' ? <XCircle size={14} /> : <AlertTriangle size={14} />}
                    </div>
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center justify-between">
                        <span className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>{d.approverName}</span>
                        <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                          {format(new Date(d.decidedAt), 'MMM d, HH:mm')}
                        </span>
                      </div>
                      <span
                        className="badge mt-1"
                        style={{
                          backgroundColor: d.decision === 'Approved' ? 'var(--success-bg)' : d.decision === 'Rejected' ? 'var(--danger-bg)' : 'var(--warning-bg)',
                          color: d.decision === 'Approved' ? 'var(--success)' : d.decision === 'Rejected' ? 'var(--danger)' : 'var(--warning)',
                        }}
                      >
                        {d.decision}
                      </span>
                      {d.comment && (
                        <p className="text-[12px] mt-1.5" style={{ color: 'var(--text-secondary)' }}>"{d.comment}"</p>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>

        {/* Right column */}
        <div className="space-y-4">
          {/* Approval progress */}
          <div
            className="rounded-xl border p-5"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
          >
            <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-4" style={{ color: 'var(--text-muted)' }}>
              Progress
            </h2>
            <ApprovalProgress
              approved={approvedCount}
              total={totalDecisions}
              required={quorumRequired}
              strategy={approval.strategy}
            />
          </div>

          {/* Approval info */}
          <div
            className="rounded-xl border p-5"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
          >
            <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-4" style={{ color: 'var(--text-muted)' }}>
              Approval Details
            </h2>
            <div className="space-y-3 text-[13px]">
              <div className="flex items-center gap-2">
                <Shield size={14} style={{ color: 'var(--text-muted)' }} />
                <span style={{ color: 'var(--text-muted)' }}>Strategy:</span>
                <span className="font-medium" style={{ color: 'var(--text-primary)' }}>{approval.strategy}</span>
              </div>
              {approval.quorumCount && (
                <div className="flex items-center gap-2">
                  <Users size={14} style={{ color: 'var(--text-muted)' }} />
                  <span style={{ color: 'var(--text-muted)' }}>Quorum:</span>
                  <span className="font-medium" style={{ color: 'var(--text-primary)' }}>{approval.quorumCount} required</span>
                </div>
              )}
              {approval.timeoutAt && (
                <div className="flex items-center gap-2">
                  <Clock size={14} style={{ color: 'var(--text-muted)' }} />
                  <span style={{ color: 'var(--text-muted)' }}>Timeout:</span>
                  <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
                    {formatDistanceToNow(new Date(approval.timeoutAt), { addSuffix: true })}
                  </span>
                </div>
              )}
            </div>
          </div>

          {/* Info box */}
          <div
            className="rounded-xl border p-4 text-[12px]"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--info-bg)', color: 'var(--info)' }}
          >
            <div className="flex items-start gap-2">
              <Info size={14} className="shrink-0 mt-0.5" />
              <div>
                <p className="font-medium mb-1">Where are approvers defined?</p>
                <p style={{ color: 'var(--text-secondary)' }}>
                  Approvers are configured per service in the YAML catalog definitions.
                  Each service specifies an <code className="font-mono px-1 py-0.5 rounded" style={{ backgroundColor: 'var(--bg-secondary)' }}>approver_group</code> (e.g.{' '}
                  <code className="font-mono px-1 py-0.5 rounded" style={{ backgroundColor: 'var(--bg-secondary)' }}>SWO-PLT-NetworkAdmins</code>)
                  which maps to an Entra ID security group.
                </p>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
