import { Bot, User, Shield, Clock, Settings } from 'lucide-react';
import type { AuditEntry } from '@/lib/types';

interface Props {
  entries: AuditEntry[];
}

const actorIcons: Record<string, React.ComponentType<{ className?: string }>> = {
  User: User,
  System: Settings,
  Bot: Bot,
  Service: Shield,
};

function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function RequestTimeline({ entries }: Props) {
  if (entries.length === 0) {
    return (
      <p className="text-sm" style={{ color: 'var(--text-muted)' }}>
        No timeline events
      </p>
    );
  }

  return (
    <div className="relative">
      {/* Vertical connector line */}
      <div
        className="absolute left-4 top-6 bottom-6 w-px"
        style={{ backgroundColor: 'var(--border-color)' }}
      />

      <ul className="space-y-0">
        {entries.map((entry, idx) => {
          const Icon = actorIcons[entry.actorType] || Clock;
          const isLast = idx === entries.length - 1;

          return (
            <li key={entry.id} className={`relative flex gap-3 ${isLast ? '' : 'pb-5'}`}>
              {/* Icon circle */}
              <div
                className="relative z-10 flex-shrink-0 w-8 h-8 rounded-full flex items-center justify-center"
                style={{
                  backgroundColor: 'var(--bg-tertiary)',
                  border: '2px solid var(--border-color)',
                }}
              >
                <Icon className="w-3.5 h-3.5" style={{ color: 'var(--text-secondary)' }} />
              </div>

              {/* Content */}
              <div className="flex-1 min-w-0 pt-0.5">
                <p className="text-sm" style={{ color: 'var(--text-primary)' }}>
                  <span className="font-medium">{entry.actorName}</span>
                  <span style={{ color: 'var(--text-muted)' }}> {entry.action.replace(/([A-Z])/g, ' $1').trim().toLowerCase()}</span>
                </p>
                <p className="text-xs mt-0.5" style={{ color: 'var(--text-muted)' }}>
                  {formatTimestamp(entry.timestamp)}
                  {entry.module && (
                    <span
                      className="ml-2 px-1.5 py-0.5 rounded text-xs"
                      style={{ backgroundColor: 'var(--bg-tertiary)' }}
                    >
                      {entry.module}
                    </span>
                  )}
                </p>
              </div>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
