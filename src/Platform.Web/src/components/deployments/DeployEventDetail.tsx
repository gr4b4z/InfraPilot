import { useState } from 'react';
import { Link } from 'react-router-dom';
import { format } from 'date-fns';
import { X, ExternalLink, GitBranch, GitPullRequest, Ticket, Workflow, Users, Clock, Undo2, PlusCircle } from 'lucide-react';
import { useSettingsStore } from '@/stores/settingsStore';
import { useFeatureFlagsStore, FeatureFlag } from '@/stores/featureFlagsStore';
import { useAuthStore } from '@/stores/authStore';
import { api } from '@/lib/api';
import { CopyEmailButton } from './CopyEmailButton';
import type { DeploymentStateEntry, DeployReference, DeployParticipant } from '@/lib/types';
import { resolveReferenceHref } from '@/lib/refUrl';

interface Props {
  entry: DeploymentStateEntry;
  product: string;
  onClose: () => void;
  /** Called after a manual deployment is created so the parent can refresh state. */
  onChanged?: () => void;
}

const REFERENCE_ICONS: Record<string, typeof ExternalLink> = {
  pipeline: Workflow,
  repository: GitBranch,
  'pull-request': GitPullRequest,
  'work-item': Ticket,
};

export function DeployEventDetail({ entry, product, onClose, onChanged }: Props) {
  const { getDisplayName } = useSettingsStore();
  const rollbacksEnabled = useFeatureFlagsStore((s) => s.isEnabled(FeatureFlag.Rollbacks));
  const isAdmin = useAuthStore((s) => s.user?.isAdmin ?? false);

  // Manual deployment entry (admin only): create a new deploy based on this one, changing
  // version/status. A note is required. Server stamps Source="manual" + triggered-by = the user.
  const [showManualForm, setShowManualForm] = useState(false);
  const [manualVersion, setManualVersion] = useState(entry.version);
  const [manualStatus, setManualStatus] = useState(entry.status ?? 'succeeded');
  const [manualNote, setManualNote] = useState('');
  const [manualSaving, setManualSaving] = useState(false);
  const [manualError, setManualError] = useState<string | null>(null);

  const submitManual = async () => {
    setManualSaving(true);
    setManualError(null);
    try {
      await api.createManualDeploy({
        product,
        service: entry.service,
        environment: entry.environment,
        version: manualVersion.trim(),
        status: manualStatus.trim() || undefined,
        note: manualNote.trim(),
      });
      onChanged?.();
      onClose();
    } catch (err) {
      setManualError(err instanceof Error ? err.message : 'Failed to create manual deployment');
    } finally {
      setManualSaving(false);
    }
  };

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

        {/* Actions */}
        <div className="flex items-center gap-4">
          {/* History link */}
          <Link
            to={`/deployments/${product}/${entry.service}/history`}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium transition-opacity hover:opacity-80"
            style={{ color: 'var(--accent)' }}
          >
            View History
            <ExternalLink size={12} />
          </Link>

          {/* Roll back — deep-links to the create-rollback flow prefilled with this
              service/product/env (manual mode). Gated by the rollbacks feature flag. */}
          {rollbacksEnabled && (
            <Link
              to={`/rollbacks?new=1&product=${encodeURIComponent(product)}&targetEnv=${encodeURIComponent(entry.environment)}&service=${encodeURIComponent(entry.service)}`}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-secondary)' }}
            >
              <Undo2 size={12} />
              Roll back
            </Link>
          )}

          {/* Manual deploy — admin only. Records a new deploy based on this one, attributed to
              the signed-in user (Source="manual"), not CI. */}
          {isAdmin && !showManualForm && (
            <button
              onClick={() => setShowManualForm(true)}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium transition-opacity hover:opacity-80"
              style={{ color: 'var(--accent)' }}
            >
              <PlusCircle size={12} />
              New manual deploy
            </button>
          )}
        </div>

        {/* Manual deploy form */}
        {isAdmin && showManualForm && (
          <div className="space-y-2 pt-3 border-t" style={{ borderColor: 'var(--border-color)' }}>
            <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              Records a <b>new</b> deployment based on this one, attributed to you (not CI). A note is required.
            </p>
            <label className="block text-[12px]" style={{ color: 'var(--text-muted)' }}>
              Version
              <input
                value={manualVersion}
                onChange={(e) => setManualVersion(e.target.value)}
                className="mt-1 w-full rounded-lg border px-3 py-1.5 text-[13px] font-mono"
                style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
              />
            </label>
            <label className="block text-[12px]" style={{ color: 'var(--text-muted)' }}>
              Status
              <input
                value={manualStatus}
                onChange={(e) => setManualStatus(e.target.value)}
                placeholder="succeeded"
                className="mt-1 w-full rounded-lg border px-3 py-1.5 text-[13px]"
                style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
              />
            </label>
            <label className="block text-[12px]" style={{ color: 'var(--text-muted)' }}>
              Note (required)
              <textarea
                value={manualNote}
                onChange={(e) => setManualNote(e.target.value)}
                rows={2}
                placeholder="Why are you recording this manually?"
                className="mt-1 w-full rounded-lg border px-3 py-1.5 text-[13px] resize-none"
                style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
              />
            </label>
            {manualError && (
              <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{manualError}</p>
            )}
            <div className="flex items-center gap-2">
              <button
                onClick={submitManual}
                disabled={manualSaving || manualVersion.trim().length === 0 || manualNote.trim().length === 0}
                title={manualNote.trim().length === 0 ? 'A note is required' : undefined}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity"
                style={{
                  backgroundColor: 'var(--accent)',
                  color: '#fff',
                  opacity: manualSaving || manualVersion.trim().length === 0 || manualNote.trim().length === 0 ? 0.5 : 1,
                  cursor: manualVersion.trim().length === 0 || manualNote.trim().length === 0 ? 'not-allowed' : 'pointer',
                }}
              >
                <PlusCircle size={13} />
                {manualSaving ? 'Creating…' : 'Create deployment'}
              </button>
              <button
                onClick={() => { setShowManualForm(false); setManualNote(''); setManualError(null); }}
                className="text-[13px] transition-opacity hover:opacity-80"
                style={{ color: 'var(--text-muted)' }}
              >
                Cancel
              </button>
            </div>
          </div>
        )}
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
