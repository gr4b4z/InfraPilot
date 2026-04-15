import { useEffect, useState, useCallback, useMemo } from 'react';
import { useParams, Link, useSearchParams } from 'react-router-dom';
import { useDeploymentStore } from '@/stores/deploymentStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { CopyEmailButton } from '@/components/deployments/CopyEmailButton';
import { format, formatDistanceToNow } from 'date-fns';
import { ArrowLeft, Loader2, ExternalLink, ChevronDown, ChevronRight, Download, Filter, Undo2, GitBranch, GitPullRequest, Ticket, Workflow } from 'lucide-react';
import type { DeployEvent, DeployReference } from '@/lib/types';

const REF_ICONS: Record<string, typeof ExternalLink> = {
  'work-item': Ticket,
  'pull-request': GitPullRequest,
  repository: GitBranch,
  pipeline: Workflow,
};

function referenceLabel(ref: DeployReference): string {
  switch (ref.type) {
    case 'work-item':
      return ref.key ?? 'Work Item';
    case 'pull-request':
      return 'Pull Request';
    case 'repository':
      if (ref.key) return ref.key;
      if (ref.url) {
        // Parse "owner/repo" from a URL like https://github.com/owner/repo or .../repo.git
        const m = ref.url.match(/[:/]([^/:]+\/[^/]+?)(?:\.git)?(?:\/|$|\?|#)/);
        if (m) return m[1];
      }
      if (ref.revision) return ref.revision.slice(0, 8);
      return 'Repository';
    case 'pipeline':
      return ref.key ?? ref.provider ?? 'Pipeline';
    default:
      return ref.key ?? ref.type;
  }
}

function referenceTooltip(ref: DeployReference, labels: Record<string, string>): string {
  switch (ref.type) {
    case 'work-item':
      return labels.workItemTitle ?? ref.type;
    case 'pull-request':
      return labels.prTitle ?? ref.type;
    default:
      return ref.type;
  }
}

const PAGE_SIZE = 20;
const MAX_HISTORY_FETCH = 500;

export function DeploymentHistoryPage() {
  const { product, service } = useParams<{ product: string; service: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const environment = searchParams.get('environment') ?? undefined;
  const { history: allHistory, loading, fetchHistory } = useDeploymentStore();
  const { getDisplayName } = useSettingsStore();
  const [expanded, setExpanded] = useState<string | null>(null);
  const [displayCount, setDisplayCount] = useState(PAGE_SIZE);

  // Always fetch full history (no environment filter) so the env list stays stable
  useEffect(() => {
    if (product && service) fetchHistory(product, service, undefined, MAX_HISTORY_FETCH);
  }, [product, service, fetchHistory]);

  // Derive unique environments from the full set
  const environments = useMemo(() => {
    const envSet = new Set(allHistory.map((e) => e.environment));
    return Array.from(envSet).sort();
  }, [allHistory]);

  // Client-side filter
  const history = useMemo(
    () => environment ? allHistory.filter((e) => e.environment === environment) : allHistory,
    [allHistory, environment],
  );

  // Reset pagination when the environment filter changes
  useEffect(() => {
    setDisplayCount(PAGE_SIZE);
  }, [environment]);

  const visibleHistory = useMemo(() => history.slice(0, displayCount), [history, displayCount]);
  const hasMore = displayCount < history.length;

  const setEnvironmentFilter = useCallback((env: string | undefined) => {
    if (env) {
      setSearchParams({ environment: env });
    } else {
      setSearchParams({});
    }
  }, [setSearchParams]);

  const downloadFile = useCallback((content: string, filename: string, mime: string) => {
    const blob = new Blob([content], { type: mime });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
  }, []);

  const exportJson = useCallback(() => {
    const data = history.map(flattenEvent);
    downloadFile(JSON.stringify(data, null, 2), `${product}-${service}-history.json`, 'application/json');
  }, [history, product, service, downloadFile]);

  const exportCsv = useCallback(() => {
    const rows = history.map(flattenEvent);
    if (rows.length === 0) return;
    const headers = Object.keys(rows[0]);
    const lines = [
      headers.join(','),
      ...rows.map((r) => headers.map((h) => csvCell(String(r[h as keyof typeof r] ?? ''))).join(',')),
    ];
    downloadFile(lines.join('\n'), `${product}-${service}-history.csv`, 'text/csv');
  }, [history, product, service, downloadFile]);

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <Link
          to={`/deployments/${product}`}
          className="p-1.5 rounded-lg transition-colors hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          <ArrowLeft size={18} />
        </Link>
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            {service}
          </h1>
          <p className="text-sm mt-0.5" style={{ color: 'var(--text-muted)' }}>
            Deployment history for {product}/{service}
            {environment && <span> — {getDisplayName(environment)}</span>}
          </p>
        </div>
        <div className="flex items-center gap-2 ml-auto">
          <EnvironmentFilter
            environments={environments}
            selected={environment}
            displayName={getDisplayName}
            onChange={setEnvironmentFilter}
          />
          <ExportMenu onCSV={exportCsv} onJSON={exportJson} disabled={history.length === 0} />
        </div>
      </div>

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
        </div>
      ) : history.length === 0 ? (
        <div className="text-center py-20 text-sm" style={{ color: 'var(--text-muted)' }}>
          No deployment history found
        </div>
      ) : (
        <div className="space-y-2">
          {visibleHistory.map((evt) => (
            <HistoryRow
              key={evt.id}
              event={evt}
              isExpanded={expanded === evt.id}
              onToggle={() => setExpanded(expanded === evt.id ? null : evt.id)}
            />
          ))}
          {hasMore && (
            <div className="flex flex-col items-center gap-1 pt-3">
              <button
                onClick={() => setDisplayCount((n) => n + PAGE_SIZE)}
                className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg transition-colors hover:opacity-80"
                style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
              >
                Load more
                <ChevronDown size={14} />
              </button>
              <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                Showing {visibleHistory.length} of {history.length}
              </span>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function HistoryRow({ event: evt, isExpanded, onToggle }: { event: DeployEvent; isExpanded: boolean; onToggle: () => void; }) {
  const { getDisplayName } = useSettingsStore();
  const workItem = evt.references.find((r) => r.type === 'work-item');
  const prAuthor = evt.participants.find((p) => p.role === 'PR Author');
  const labels = evt.enrichment?.labels ?? {};

  return (
    <div
      className="rounded-lg border p-3"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div
        className="flex items-center gap-3 cursor-pointer"
        onClick={onToggle}
      >
        <span style={{ color: 'var(--text-muted)' }}>
          {isExpanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
        </span>

        <span className="font-mono text-[13px] font-medium min-w-[80px]" style={{ color: statusColor(evt.status) }}>
          v{evt.version}
        </span>

        <RollbackIndicator isRollback={evt.isRollback} previousVersion={evt.previousVersion} />

        <StatusBadge status={evt.status} />

        <span
          className="badge text-[11px]"
          style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
        >
          {getDisplayName(evt.environment)}
        </span>

        {workItem?.key && (
          <span className="text-[12px]" style={{ color: 'var(--text-secondary)' }}>
            {workItem.key}
            {labels.workItemTitle && ` — ${labels.workItemTitle}`}
          </span>
        )}

        <span className="flex-1" />

        {prAuthor?.displayName && (
          <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {prAuthor.displayName}
          </span>
        )}

        <span className="text-[12px] whitespace-nowrap" style={{ color: 'var(--text-muted)' }}>
          {formatDistanceToNow(new Date(evt.deployedAt), { addSuffix: true })}
        </span>
      </div>

      {isExpanded && (
        <div className="mt-3 pl-7 space-y-2 text-[13px]">
          <div className="flex gap-6">
            <div>
              <span style={{ color: 'var(--text-muted)' }}>Source: </span>
              <span style={{ color: 'var(--text-secondary)' }}>{evt.source}</span>
            </div>
            <div>
              <span style={{ color: 'var(--text-muted)' }}>Deployed: </span>
              <span style={{ color: 'var(--text-secondary)' }}>
                {format(new Date(evt.deployedAt), 'MMM d, yyyy HH:mm')}
              </span>
            </div>
            {evt.previousVersion && (
              <div>
                <span style={{ color: 'var(--text-muted)' }}>Previous: </span>
                <span className="font-mono" style={{ color: 'var(--text-secondary)' }}>v{evt.previousVersion}</span>
              </div>
            )}
          </div>

          {evt.references.length > 0 && (
            <div className="flex flex-wrap gap-3">
              {evt.references.map((ref, i) => {
                const Icon = REF_ICONS[ref.type] ?? ExternalLink;
                const label = referenceLabel(ref);
                const tooltip = referenceTooltip(ref, labels);
                return ref.url ? (
                  <a
                    key={i}
                    href={ref.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    title={tooltip}
                    className="inline-flex items-center gap-1 hover:underline"
                    style={{ color: 'var(--accent)' }}
                  >
                    <Icon size={11} />
                    {label}
                  </a>
                ) : (
                  <span
                    key={i}
                    title={tooltip}
                    className="inline-flex items-center gap-1"
                    style={{ color: 'var(--text-secondary)' }}
                  >
                    <Icon size={11} />
                    {label}
                  </span>
                );
              })}
            </div>
          )}

          {[...evt.participants, ...(evt.enrichment?.participants ?? [])].length > 0 && (
            <div className="flex flex-wrap gap-x-4 gap-y-1">
              {[...evt.participants, ...(evt.enrichment?.participants ?? [])].map((p, i) => (
                <span key={i} className="inline-flex items-center gap-1" style={{ color: 'var(--text-muted)' }}>
                  {p.role}: <span style={{ color: 'var(--text-secondary)' }}>{p.displayName ?? p.email}</span>
                  <CopyEmailButton email={p.email} size={11} />
                </span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ── Status helpers ────────────────────────────────────────────────

const STATUS_STYLES: Record<string, { bg: string; fg: string; label: string }> = {
  succeeded: { bg: 'rgba(34,197,94,0.12)', fg: '#22c55e', label: 'Succeeded' },
  failed: { bg: 'rgba(239,68,68,0.12)', fg: '#ef4444', label: 'Failed' },
  in_progress: { bg: 'rgba(234,179,8,0.12)', fg: '#eab308', label: 'In Progress' },
};

function RollbackIndicator({ isRollback, previousVersion }: { isRollback?: boolean; previousVersion?: string | null }) {
  if (!isRollback) return null;
  const title = previousVersion ? `Rolled back from v${previousVersion}` : 'Rollback';
  return (
    <span
      title={title}
      aria-label={title}
      className="inline-flex"
      style={{ color: 'var(--text-muted)' }}
    >
      <Undo2 size={12} />
    </span>
  );
}

function StatusBadge({ status }: { status?: string }) {
  const s = STATUS_STYLES[status ?? 'succeeded'] ?? STATUS_STYLES.succeeded;
  return (
    <span
      className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-semibold uppercase tracking-wide leading-none"
      style={{ backgroundColor: s.bg, color: s.fg }}
    >
      <span
        className="inline-block w-1.5 h-1.5 rounded-full"
        style={{ backgroundColor: s.fg }}
      />
      {s.label}
    </span>
  );
}

function statusColor(status?: string): string {
  if (status === 'failed') return '#ef4444';
  if (status === 'in_progress') return '#eab308';
  return 'var(--text-primary)';
}

// ── Sub-components ────────────────────────────────────────────────

function ExportMenu({ onCSV, onJSON, disabled }: { onCSV: () => void; onJSON: () => void; disabled: boolean }) {
  const [open, setOpen] = useState(false);

  return (
    <div className="relative">
      <button
        onClick={() => setOpen(!open)}
        disabled={disabled}
        className="inline-flex items-center gap-1.5 text-[12px] font-medium px-2.5 py-1.5 rounded-lg transition-colors hover:opacity-80 disabled:opacity-40"
        style={{ color: 'var(--text-muted)', border: '1px solid var(--border-color)' }}
      >
        <Download size={13} />
        Export
      </button>
      {open && (
        <>
          <div className="fixed inset-0 z-10" onClick={() => setOpen(false)} />
          <div
            className="absolute right-0 top-full mt-1 z-20 rounded-lg border shadow-lg py-1 min-w-[120px]"
            style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
          >
            <button
              onClick={() => { onCSV(); setOpen(false); }}
              className="w-full text-left px-3 py-1.5 text-[12px] transition-colors hover:bg-[var(--bg-secondary)]"
              style={{ color: 'var(--text-primary)' }}
            >
              Export CSV
            </button>
            <button
              onClick={() => { onJSON(); setOpen(false); }}
              className="w-full text-left px-3 py-1.5 text-[12px] transition-colors hover:bg-[var(--bg-secondary)]"
              style={{ color: 'var(--text-primary)' }}
            >
              Export JSON
            </button>
          </div>
        </>
      )}
    </div>
  );
}

function EnvironmentFilter({
  environments,
  selected,
  displayName,
  onChange,
}: {
  environments: string[];
  selected: string | undefined;
  displayName: (key: string) => string;
  onChange: (env: string | undefined) => void;
}) {
  const [open, setOpen] = useState(false);

  if (environments.length <= 1) return null;

  return (
    <div className="relative">
      <button
        onClick={() => setOpen(!open)}
        className="inline-flex items-center gap-1.5 text-[12px] font-medium px-2.5 py-1.5 rounded-lg transition-colors hover:opacity-80"
        style={{ color: selected ? 'var(--accent)' : 'var(--text-muted)', border: '1px solid var(--border-color)' }}
      >
        <Filter size={13} />
        {selected ? displayName(selected) : 'All environments'}
        <ChevronDown size={12} />
      </button>
      {open && (
        <>
          <div className="fixed inset-0 z-10" onClick={() => setOpen(false)} />
          <div
            className="absolute right-0 top-full mt-1 z-20 rounded-lg border shadow-lg py-1 min-w-[160px]"
            style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
          >
            <button
              onClick={() => { onChange(undefined); setOpen(false); }}
              className="w-full text-left px-3 py-1.5 text-[12px] transition-colors hover:bg-[var(--bg-secondary)]"
              style={{ color: !selected ? 'var(--accent)' : 'var(--text-primary)', fontWeight: !selected ? 600 : 400 }}
            >
              All environments
            </button>
            {environments.map((env) => (
              <button
                key={env}
                onClick={() => { onChange(env); setOpen(false); }}
                className="w-full text-left px-3 py-1.5 text-[12px] transition-colors hover:bg-[var(--bg-secondary)]"
                style={{ color: selected === env ? 'var(--accent)' : 'var(--text-primary)', fontWeight: selected === env ? 600 : 400 }}
              >
                {displayName(env)}
              </button>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

// ── Export helpers ─────────────────────────────────────────────────

function csvCell(value: string): string {
  if (value.includes(',') || value.includes('"') || value.includes('\n')) {
    return `"${value.replace(/"/g, '""')}"`;
  }
  return value;
}

function flattenEvent(evt: DeployEvent): Record<string, string> {
  const workItems = evt.references
    .filter((r) => r.type === 'work-item')
    .map((r) => r.key ?? r.url ?? '')
    .join('; ');
  const prs = evt.references
    .filter((r) => r.type === 'pull-request')
    .map((r) => r.url ?? r.key ?? '')
    .join('; ');
  const allParticipants = [...evt.participants, ...(evt.enrichment?.participants ?? [])];
  const participants = allParticipants
    .map((p) => `${p.role}: ${p.displayName ?? p.email ?? ''}`)
    .join('; ');

  return {
    id: evt.id,
    product: evt.product,
    service: evt.service,
    environment: evt.environment,
    version: evt.version,
    previousVersion: evt.previousVersion ?? '',
    isRollback: evt.isRollback ? 'true' : '',
    status: evt.status ?? 'succeeded',
    source: evt.source,
    deployedAt: evt.deployedAt,
    workItems,
    pullRequests: prs,
    participants,
  };
}
