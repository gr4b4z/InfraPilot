import { useNavigate } from 'react-router-dom';
import { ExternalLink } from 'lucide-react';

interface RequestItem {
  id: string;
  serviceName: string;
  status: string;
  requesterName: string;
  createdAt: string;
  updatedAt: string;
}

interface Props {
  title?: string;
  data: RequestItem[] | unknown;
}

const statusColors: Record<string, string> = {
  Completed: 'var(--color-swo-green)',
  Failed: '#FCA5A5',
  Executing: 'var(--color-swo-cyan)',
  AwaitingApproval: 'var(--color-swo-yellow)',
  Draft: 'var(--color-swo-gray-30)',
  Rejected: '#FCA5A5',
};

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  });
}

export function DeploymentListCard({ title, data }: Props) {
  const navigate = useNavigate();
  const items = Array.isArray(data) ? (data as RequestItem[]) : [];

  if (items.length === 0) {
    return (
      <div className="mt-2 p-3 rounded-lg text-xs" style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-muted)' }}>
        No matching requests found.
      </div>
    );
  }

  return (
    <div className="mt-2 rounded-lg overflow-hidden border" style={{ borderColor: 'var(--border-color)' }}>
      {title && (
        <div className="px-3 py-2 text-xs font-semibold" style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)' }}>
          {title} ({items.length})
        </div>
      )}
      <div className="divide-y" style={{ borderColor: 'var(--border-color)' }}>
        {items.map((item) => (
          <button
            key={item.id}
            onClick={() => navigate(`/requests/${item.id}`)}
            className="w-full flex items-center gap-2 px-3 py-2 text-left text-xs transition-colors hover:bg-[var(--bg-secondary)]"
            style={{ backgroundColor: 'var(--bg-primary)' }}
          >
            <span
              className="w-2 h-2 rounded-full shrink-0"
              style={{ backgroundColor: statusColors[item.status] || 'var(--text-muted)' }}
            />
            <div className="flex-1 min-w-0">
              <div className="font-medium truncate" style={{ color: 'var(--text-primary)' }}>
                {item.serviceName}
              </div>
              <div style={{ color: 'var(--text-muted)' }}>
                {item.requesterName} &middot; {formatDate(item.createdAt)}
              </div>
            </div>
            <span
              className="px-1.5 py-0.5 rounded text-xs shrink-0"
              style={{
                backgroundColor: (statusColors[item.status] || 'var(--text-muted)') + '20',
                color: 'var(--text-secondary)',
              }}
            >
              {item.status}
            </span>
            <ExternalLink size={12} style={{ color: 'var(--text-muted)' }} />
          </button>
        ))}
      </div>
    </div>
  );
}
