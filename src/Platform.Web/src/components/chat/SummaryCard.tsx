import { DeploymentListCard } from './DeploymentListCard';

interface SummaryData {
  total: number;
  completed: number;
  failed: number;
  awaitingApproval: number;
  executing: number;
  other: number;
  from: string;
  to: string;
  items: Array<{
    id: string;
    serviceName: string;
    status: string;
    requesterName: string;
    createdAt: string;
    updatedAt: string;
  }>;
}

interface Props {
  title?: string;
  data: SummaryData | unknown;
}

const metricColor: Record<string, string> = {
  Completed: 'var(--color-swo-green)',
  Failed: '#FCA5A5',
  Executing: 'var(--color-swo-cyan)',
  'Awaiting Approval': 'var(--color-swo-yellow)',
};

export function SummaryCard({ title, data }: Props) {
  const summary = data as SummaryData;
  if (!summary || summary.total === undefined) return null;

  const metrics = [
    { label: 'Total', value: summary.total, color: 'var(--text-primary)' },
    { label: 'Completed', value: summary.completed, color: metricColor.Completed },
    { label: 'Failed', value: summary.failed, color: metricColor.Failed },
    { label: 'Executing', value: summary.executing, color: metricColor.Executing },
    { label: 'Awaiting Approval', value: summary.awaitingApproval, color: metricColor['Awaiting Approval'] },
  ].filter((m) => m.value > 0 || m.label === 'Total');

  return (
    <div className="mt-2 space-y-2">
      <div className="rounded-lg border overflow-hidden" style={{ borderColor: 'var(--border-color)' }}>
        {title && (
          <div className="px-3 py-2 text-xs font-semibold" style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)' }}>
            {title}
          </div>
        )}
        <div className="grid grid-cols-3 gap-px" style={{ backgroundColor: 'var(--border-color)' }}>
          {metrics.map((m) => (
            <div key={m.label} className="px-3 py-2 text-center" style={{ backgroundColor: 'var(--bg-primary)' }}>
              <div className="text-lg font-bold" style={{ color: m.color }}>{m.value}</div>
              <div className="text-xs" style={{ color: 'var(--text-muted)' }}>{m.label}</div>
            </div>
          ))}
        </div>
      </div>

      {summary.items && summary.items.length > 0 && (
        <DeploymentListCard title="Details" data={summary.items} />
      )}
    </div>
  );
}
