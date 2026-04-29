import { Link } from 'react-router-dom';
import { format } from 'date-fns';
import { X, ExternalLink, GitBranch, GitPullRequest, Ticket, Workflow, Users, Clock, Undo2 } from 'lucide-react';
import { useSettingsStore } from '@/stores/settingsStore';
import { CopyEmailButton } from './CopyEmailButton';
import type { DeploymentStateEntry, DeployReference, DeployParticipant } from '@/lib/types';
import { resolveReferenceHref } from '@/lib/refUrl';

interface Props {
  entry: DeploymentStateEntry;
  product: string;
  onClose: () => void;
}

const REFERENCE_ICONS: Record<string, typeof ExternalLink> = {
  pipeline: Workflow,
  repository: GitBranch,
  'pull-request': GitPullRequest,
  'work-item': Ticket,
};

export function DeployEventDetail({ entry, product, onClose }: Props) {
  const { getDisplayName } = useSettingsStore();

  // Event-level participants (no reference context — rare with the new model but kept for
  // backward compat with legacy payloads that put everything at event level).
  const eventParticipants = [
    ...entry.participants,
    ...(entry.enrichment?.participants ?? []),
  ];

  const labels = entry.enrichment?.labels ?? {};

  return (
    <div
      className="fixed inset-y-0 right-0 w-[420px] z-50 border-l shadow-lg overflow-y-auto"
      style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
    >
      <div className="p-5 space-y-5">
        {/* Header */}
        <div className="flex items-start justify-between">
          <div>
            <h2 className="text-base font-semibold" style={{ color: 'var(--text-primary)' }}>
              {entry.service}
            </h2>
            <p className="text-sm mt-0.5" style={{ color: 'var(--text-muted)' }}>
              {getDisplayName(entry.environment)}
            </p>
          </div>
          <button onClick={onClose} className="p-1 rounded-lg transition-colors hover:opacity-80" style={{ color: 'var(--text-muted)' }}>
            <X size={18} />
          </button>
        </div>

        {/* Rollback banner */}
        {entry.isRollback && (
          <div
            className="flex items-center gap-2 px-3 py-2 rounded-lg text-[13px]"
            style={{ backgroundColor: 'rgba(234,179,8,0.1)', color: '#eab308' }}
          >
            <Undo2 size={14} />
            <span>
              Rollback
              {entry.previousVersion && (
                <span style={{ color: 'var(--text-secondary)' }}>
                  {' '}— reverted from <span className="font-mono">v{entry.previousVersion}</span> to <span className="font-mono">v{entry.version}</span>
                </span>
              )}
            </span>
          </div>
        )}

        {/* Version info */}
        <div className="space-y-2">
          <div className="flex items-center justify-between text-[13px]">
            <span style={{ color: 'var(--text-muted)' }}>Version</span>
            <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>v{entry.version}</span>
          </div>
          {entry.previousVersion && !entry.isRollback && (
            <div className="flex items-center justify-between text-[13px]">
              <span style={{ color: 'var(--text-muted)' }}>Previous</span>
              <span className="font-mono" style={{ color: 'var(--text-secondary)' }}>v{entry.previousVersion}</span>
            </div>
          )}
          <div className="flex items-center justify-between text-[13px]">
            <span style={{ color: 'var(--text-muted)' }}>Deployed</span>
            <span className="flex items-center gap-1" style={{ color: 'var(--text-secondary)' }}>
              <Clock size={12} />
              {format(new Date(entry.deployedAt), 'MMM d, yyyy HH:mm')}
            </span>
          </div>
          <div className="flex items-center justify-between text-[13px]">
            <span style={{ color: 'var(--text-muted)' }}>Source</span>
            <span className="badge text-[11px]" style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}>
              {entry.source}
            </span>
          </div>
        </div>

        {/* References — participants shown inline under each reference */}
        {entry.references.length > 0 && (
          <div className="space-y-2">
            <h3 className="text-[12px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
              References
            </h3>
            <div className="space-y-2.5">
              {entry.references.map((ref, i) => (
                <ReferenceItem key={i} reference={ref} labels={labels} />
              ))}
            </div>
          </div>
        )}

        {/* Event-level participants — only shown when no references carry them (legacy payloads) */}
        {eventParticipants.length > 0 && (
          <div className="space-y-2">
            <h3 className="text-[12px] font-medium uppercase tracking-wider flex items-center gap-1.5" style={{ color: 'var(--text-muted)' }}>
              <Users size={12} /> Participants
            </h3>
            <div className="space-y-1">
              {eventParticipants.map((p, i) => (
                <ParticipantItem key={i} participant={p} />
              ))}
            </div>
          </div>
        )}

        {/* History link */}
        <Link
          to={`/deployments/${product}/${entry.service}/history`}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--accent)' }}
        >
          View History
          <ExternalLink size={12} />
        </Link>
      </div>
    </div>
  );
}

function ReferenceItem({ reference, labels }: { reference: DeployReference; labels: Record<string, string> }) {
  const Icon = REFERENCE_ICONS[reference.type] ?? ExternalLink;
  const label = buildReferenceLabel(reference, labels);
  const href = resolveReferenceHref(reference);
  const participants = reference.participants ?? [];

  return (
    <div className="space-y-1">
      <div className="flex items-center gap-2 text-[13px] min-w-0">
        <Icon size={13} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
        {href ? (
          <a
            href={href}
            target="_blank"
            rel="noopener noreferrer"
            className="hover:underline truncate"
            title={label}
            style={{ color: 'var(--accent)' }}
          >
            {label}
          </a>
        ) : (
          <span className="truncate" title={label} style={{ color: 'var(--text-secondary)' }}>{label}</span>
        )}
      </div>
      {participants.length > 0 && (
        <div className="pl-5 space-y-0.5">
          {participants.map((p, i) => (
            <ParticipantItem key={i} participant={p} />
          ))}
        </div>
      )}
    </div>
  );
}

function buildReferenceLabel(ref: DeployReference, labels: Record<string, string>): string {
  switch (ref.type) {
    case 'work-item': {
      const key = ref.key ?? 'work-item';
      const title = ref.title ?? labels.workItemTitle;
      return title ? `${key} \u2014 ${title}` : key;
    }
    case 'pull-request': {
      const num = ref.key ? `#${ref.key}` : 'Pull Request';
      const title = ref.title ?? labels.prTitle;
      return title ? `${num} \u2014 ${title}` : num;
    }
    case 'repository': {
      if (ref.key) return ref.revision ? `${ref.key} @ ${ref.revision.slice(0, 8)}` : ref.key;
      if (ref.revision) return ref.revision.slice(0, 8);
      return 'repository';
    }
    case 'pipeline':
      return ref.key ?? ref.provider ?? 'pipeline';
    default:
      return ref.key ?? ref.type;
  }
}

function ParticipantItem({ participant }: { participant: DeployParticipant }) {
  return (
    <div className="flex items-center justify-between text-[13px]">
      <span style={{ color: 'var(--text-muted)' }}>{participant.role}</span>
      <span className="inline-flex items-center gap-1.5" style={{ color: 'var(--text-secondary)' }}>
        {participant.displayName ?? participant.email ?? '—'}
        <CopyEmailButton email={participant.email} />
      </span>
    </div>
  );
}
