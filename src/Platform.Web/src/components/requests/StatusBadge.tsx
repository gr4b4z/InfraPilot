import type { RequestStatus } from '@/lib/types';

const statusConfig: Record<RequestStatus, { bg: string; text: string; dot: string }> = {
  Draft: { bg: 'var(--bg-secondary)', text: 'var(--text-secondary)', dot: 'var(--text-muted)' },
  Validating: { bg: 'var(--info-bg)', text: 'var(--info)', dot: 'var(--info)' },
  ValidationFailed: { bg: 'var(--danger-bg)', text: 'var(--danger)', dot: 'var(--danger)' },
  AwaitingApproval: { bg: 'var(--warning-bg)', text: 'var(--warning)', dot: 'var(--warning)' },
  Executing: { bg: 'var(--info-bg)', text: 'var(--info)', dot: 'var(--info)' },
  Completed: { bg: 'var(--success-bg)', text: 'var(--success)', dot: 'var(--success)' },
  Failed: { bg: 'var(--danger-bg)', text: 'var(--danger)', dot: 'var(--danger)' },
  Retrying: { bg: 'var(--warning-bg)', text: 'var(--warning)', dot: 'var(--warning)' },
  Rejected: { bg: 'var(--danger-bg)', text: 'var(--danger)', dot: 'var(--danger)' },
  ChangesRequested: { bg: 'var(--warning-bg)', text: 'var(--warning)', dot: 'var(--warning)' },
  TimedOut: { bg: 'var(--bg-secondary)', text: 'var(--text-muted)', dot: 'var(--text-muted)' },
  ManuallyResolved: { bg: 'var(--success-bg)', text: 'var(--success)', dot: 'var(--success)' },
  Cancelled: { bg: 'var(--bg-secondary)', text: 'var(--text-muted)', dot: 'var(--text-muted)' },
};

interface Props {
  status: RequestStatus;
}

export function StatusBadge({ status }: Props) {
  const cfg = statusConfig[status] || statusConfig.Draft;

  return (
    <span
      className="badge"
      style={{ backgroundColor: cfg.bg, color: cfg.text }}
    >
      <span
        className="w-1.5 h-1.5 rounded-full shrink-0"
        style={{ backgroundColor: cfg.dot }}
      />
      {status.replace(/([A-Z])/g, ' $1').trim()}
    </span>
  );
}
