import { useEffect, useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import { api } from '@/lib/api';
import type { PromotionCandidate, PromotionApprovalEntry, PromotionStatus } from '@/lib/api';
import { formatDistanceToNow, format } from 'date-fns';
import {
  ArrowLeft,
  ArrowRight,
  Clock,
  CheckCircle,
  XCircle,
  Rocket,
  ExternalLink,
  GitPullRequest,
  User,
} from 'lucide-react';
import { CopyEmailButton } from '@/components/deployments/CopyEmailButton';

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

export function PromotionDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [candidate, setCandidate] = useState<PromotionCandidate | null>(null);
  const [approvals, setApprovals] = useState<PromotionApprovalEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [comment, setComment] = useState('');
  const [actionLoading, setActionLoading] = useState(false);
  const [actionDone, setActionDone] = useState<string | null>(null);

  const fetchData = () => {
    api
      .getPromotion(id!)
      .then((data) => {
        setCandidate(data.candidate);
        setApprovals(data.approvals || []);
      })
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    fetchData();
  }, [id]);

  const handleAction = async (action: 'approve' | 'reject') => {
    setActionLoading(true);
    try {
      if (action === 'approve') {
        await api.approvePromotion(id!, comment || undefined);
      } else {
        await api.rejectPromotion(id!, comment || undefined);
      }
      setActionDone(action === 'approve' ? 'Approved' : 'Rejected');
      setComment('');
      fetchData();
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

  if (error && !candidate) {
    return (
      <div className="flex flex-col items-center justify-center h-64 gap-2">
        <XCircle size={24} style={{ color: 'var(--danger)' }} />
        <p className="text-[14px] font-medium" style={{ color: 'var(--danger)' }}>{error}</p>
        <Link to="/promotions" className="text-[13px] font-medium" style={{ color: 'var(--accent)' }}>
          Back to promotions
        </Link>
      </div>
    );
  }

  if (!candidate) return null;

  const cfg = STATUS_CONFIG[candidate.status] ?? STATUS_CONFIG.Pending;
  const StatusIcon = cfg.icon;

  return (
    <div className="max-w-3xl mx-auto space-y-6">
      {/* Breadcrumb */}
      <Link
        to="/promotions"
        className="inline-flex items-center gap-1.5 text-[12px] font-medium transition-colors hover:text-[var(--accent)]"
        style={{ color: 'var(--text-muted)' }}
      >
        <ArrowLeft size={14} /> Back to promotions
      </Link>

      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            {candidate.product} / {candidate.service}
          </h1>
          <div className="flex items-center gap-3 mt-1.5 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
            <span className="font-medium">{candidate.sourceEnv}</span>
            <ArrowRight size={14} style={{ color: 'var(--text-muted)' }} />
            <span className="font-medium">{candidate.targetEnv}</span>
            <span
              className="px-1.5 py-0.5 rounded text-[12px] font-mono"
              style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
            >
              {candidate.version}
            </span>
          </div>
        </div>
        <span className="badge" style={{ backgroundColor: cfg.bg, color: cfg.color }}>
          <StatusIcon size={10} />
          {candidate.status}
        </span>
      </div>

      {/* Success banner */}
      {actionDone && (
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{
            backgroundColor: actionDone === 'Approved' ? 'var(--success-bg)' : 'var(--danger-bg)',
            borderColor: actionDone === 'Approved' ? 'var(--success)' : 'var(--danger)',
            color: actionDone === 'Approved' ? 'var(--success)' : 'var(--danger)',
          }}
        >
          {actionDone === 'Approved' ? <CheckCircle size={18} /> : <XCircle size={18} />}
          <span className="text-[13px] font-medium">
            {actionDone === 'Approved'
              ? 'You approved this promotion.'
              : 'You rejected this promotion.'}
          </span>
        </div>
      )}

      {/* Error banner */}
      {error && candidate && (
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{ backgroundColor: 'var(--danger-bg)', borderColor: 'var(--danger)', color: 'var(--danger)' }}
        >
          <XCircle size={18} />
          <span className="text-[13px] font-medium">{error}</span>
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left column */}
        <div className="lg:col-span-2 space-y-4">
          {/* Approve / Reject actions */}
          {candidate.canApprove && !actionDone && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2
                className="text-[11px] font-semibold uppercase tracking-wider mb-4"
                style={{ color: 'var(--text-muted)' }}
              >
                Your Decision
              </h2>
              <textarea
                value={comment}
                onChange={(e) => setComment(e.target.value)}
                placeholder="Optional comment..."
                rows={3}
                className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none mb-3"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-secondary)',
                  color: 'var(--text-primary)',
                }}
              />
              <div className="flex items-center gap-2">
                <button
                  onClick={() => handleAction('approve')}
                  disabled={actionLoading}
                  className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium transition-opacity"
                  style={{
                    backgroundColor: 'var(--success)',
                    color: '#fff',
                    opacity: actionLoading ? 0.6 : 1,
                  }}
                >
                  <CheckCircle size={14} />
                  Approve
                </button>
                <button
                  onClick={() => handleAction('reject')}
                  disabled={actionLoading}
                  className="flex items-center gap-1.5 px-4 py-2 rounded-lg text-[13px] font-medium transition-opacity"
                  style={{
                    backgroundColor: 'var(--danger)',
                    color: '#fff',
                    opacity: actionLoading ? 0.6 : 1,
                  }}
                >
                  <XCircle size={14} />
                  Reject
                </button>
              </div>
            </div>
          )}

          {/* Approval trail */}
          {approvals.length > 0 && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2
                className="text-[11px] font-semibold uppercase tracking-wider mb-4"
                style={{ color: 'var(--text-muted)' }}
              >
                Approval Trail ({approvals.length})
              </h2>
              <div className="space-y-2">
                {approvals.map((a) => {
                  const isApproved = a.decision === 'Approved';
                  return (
                    <div
                      key={a.id}
                      className="flex items-start gap-3 p-3 rounded-lg border"
                      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
                    >
                      <div
                        className="w-7 h-7 rounded-full flex items-center justify-center shrink-0 mt-0.5"
                        style={{
                          backgroundColor: isApproved ? 'var(--success-bg)' : 'var(--danger-bg)',
                          color: isApproved ? 'var(--success)' : 'var(--danger)',
                        }}
                      >
                        {isApproved ? <CheckCircle size={14} /> : <XCircle size={14} />}
                      </div>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between">
                          <span className="inline-flex items-center gap-1.5 text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
                            {a.approverName}
                            <CopyEmailButton email={a.approverEmail} />
                          </span>
                          <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                            {format(new Date(a.createdAt), 'MMM d, HH:mm')}
                          </span>
                        </div>
                        <div className="mt-1">
                          <span
                            className="badge"
                            style={{
                              backgroundColor: isApproved ? 'var(--success-bg)' : 'var(--danger-bg)',
                              color: isApproved ? 'var(--success)' : 'var(--danger)',
                            }}
                          >
                            {a.decision}
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
          )}
        </div>

        {/* Right column */}
        <div className="space-y-4">
          {/* Details card */}
          <div
            className="rounded-xl border p-5"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
          >
            <h2
              className="text-[11px] font-semibold uppercase tracking-wider mb-4"
              style={{ color: 'var(--text-muted)' }}
            >
              Details
            </h2>
            <div className="space-y-3 text-[13px]">
              <div className="flex items-center gap-2">
                <GitPullRequest size={14} style={{ color: 'var(--text-muted)' }} />
                <span style={{ color: 'var(--text-muted)' }}>Product:</span>
                <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
                  {candidate.product}
                </span>
              </div>
              <div className="flex items-center gap-2">
                <Rocket size={14} style={{ color: 'var(--text-muted)' }} />
                <span style={{ color: 'var(--text-muted)' }}>Service:</span>
                <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
                  {candidate.service}
                </span>
              </div>
              {(candidate.sourceDeployerName || candidate.sourceDeployerEmail) && (
                <div className="flex items-center gap-2">
                  <User size={14} style={{ color: 'var(--text-muted)' }} />
                  <span style={{ color: 'var(--text-muted)' }}>Deployer:</span>
                  <span className="inline-flex items-center gap-1.5 font-medium" style={{ color: 'var(--text-primary)' }}>
                    {candidate.sourceDeployerName ?? candidate.sourceDeployerEmail}
                    <CopyEmailButton email={candidate.sourceDeployerEmail} />
                  </span>
                </div>
              )}
            </div>
          </div>

          {/* Timestamps */}
          <div
            className="rounded-xl border p-5"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
          >
            <h2
              className="text-[11px] font-semibold uppercase tracking-wider mb-4"
              style={{ color: 'var(--text-muted)' }}
            >
              Timestamps
            </h2>
            <div className="space-y-3 text-[13px]">
              <div>
                <span className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
                  Created
                </span>
                <p className="font-medium mt-0.5" style={{ color: 'var(--text-primary)' }}>
                  {format(new Date(candidate.createdAt), 'MMM d, yyyy HH:mm')}
                  <span className="ml-2 text-[11px] font-normal" style={{ color: 'var(--text-muted)' }}>
                    ({formatDistanceToNow(new Date(candidate.createdAt), { addSuffix: true })})
                  </span>
                </p>
              </div>
              {candidate.approvedAt && (
                <div>
                  <span className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
                    Approved
                  </span>
                  <p className="font-medium mt-0.5" style={{ color: 'var(--text-primary)' }}>
                    {format(new Date(candidate.approvedAt), 'MMM d, yyyy HH:mm')}
                  </p>
                </div>
              )}
              {candidate.deployedAt && (
                <div>
                  <span className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
                    Deployed
                  </span>
                  <p className="font-medium mt-0.5" style={{ color: 'var(--text-primary)' }}>
                    {format(new Date(candidate.deployedAt), 'MMM d, yyyy HH:mm')}
                  </p>
                </div>
              )}
            </div>
          </div>

          {/* External run link */}
          {candidate.externalRunUrl && (
            <a
              href={candidate.externalRunUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="flex items-center gap-2 rounded-xl border p-4 text-[13px] font-medium transition-colors hover:text-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
              }}
            >
              <ExternalLink size={14} style={{ color: 'var(--accent)' }} />
              View CI run
            </a>
          )}
        </div>
      </div>
    </div>
  );
}
