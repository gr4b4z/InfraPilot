import { useEffect, useState } from 'react';
import { formatDistanceToNow } from 'date-fns';
import { Package, Loader2, Search, X, ExternalLink } from 'lucide-react';
import { api } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import type { BuildSummary } from '@/lib/types';

/**
 * The build registry — every published build, from any branch, newest first. This page answers
 * "what builds exist, and which branch produced them"; deploying one is the promotion surface's
 * job (plan: feature-branch-builds, Phase C), not this page's.
 */
export function BuildsPage() {
  useDocumentTitle(['Builds']);

  const [builds, setBuilds] = useState<BuildSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [product, setProduct] = useState('');
  const [service, setService] = useState('');
  const [branch, setBranch] = useState('');

  const hasFilter = Boolean(product.trim() || service.trim() || branch.trim());

  useEffect(() => {
    let cancelled = false;
    // Debounced so a keystroke burst costs one round trip, not one per letter.
    const timer = setTimeout(() => {
      api
        .listBuilds({
          product: product.trim() || undefined,
          service: service.trim() || undefined,
          branch: branch.trim() || undefined,
          limit: 100,
        })
        .then((r) => {
          if (cancelled) return;
          setBuilds(r.results);
          setLoading(false);
        })
        .catch(() => {
          if (cancelled) return;
          setBuilds([]);
          setLoading(false);
        });
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [product, service, branch]);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start gap-3">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            Builds
          </h1>
          <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
            Registered builds from all branches — newest first
          </p>
        </div>
      </div>

      {/* Exact-match product/service filters plus a substring branch filter — "MPT-1234" finds the
         feature branch without spelling out the full ref. */}
      <div className="flex flex-wrap items-center gap-2">
        <FilterInput value={product} onChange={setProduct} placeholder="Product" />
        <FilterInput value={service} onChange={setService} placeholder="Service" />
        <FilterInput value={branch} onChange={setBranch} placeholder="Branch contains…" wide />
      </div>

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
        </div>
      ) : builds.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-20 text-center">
          <Package size={40} style={{ color: 'var(--text-muted)' }} />
          <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>
            {hasFilter ? 'No builds match the current filter' : 'No builds registered yet'}
          </p>
        </div>
      ) : (
        <div
          className="rounded-xl border overflow-x-auto"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
        >
          <table className="w-full min-w-max text-[13px]">
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border-color)' }}>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Product</th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Service</th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Version</th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Branch</th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Commit</th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Registered</th>
              </tr>
            </thead>
            <tbody>
              {builds.map((build) => (
                <tr key={build.id} style={{ borderBottom: '1px solid var(--border-color)' }}>
                  <td className="px-4 py-2.5 font-medium" style={{ color: 'var(--text-primary)' }}>
                    {build.product}
                  </td>
                  <td className="px-4 py-2.5" style={{ color: 'var(--text-primary)' }}>{build.service}</td>
                  <td className="px-4 py-2.5">
                    {build.buildUrl ? (
                      <a
                        href={build.buildUrl}
                        target="_blank"
                        rel="noreferrer"
                        className="inline-flex items-center gap-1 font-mono text-[12px] hover:underline"
                        style={{ color: 'var(--accent)' }}
                        title="Open the CI run"
                      >
                        {build.version}
                        <ExternalLink size={11} />
                      </a>
                    ) : (
                      <span className="font-mono text-[12px]" style={{ color: 'var(--text-primary)' }}>
                        {build.version}
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-2.5">
                    <BranchBadge branch={build.branch} />
                  </td>
                  <td className="px-4 py-2.5 font-mono text-[12px]" style={{ color: 'var(--text-muted)' }}>
                    {build.commitSha ? build.commitSha.slice(0, 8) : '—'}
                  </td>
                  <td className="px-4 py-2.5 whitespace-nowrap" style={{ color: 'var(--text-muted)' }}>
                    {formatDistanceToNow(new Date(build.createdAt), { addSuffix: true })}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function FilterInput({
  value,
  onChange,
  placeholder,
  wide,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
  wide?: boolean;
}) {
  return (
    <div className={`relative ${wide ? 'w-64' : 'w-44'}`}>
      <Search
        size={13}
        className="absolute left-2.5 top-1/2 -translate-y-1/2 pointer-events-none"
        style={{ color: 'var(--text-muted)' }}
      />
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        aria-label={placeholder}
        className="w-full rounded-lg border pl-7 pr-7 py-1.5 text-[13px]"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          color: 'var(--text-primary)',
        }}
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange('')}
          aria-label={`Clear ${placeholder}`}
          className="absolute right-2 top-1/2 -translate-y-1/2 transition-opacity hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          <X size={13} />
        </button>
      )}
    </div>
  );
}

/**
 * The branch, prominent by design — the registry exists so a feature build is never mistaken for
 * main. Trunk and release refs read as the stable spine (accent); anything else is a feature
 * branch and stays visually distinct (warning).
 */
function BranchBadge({ branch }: { branch: string }) {
  const short = branch.replace(/^refs\/heads\//, '');
  const isTrunk = short === 'main' || short === 'master' || short.startsWith('release/');
  return (
    <span
      className="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium max-w-72 truncate"
      style={{
        backgroundColor: isTrunk ? 'var(--accent-bg)' : 'var(--warning-bg, rgba(217,119,6,0.12))',
        color: isTrunk ? 'var(--accent)' : 'var(--warning)',
      }}
      title={branch}
    >
      {short}
    </span>
  );
}
