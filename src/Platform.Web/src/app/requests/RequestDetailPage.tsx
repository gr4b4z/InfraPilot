import { useParams, Link } from 'react-router-dom';
import { useEffect, useState, useCallback } from 'react';
import { StatusBadge } from '@/components/requests/StatusBadge';
import type { ServiceRequest, AuditEntry, ExecutionResult } from '@/lib/types';
import { formatDistanceToNow, format } from 'date-fns';
import { ArrowLeft, Clock, User, Cpu, AlertTriangle, Copy, CheckCircle, ExternalLink, Loader2, RotateCcw, XCircle } from 'lucide-react';
import { api } from '@/lib/api';

interface ExecutionOutput {
  // Azure DevOps fields
  buildId?: number;
  buildNumber?: string;
  buildUrl?: string;
  pipelineName?: string;
  result?: string;
  sourceBranch?: string;
  startTime?: string;
  finishTime?: string;
  // Jira fields
  ticketKey?: string;
  ticketUrl?: string;
  ticketId?: string;
}

function parseExecutionOutput(outputJson?: string): ExecutionOutput | null {
  if (!outputJson) return null;
  try {
    return JSON.parse(outputJson);
  } catch {
    return null;
  }
}

export function RequestDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [request, setRequest] = useState<ServiceRequest | null>(null);
  const [auditLog, setAuditLog] = useState<AuditEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [cancelling, setCancelling] = useState(false);

  const fetchData = useCallback(() => {
    return Promise.all([
      api.getRequest(id!),
      api.getAuditLog({ entityId: id!, entityType: 'ServiceRequest' }).catch(() => ({ items: [], total: 0 })),
    ])
      .then(([reqData, auditData]) => {
        setRequest(reqData.request);
        setAuditLog(auditData.items || []);
      })
      .catch((err) => setError(err.message));
  }, [id]);

  useEffect(() => {
    fetchData().finally(() => setLoading(false));
  }, [fetchData]);

  // Auto-refresh while request is in Executing status (pipeline running)
  useEffect(() => {
    if (!request || request.status !== 'Executing') return;

    const interval = setInterval(() => {
      fetchData();
    }, 15_000);

    return () => clearInterval(interval);
  }, [request?.status, fetchData]);

  const copyId = () => {
    navigator.clipboard.writeText(id || '');
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const handleRetry = async () => {
    if (!id || retrying) return;
    setRetrying(true);
    try {
      await api.retryRequest(id);
      await fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to retry request');
    } finally {
      setRetrying(false);
    }
  };

  const handleCancel = async () => {
    if (!id || cancelling) return;
    setCancelling(true);
    try {
      await api.cancelRequest(id);
      await fetchData();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to cancel request');
    } finally {
      setCancelling(false);
    }
  };

  if (loading) {
    return (
      <div className="space-y-4">
        <div className="skeleton h-10 w-64" />
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 skeleton h-80" />
          <div className="skeleton h-60" />
        </div>
      </div>
    );
  }

  if (error || !request) {
    return (
      <div className="flex flex-col items-center justify-center h-64 gap-2">
        <AlertTriangle size={24} style={{ color: 'var(--danger)' }} />
        <p className="text-[14px] font-medium" style={{ color: 'var(--danger)' }}>{error || 'Request not found'}</p>
        <Link to="/requests" className="text-[13px] font-medium" style={{ color: 'var(--accent)' }}>Back to requests</Link>
      </div>
    );
  }

  let inputs: Record<string, unknown> = {};
  try {
    inputs = typeof request.inputsJson === 'string' ? JSON.parse(request.inputsJson) : request.inputsJson;
  } catch { /* ignore */ }

  const actorIcon = (type: string) => {
    switch (type) {
      case 'user': return <User size={12} />;
      case 'system': return <Cpu size={12} />;
      default: return <Clock size={12} />;
    }
  };

  return (
    <div className="space-y-6">
      {/* Breadcrumb + Header */}
      <div>
        <Link
          to="/requests"
          className="inline-flex items-center gap-1.5 text-[12px] font-medium mb-3 transition-colors hover:text-[var(--accent)]"
          style={{ color: 'var(--text-muted)' }}
        >
          <ArrowLeft size={14} /> Back to requests
        </Link>
        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
              {request.catalogItem?.name || 'Service Request'}
            </h1>
            <div className="flex items-center gap-3 mt-1.5">
              <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                Requested by <span style={{ color: 'var(--text-secondary)' }}>{request.requesterName}</span>
              </span>
              <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                {formatDistanceToNow(new Date(request.createdAt), { addSuffix: true })}
              </span>
              <button
                onClick={copyId}
                className="flex items-center gap-1 text-[11px] font-mono px-1.5 py-0.5 rounded transition-colors"
                style={{ color: 'var(--text-muted)', backgroundColor: 'var(--bg-secondary)' }}
                title="Copy request ID"
              >
                {copied ? <CheckCircle size={10} /> : <Copy size={10} />}
                {id?.slice(0, 8)}...
              </button>
              {request.externalTicketKey && (
                <a
                  href={request.externalTicketUrl || '#'}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex items-center gap-1.5 text-[11px] font-medium px-2 py-0.5 rounded-md transition-colors hover:opacity-80"
                  style={{ backgroundColor: 'var(--info-bg)', color: 'var(--info)' }}
                >
                  {request.externalTicketKey}
                  <ExternalLink size={10} />
                </a>
              )}
            </div>
          </div>
          <div className="flex items-center gap-2">
            <StatusBadge status={request.status} />
            {request.status === 'Failed' && (
              <button
                onClick={handleRetry}
                disabled={retrying}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 text-[12px] font-semibold rounded-lg transition-all cursor-pointer relative z-10"
                style={{
                  backgroundColor: 'var(--accent)',
                  color: 'white',
                  opacity: retrying ? 0.7 : 1,
                }}
              >
                {retrying ? (
                  <Loader2 size={13} className="animate-spin" />
                ) : (
                  <RotateCcw size={13} />
                )}
                Retry
              </button>
            )}
            {!['Completed', 'Rejected', 'Cancelled', 'ManuallyResolved', 'TimedOut'].includes(request.status) && (
              <button
                onClick={handleCancel}
                disabled={cancelling}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 text-[12px] font-semibold rounded-lg transition-all cursor-pointer relative z-10"
                style={{
                  backgroundColor: 'var(--danger-bg)',
                  color: 'var(--danger)',
                  opacity: cancelling ? 0.7 : 1,
                  border: '1px solid var(--danger)',
                }}
              >
                {cancelling ? (
                  <Loader2 size={13} className="animate-spin" />
                ) : (
                  <XCircle size={13} />
                )}
                Cancel
              </button>
            )}
          </div>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Main content */}
        <div className="lg:col-span-2 space-y-4">
          {/* Request inputs */}
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

          {/* Execution results */}
          {request.executionResults && request.executionResults.length > 0 && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-4" style={{ color: 'var(--text-muted)' }}>
                Execution Results
              </h2>
              <div className="space-y-3">
                {request.executionResults.map((er, i) => (
                  <ExecutionResultCard key={er.id} result={er} attempt={i + 1} />
                ))}
              </div>
            </div>
          )}

          {/* Approval info */}
          {request.approvalRequest && (
            <div
              className="rounded-xl border p-5"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-4" style={{ color: 'var(--text-muted)' }}>
                Approval
              </h2>
              <div className="flex items-center gap-4 mb-3 text-[13px]">
                <span style={{ color: 'var(--text-secondary)' }}>
                  Strategy: <strong>{request.approvalRequest.strategy}</strong>
                </span>
                <span
                  className="badge"
                  style={{
                    backgroundColor: request.approvalRequest.status === 'Approved' ? 'var(--success-bg)' :
                      request.approvalRequest.status === 'Rejected' ? 'var(--danger-bg)' : 'var(--warning-bg)',
                    color: request.approvalRequest.status === 'Approved' ? 'var(--success)' :
                      request.approvalRequest.status === 'Rejected' ? 'var(--danger)' : 'var(--warning)',
                  }}
                >
                  {request.approvalRequest.status}
                </span>
              </div>
              {request.approvalRequest.decisions?.map((d) => (
                <div
                  key={d.id}
                  className="p-3 rounded-lg border mb-2"
                  style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
                >
                  <div className="flex items-center justify-between text-[13px]">
                    <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
                      {d.approverName}
                    </span>
                    <div className="flex items-center gap-2">
                      <span
                        className="badge"
                        style={{
                          backgroundColor: d.decision === 'Approved' ? 'var(--success-bg)' : 'var(--danger-bg)',
                          color: d.decision === 'Approved' ? 'var(--success)' : 'var(--danger)',
                        }}
                      >
                        {d.decision}
                      </span>
                      <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                        {format(new Date(d.decidedAt), 'MMM d, HH:mm')}
                      </span>
                    </div>
                  </div>
                  {d.comment && (
                    <p className="mt-1.5 text-[12px]" style={{ color: 'var(--text-secondary)' }}>"{d.comment}"</p>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Timeline sidebar */}
        <div
          className="rounded-xl border p-5 h-fit"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <h2 className="text-[11px] font-semibold uppercase tracking-wider mb-4" style={{ color: 'var(--text-muted)' }}>
            Activity Timeline
          </h2>
          {auditLog.length === 0 ? (
            <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>No audit events found</p>
          ) : (
            <div className="space-y-0">
              {auditLog.map((entry, i) => (
                <div key={entry.id} className="flex gap-3">
                  <div className="flex flex-col items-center">
                    <div
                      className="w-6 h-6 rounded-full flex items-center justify-center shrink-0"
                      style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
                    >
                      {actorIcon(entry.actorType)}
                    </div>
                    {i < auditLog.length - 1 && (
                      <div className="w-px flex-1 my-1" style={{ backgroundColor: 'var(--border-color)' }} />
                    )}
                  </div>
                  <div className="pb-4">
                    <p className="text-[12px] font-medium" style={{ color: 'var(--text-primary)' }}>
                      {entry.action.replace(/\./g, ' ').replace(/request |approval /, '')}
                    </p>
                    <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
                      {entry.actorName} &middot; {format(new Date(entry.timestamp), 'MMM d, HH:mm')}
                    </p>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function ExecutionResultCard({ result, attempt }: { result: ExecutionResult; attempt: number }) {
  const output = parseExecutionOutput(result.outputJson);
  const isJira = !!output?.ticketKey;
  const isInProgress = result.status === 'InProgress';
  const isCompleted = result.status === 'Completed';
  const isFailed = result.status === 'Failed';

  const statusColor = isCompleted ? 'var(--success)' : isFailed ? 'var(--danger)' : 'var(--accent)';
  const statusBg = isCompleted ? 'var(--success-bg)' : isFailed ? 'var(--danger-bg)' : 'var(--accent-muted)';

  return (
    <div
      className="p-4 rounded-lg border"
      style={{
        borderColor: isInProgress ? 'var(--accent)' + '40' : 'var(--border-color)',
        backgroundColor: 'var(--bg-secondary)',
      }}
    >
      {/* Header row */}
      <div className="flex items-center justify-between text-[13px]">
        <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
          Attempt {attempt}
          {output?.pipelineName && (
            <span className="font-normal ml-2" style={{ color: 'var(--text-muted)' }}>
              &middot; {output.pipelineName}
            </span>
          )}
          {isJira && output?.ticketKey && (
            <span className="font-normal ml-2" style={{ color: 'var(--text-muted)' }}>
              &middot; {output.ticketKey}
            </span>
          )}
        </span>
        <div className="flex items-center gap-3">
          <span className="badge flex items-center gap-1" style={{ backgroundColor: statusBg, color: statusColor }}>
            {isInProgress && <Loader2 size={10} className="animate-spin" />}
            {isInProgress ? 'Running' : result.status}
          </span>
          {result.completedAt && (
            <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              {format(new Date(result.completedAt), 'MMM d, HH:mm')}
            </span>
          )}
        </div>
      </div>

      {/* Execution details */}
      {output && (
        <div className="mt-3 space-y-2">
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {output.buildNumber && (
              <span>Build <strong style={{ color: 'var(--text-secondary)' }}>#{output.buildNumber}</strong></span>
            )}
            {output.sourceBranch && (
              <span>{output.sourceBranch.replace('refs/heads/', '')}</span>
            )}
            {output.result && !isInProgress && (
              <span style={{ color: statusColor }}>{output.result}</span>
            )}
            {isInProgress && (
              <span className="flex items-center gap-1" style={{ color: 'var(--accent)' }}>
                <span className="relative flex h-2 w-2">
                  <span className="animate-ping absolute inline-flex h-full w-full rounded-full opacity-75" style={{ backgroundColor: 'var(--accent)' }} />
                  <span className="relative inline-flex rounded-full h-2 w-2" style={{ backgroundColor: 'var(--accent)' }} />
                </span>
                Pipeline running...
              </span>
            )}
          </div>

          {output.buildUrl && (
            <a
              href={output.buildUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1.5 text-[12px] font-medium transition-opacity hover:opacity-80"
              style={{ color: 'var(--accent)' }}
            >
              <ExternalLink size={12} />
              View in Azure DevOps
            </a>
          )}

          {output.ticketUrl && (
            <a
              href={output.ticketUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1.5 text-[12px] font-medium transition-opacity hover:opacity-80"
              style={{ color: 'var(--accent)' }}
            >
              <ExternalLink size={12} />
              View in Jira
            </a>
          )}
        </div>
      )}

      {/* Error message */}
      {result.errorMessage && (
        <div className="flex items-start gap-2 mt-3 text-[12px]" style={{ color: 'var(--danger)' }}>
          <AlertTriangle size={13} className="mt-0.5 shrink-0" />
          <span>{result.errorMessage}</span>
        </div>
      )}
    </div>
  );
}
