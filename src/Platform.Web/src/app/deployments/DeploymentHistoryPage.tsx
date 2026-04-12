import { useEffect, useState } from 'react';
import { useParams, Link, useSearchParams } from 'react-router-dom';
import { useDeploymentStore } from '@/stores/deploymentStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { format, formatDistanceToNow } from 'date-fns';
import { ArrowLeft, Loader2, ExternalLink, ChevronDown, ChevronRight } from 'lucide-react';
import type { DeployEvent } from '@/lib/types';

export function DeploymentHistoryPage() {
  const { product, service } = useParams<{ product: string; service: string }>();
  const [searchParams] = useSearchParams();
  const environment = searchParams.get('environment') ?? undefined;
  const { history, loading, fetchHistory } = useDeploymentStore();
  const { getDisplayName } = useSettingsStore();
  const [expanded, setExpanded] = useState<string | null>(null);

  const displayName = (key: string) => getDisplayName(key, product);

  useEffect(() => {
    if (product && service) fetchHistory(product, service, environment);
  }, [product, service, environment, fetchHistory]);

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
            {environment && <span> — {displayName(environment)}</span>}
          </p>
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
          {history.map((evt) => (
            <HistoryRow
              key={evt.id}
              event={evt}
              product={product}
              isExpanded={expanded === evt.id}
              onToggle={() => setExpanded(expanded === evt.id ? null : evt.id)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function HistoryRow({ event: evt, product, isExpanded, onToggle }: { event: DeployEvent; product?: string; isExpanded: boolean; onToggle: () => void; }) {
  const { getDisplayName: rawGetDisplayName } = useSettingsStore();
  const getDisplayName = (key: string) => rawGetDisplayName(key, product);
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

        <span className="font-mono text-[13px] font-medium min-w-[80px]" style={{ color: 'var(--text-primary)' }}>
          v{evt.version}
        </span>

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
              {evt.references.map((ref, i) => (
                ref.url ? (
                  <a
                    key={i}
                    href={ref.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-1 hover:underline"
                    style={{ color: 'var(--accent)' }}
                  >
                    <ExternalLink size={11} />
                    {ref.type === 'work-item' ? ref.key : ref.type}
                  </a>
                ) : null
              ))}
            </div>
          )}

          {[...evt.participants, ...(evt.enrichment?.participants ?? [])].length > 0 && (
            <div className="flex flex-wrap gap-x-4 gap-y-1">
              {[...evt.participants, ...(evt.enrichment?.participants ?? [])].map((p, i) => (
                <span key={i} style={{ color: 'var(--text-muted)' }}>
                  {p.role}: <span style={{ color: 'var(--text-secondary)' }}>{p.displayName ?? p.email}</span>
                </span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
