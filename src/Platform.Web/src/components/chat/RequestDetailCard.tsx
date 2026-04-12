import { useNavigate } from 'react-router-dom';
import { ExternalLink } from 'lucide-react';

interface Decision {
  approverName: string;
  decision: string;
  comment?: string;
  decidedAt: string;
}

interface DetailData {
  id: string;
  serviceName: string;
  status: string;
  requesterName: string;
  createdAt: string;
  inputs?: Record<string, unknown>;
  executionStatus?: string;
  executionOutput?: string;
  approvalStatus?: string;
  decisions?: Decision[];
}

interface Props {
  title?: string;
  data: DetailData | unknown;
}

export function RequestDetailCard({ title, data }: Props) {
  const navigate = useNavigate();
  const detail = data as DetailData;

  if (!detail?.id) return null;

  return (
    <div className="mt-2 rounded-lg border overflow-hidden" style={{ borderColor: 'var(--border-color)' }}>
      {title && (
        <div className="px-3 py-2 text-xs font-semibold" style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)' }}>
          {title}
        </div>
      )}
      <div className="p-3 space-y-2" style={{ backgroundColor: 'var(--bg-primary)' }}>
        <div className="flex items-center justify-between">
          <span className="text-xs font-semibold" style={{ color: 'var(--text-primary)' }}>{detail.serviceName}</span>
          <span className="text-xs px-1.5 py-0.5 rounded" style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)' }}>
            {detail.status}
          </span>
        </div>

        <div className="text-xs space-y-1" style={{ color: 'var(--text-muted)' }}>
          <div>Requester: {detail.requesterName}</div>
          <div>Created: {new Date(detail.createdAt).toLocaleString()}</div>
        </div>

        {detail.inputs && Object.keys(detail.inputs).length > 0 && (
          <div className="mt-2">
            <div className="text-xs font-medium mb-1" style={{ color: 'var(--text-secondary)' }}>Inputs</div>
            <div className="space-y-0.5">
              {Object.entries(detail.inputs).map(([key, val]) => (
                <div key={key} className="text-xs flex gap-2" style={{ color: 'var(--text-muted)' }}>
                  <span className="font-mono">{key}:</span>
                  <span>{String(val)}</span>
                </div>
              ))}
            </div>
          </div>
        )}

        {detail.executionStatus && (
          <div className="text-xs" style={{ color: 'var(--text-muted)' }}>
            Execution: {detail.executionStatus}
          </div>
        )}

        {detail.decisions && detail.decisions.length > 0 && (
          <div className="mt-2">
            <div className="text-xs font-medium mb-1" style={{ color: 'var(--text-secondary)' }}>Approvals</div>
            {detail.decisions.map((d, i) => (
              <div key={i} className="text-xs" style={{ color: 'var(--text-muted)' }}>
                {d.approverName}: {d.decision} {d.comment && `- "${d.comment}"`}
              </div>
            ))}
          </div>
        )}

        <button
          onClick={() => navigate(`/requests/${detail.id}`)}
          className="mt-2 flex items-center gap-1 text-xs font-medium transition-colors"
          style={{ color: 'var(--accent)' }}
        >
          View full details <ExternalLink size={11} />
        </button>
      </div>
    </div>
  );
}
