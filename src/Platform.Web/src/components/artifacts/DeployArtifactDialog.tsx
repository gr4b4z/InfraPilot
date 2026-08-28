import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { formatDistanceToNow } from 'date-fns';
import { Loader2, Package, Rocket, Search, X } from 'lucide-react';
import { api } from '@/lib/api';
import { Dialog } from '@/components/ui/Dialog';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { BranchBadge } from './BranchBadge';
import type { BuildSummary, BuildTarget } from '@/lib/types';

/**
 * "Deploy an artifact": pick any registered artifact of this service — main or feature branch — and
 * a target environment with a `build → *` policy, and open a promotion for it. The server assembles
 * the change set from the artifact's manifest; this dialog only chooses. On success it navigates to
 * the new promotion, which is where approval (if the edge has one) and progress live.
 */
export function DeployArtifactDialog({
  product,
  service,
  targets,
  onClose,
}: {
  product: string;
  service: string;
  targets: BuildTarget[];
  onClose: () => void;
}) {
  const navigate = useNavigate();
  const [artifacts, setArtifacts] = useState<BuildSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [branchFilter, setBranchFilter] = useState('');
  const [selectedArtifact, setSelectedArtifact] = useState<string | null>(null);
  const [targetEnv, setTargetEnv] = useState<string | null>(targets.length === 1 ? targets[0].targetEnv : null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const timer = setTimeout(() => {
      api
        .listBuilds({ product, service, branch: branchFilter.trim() || undefined, limit: 30 })
        .then((r) => {
          if (cancelled) return;
          setArtifacts(r.results);
          setLoading(false);
        })
        .catch(() => {
          if (cancelled) return;
          setArtifacts([]);
          setLoading(false);
        });
    }, 200);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [product, service, branchFilter]);

  const selectedTarget = useMemo(
    () => targets.find((t) => t.targetEnv === targetEnv) ?? null,
    [targets, targetEnv],
  );

  const submit = async () => {
    if (!selectedArtifact || !targetEnv || submitting) return;
    setSubmitting(true);
    setError(null);
    try {
      const created = await api.createPromotionFromBuild(selectedArtifact, targetEnv);
      onClose();
      navigate(`/promotions/${created.id}`);
    } catch (e) {
      // The API's 422s carry a human-readable `error` (already-at-version, edge misconfigured…).
      setError(e instanceof Error ? e.message : 'Failed to create the promotion');
      setSubmitting(false);
    }
  };

  return (
    <Dialog onClose={onClose} ariaLabel={`Deploy an artifact of ${service}`} width={640}>
      <div className="p-4 space-y-4">
        <div className="flex items-start gap-2">
          <div>
            <h2 className="text-base font-semibold" style={{ color: 'var(--text-primary)' }}>
              Deploy an artifact
            </h2>
            <p className="text-[12px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
              Any registered artifact of {service} — the branch badge says what you're shipping
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="ml-auto p-1 rounded transition-opacity hover:opacity-80"
            style={{ color: 'var(--text-muted)' }}
          >
            <X size={16} />
          </button>
        </div>

        {/* Artifact picker — newest first, filterable by branch. */}
        <div className="relative">
          <Search
            size={13}
            className="absolute left-2.5 top-1/2 -translate-y-1/2 pointer-events-none"
            style={{ color: 'var(--text-muted)' }}
          />
          <input
            type="text"
            value={branchFilter}
            onChange={(e) => setBranchFilter(e.target.value)}
            placeholder="Filter by branch — e.g. MPT-1234"
            aria-label="Filter artifacts by branch"
            className="w-full rounded-lg border pl-7 pr-3 py-1.5 text-[13px]"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-primary)',
              color: 'var(--text-primary)',
            }}
          />
        </div>

        <div
          className="rounded-lg border max-h-64 overflow-y-auto divide-y"
          style={{ borderColor: 'var(--border-color)' }}
          role="radiogroup"
          aria-label="Registered artifacts"
        >
          {loading ? (
            <div className="flex items-center justify-center py-10">
              <Loader2 className="animate-spin" size={18} style={{ color: 'var(--text-muted)' }} />
            </div>
          ) : artifacts.length === 0 ? (
            <div className="flex flex-col items-center py-8 text-center">
              <Package size={24} style={{ color: 'var(--text-muted)' }} />
              <p className="mt-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
                {branchFilter
                  ? 'No artifacts match this branch filter'
                  : 'No registered artifacts for this service yet'}
              </p>
            </div>
          ) : (
            artifacts.map((artifact) => {
              const selected = selectedArtifact === artifact.id;
              return (
                <button
                  key={artifact.id}
                  type="button"
                  role="radio"
                  aria-checked={selected}
                  onClick={() => setSelectedArtifact(artifact.id)}
                  className="w-full flex items-center gap-2.5 px-3 py-2 text-left transition-colors"
                  style={{
                    borderColor: 'var(--border-color)',
                    backgroundColor: selected ? 'var(--accent-bg)' : 'transparent',
                  }}
                >
                  <span
                    className="inline-block w-3 h-3 rounded-full border flex-shrink-0"
                    style={{
                      borderColor: selected ? 'var(--accent)' : 'var(--border-color)',
                      backgroundColor: selected ? 'var(--accent)' : 'transparent',
                    }}
                  />
                  <span className="font-mono text-[12px]" style={{ color: 'var(--text-primary)' }}>
                    {artifact.version}
                  </span>
                  <BranchBadge branch={artifact.branch} />
                  <span className="flex-1" />
                  <span className="text-[11px] whitespace-nowrap" style={{ color: 'var(--text-muted)' }}>
                    {formatDistanceToNow(new Date(artifact.createdAt), { addSuffix: true })}
                  </span>
                </button>
              );
            })
          )}
        </div>

        {/* Target env — only edges with a build → * policy are offered. */}
        <div>
          <p className="text-[12px] font-medium mb-1.5" style={{ color: 'var(--text-secondary)' }}>
            Deploy to
          </p>
          <div className="flex flex-wrap items-center gap-1.5" role="radiogroup" aria-label="Target environment">
            {targets.map((t) => {
              const selected = targetEnv === t.targetEnv;
              return (
                <button
                  key={t.targetEnv}
                  type="button"
                  role="radio"
                  aria-checked={selected}
                  onClick={() => setTargetEnv(t.targetEnv)}
                  className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full border text-[12px] font-medium transition-colors"
                  style={{
                    borderColor: selected ? 'var(--accent)' : 'var(--border-color)',
                    backgroundColor: selected ? 'var(--accent-bg)' : 'transparent',
                    color: selected ? 'var(--accent)' : 'var(--text-muted)',
                  }}
                >
                  <EnvBadge env={t.targetEnv} size="xs" />
                  {t.autoApprove ? 'deploys immediately' : 'needs approval'}
                </button>
              );
            })}
          </div>
        </div>

        {error && (
          <p className="text-[12px]" role="alert" style={{ color: 'var(--error, #dc2626)' }}>
            {error}
          </p>
        )}

        <div className="flex items-center justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
            style={{ color: 'var(--text-muted)', border: '1px solid var(--border-color)' }}
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={submit}
            disabled={!selectedArtifact || !targetEnv || submitting}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)', color: '#fff' }}
          >
            {submitting ? <Loader2 className="animate-spin" size={13} /> : <Rocket size={13} />}
            {selectedTarget?.autoApprove ? 'Deploy' : 'Request deployment'}
          </button>
        </div>
      </div>
    </Dialog>
  );
}
