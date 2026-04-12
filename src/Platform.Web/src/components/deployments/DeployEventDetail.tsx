import { Link } from 'react-router-dom';
import { format } from 'date-fns';
import { X, ExternalLink, GitBranch, GitPullRequest, Ticket, Workflow, Users, Clock } from 'lucide-react';
import { useSettingsStore } from '@/stores/settingsStore';
import type { DeploymentStateEntry, DeployReference, DeployParticipant } from '@/lib/types';

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
  const settings = useSettingsStore();
  const getDisplayName = (key: string) => settings.getDisplayName(key, product);
  const allParticipants = [
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

        {/* Version info */}
        <div className="space-y-2">
          <div className="flex items-center justify-between text-[13px]">
            <span style={{ color: 'var(--text-muted)' }}>Version</span>
            <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>v{entry.version}</span>
          </div>
          {entry.previousVersion && (
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

        {/* References */}
        {entry.references.length > 0 && (
          <div className="space-y-2">
            <h3 className="text-[12px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
              References
            </h3>
            <div className="space-y-1.5">
              {entry.references.map((ref, i) => (
                <ReferenceItem key={i} reference={ref} labels={labels} />
              ))}
            </div>
          </div>
        )}

        {/* Participants */}
        {allParticipants.length > 0 && (
          <div className="space-y-2">
            <h3 className="text-[12px] font-medium uppercase tracking-wider flex items-center gap-1.5" style={{ color: 'var(--text-muted)' }}>
              <Users size={12} /> Participants
            </h3>
            <div className="space-y-1">
              {allParticipants.map((p, i) => (
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
  const label = reference.type === 'work-item'
    ? `${reference.key ?? reference.type}${labels.workItemTitle ? ` — ${labels.workItemTitle}` : ''}`
    : reference.type === 'repository' && reference.revision
      ? `${reference.revision.slice(0, 8)}`
      : reference.type === 'pull-request' && labels.prTitle
        ? labels.prTitle
        : reference.type;

  return (
    <div className="flex items-center gap-2 text-[13px]">
      <Icon size={13} style={{ color: 'var(--text-muted)' }} />
      {reference.url ? (
        <a
          href={reference.url}
          target="_blank"
          rel="noopener noreferrer"
          className="hover:underline truncate"
          style={{ color: 'var(--accent)' }}
        >
          {label}
        </a>
      ) : (
        <span style={{ color: 'var(--text-secondary)' }}>{label}</span>
      )}
    </div>
  );
}

function ParticipantItem({ participant }: { participant: DeployParticipant }) {
  return (
    <div className="flex items-center justify-between text-[13px]">
      <span style={{ color: 'var(--text-muted)' }}>{participant.role}</span>
      <span style={{ color: 'var(--text-secondary)' }}>{participant.displayName ?? participant.email ?? '—'}</span>
    </div>
  );
}
