import { useNavigate } from 'react-router-dom';
import { ExternalLink, GitPullRequest, Ticket } from 'lucide-react';
import { formatDistanceToNow } from 'date-fns';

interface Participant {
  role: string;
  displayName?: string;
  email?: string;
}

interface ActivityItem {
  service: string;
  environment: string;
  version: string;
  previousVersion?: string;
  deployedAt: string;
  source: string;
  workItemKey?: string;
  workItemTitle?: string;
  prUrl?: string;
  participants: Participant[];
}

interface ActivityData {
  product?: string;
  environment?: string;
  since: string;
  items: ActivityItem[];
  navigationUrl?: string;
}

interface Props {
  title?: string;
  data: ActivityData | unknown;
}

export function DeploymentActivityCard({ title, data }: Props) {
  const navigate = useNavigate();
  const d = data as ActivityData;

  if (!d?.items?.length) {
    return (
      <div
        className="mt-2 p-3 rounded-lg text-xs"
        style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-muted)' }}
      >
        No deployments found in this period.
      </div>
    );
  }

  return (
    <div
      className="mt-2 rounded-lg overflow-hidden border"
      style={{ borderColor: 'var(--border-color)' }}
    >
      {title && (
        <div
          className="px-3 py-2 text-xs font-semibold flex items-center justify-between"
          style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)' }}
        >
          <span>
            {title} ({d.items.length})
          </span>
          {d.navigationUrl && (
            <button
              onClick={() => navigate(d.navigationUrl!)}
              className="flex items-center gap-1 hover:opacity-80 transition-opacity"
              style={{ color: 'var(--accent)' }}
            >
              Open <ExternalLink size={11} />
            </button>
          )}
        </div>
      )}
      <div
        className="divide-y"
        style={{ borderColor: 'var(--border-color)' }}
      >
        {d.items.map((item, idx) => {
          const prAuthor = item.participants.find((p) => p.role === 'PR Author');
          const qa = item.participants.find((p) => p.role === 'QA');

          return (
            <div
              key={idx}
              className="px-3 py-2 text-xs"
              style={{ backgroundColor: 'var(--bg-primary)' }}
            >
              {/* Row 1: service → env + version */}
              <div className="flex items-center justify-between gap-2">
                <div className="flex items-center gap-1.5 min-w-0">
                  <span
                    className="font-medium truncate"
                    style={{ color: 'var(--text-primary)' }}
                  >
                    {item.service}
                  </span>
                  <span style={{ color: 'var(--text-muted)' }}>→</span>
                  <span
                    className="px-1.5 py-0.5 rounded text-[10px] font-medium"
                    style={{
                      backgroundColor: 'var(--accent-muted)',
                      color: 'var(--accent)',
                    }}
                  >
                    {item.environment}
                  </span>
                </div>
                <span
                  className="font-mono text-[10px] font-medium whitespace-nowrap"
                  style={{ color: 'var(--text-primary)' }}
                >
                  {item.previousVersion
                    ? `v${item.previousVersion} → v${item.version}`
                    : `v${item.version}`}
                </span>
              </div>

              {/* Row 2: work item — title shown on hover */}
              {(item.workItemKey || item.workItemTitle) && (
                <div
                  className="mt-1 flex items-center gap-1.5 truncate"
                  style={{ color: 'var(--text-secondary)' }}
                  title={item.workItemTitle}
                >
                  <Ticket size={10} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
                  {item.workItemKey && (
                    <span className="font-medium">{item.workItemKey}</span>
                  )}
                </div>
              )}

              {/* Row 3: participants + time + PR link */}
              <div className="mt-1 flex items-center justify-between gap-2">
                <div
                  className="flex items-center gap-1.5 text-[10px] truncate"
                  style={{ color: 'var(--text-muted)' }}
                >
                  {prAuthor && (
                    <span>PR: {prAuthor.displayName ?? prAuthor.email}</span>
                  )}
                  {qa && (
                    <>
                      <span>&middot;</span>
                      <span>QA: {qa.displayName ?? qa.email}</span>
                    </>
                  )}
                  <span>&middot;</span>
                  <span>
                    {formatDistanceToNow(new Date(item.deployedAt), { addSuffix: true })}
                  </span>
                </div>
                {item.prUrl && (
                  <a
                    href={item.prUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="hover:opacity-80 transition-opacity shrink-0"
                    style={{ color: 'var(--accent)' }}
                    onClick={(e) => e.stopPropagation()}
                  >
                    <GitPullRequest size={11} />
                  </a>
                )}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
