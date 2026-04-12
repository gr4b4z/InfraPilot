import { Clock, User, Shield, Settings, Bot } from 'lucide-react';

interface TimelineEvent {
  action: string;
  actorName: string;
  actorType: string;
  module: string;
  timestamp: string;
}

interface TimelineData {
  detail?: {
    serviceName: string;
    status: string;
    requesterName: string;
  };
  timeline: TimelineEvent[];
}

interface Props {
  title?: string;
  data: TimelineData | unknown;
}

const actorIcons: Record<string, React.ComponentType<{ size?: number; className?: string }>> = {
  user: User,
  system: Settings,
  bot: Bot,
  service: Shield,
};

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  });
}

export function TimelineCard({ title, data }: Props) {
  const d = data as TimelineData;
  const events = d?.timeline ?? [];

  if (events.length === 0) {
    return (
      <div className="mt-2 p-3 rounded-lg text-xs" style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-muted)' }}>
        No timeline events found.
      </div>
    );
  }

  return (
    <div className="mt-2 rounded-lg border overflow-hidden" style={{ borderColor: 'var(--border-color)' }}>
      {title && (
        <div className="px-3 py-2 text-xs font-semibold" style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)' }}>
          {title}
        </div>
      )}
      <div className="p-3" style={{ backgroundColor: 'var(--bg-primary)' }}>
        {d.detail && (
          <div className="mb-3 pb-2" style={{ borderBottom: '1px solid var(--border-color)' }}>
            <span className="text-xs font-semibold" style={{ color: 'var(--text-primary)' }}>{d.detail.serviceName}</span>
            <span className="text-xs ml-2 px-1.5 py-0.5 rounded" style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-muted)' }}>
              {d.detail.status}
            </span>
          </div>
        )}
        <div className="relative">
          <div className="absolute left-[9px] top-3 bottom-3 w-px" style={{ backgroundColor: 'var(--border-color)' }} />
          <div className="space-y-2">
            {events.map((event, idx) => {
              const Icon = actorIcons[event.actorType.toLowerCase()] || Clock;
              return (
                <div key={idx} className="relative flex gap-2">
                  <div
                    className="relative z-10 w-5 h-5 rounded-full flex items-center justify-center shrink-0 mt-0.5"
                    style={{ backgroundColor: 'var(--bg-tertiary)', border: '1.5px solid var(--border-color)' }}
                  >
                    <Icon size={10} style={{ color: 'var(--text-muted)' }} />
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="text-xs" style={{ color: 'var(--text-primary)' }}>
                      <span className="font-medium">{event.actorName}</span>{' '}
                      <span style={{ color: 'var(--text-muted)' }}>
                        {event.action.replace(/\./g, ' ').replace(/([A-Z])/g, ' $1').trim().toLowerCase()}
                      </span>
                    </div>
                    <div className="text-xs" style={{ color: 'var(--text-muted)' }}>
                      {formatTime(event.timestamp)}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );
}
