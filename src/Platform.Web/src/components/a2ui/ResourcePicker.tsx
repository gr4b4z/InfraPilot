import { useState, useRef, useEffect, useCallback } from 'react';
import { Search, Loader2 } from 'lucide-react';
import { api } from '@/lib/api';
import { formatDistanceToNow } from 'date-fns';
import type { ComponentProps } from './A2UIRenderer';

/**
 * Fallback options shown when no `source` is configured on the component.
 * Once real source integrations are wired up these become irrelevant.
 */
const MOCK_RESOURCES = [
  { id: 'vm-web-01', label: 'vm-web-01 (Web Server)' },
  { id: 'vm-api-01', label: 'vm-api-01 (API Server)' },
  { id: 'db-primary', label: 'db-primary (PostgreSQL)' },
  { id: 'redis-cache', label: 'redis-cache (Cache Cluster)' },
  { id: 'lb-public', label: 'lb-public (Load Balancer)' },
  { id: 'k8s-cluster', label: 'k8s-cluster (Kubernetes)' },
];

interface ResourceOption {
  id: string;
  label: string;
}

/**
 * Resolves options for a configured `source`. Currently supports:
 *
 * - `deployments/versions` — calls `GET /api/deployments/versions` with the
 *   product, environment, and optionally service fields read from sibling form
 *   values.
 *
 * Unknown sources fall through to the static mock list.
 */
async function fetchSourceOptions(
  source: string,
  allValues: Record<string, unknown>,
): Promise<ResourceOption[]> {
  if (source === 'deployments/versions') {
    const product = (allValues['product'] as string) || '';
    const environment = (allValues['environment'] as string) || '';
    const service = (allValues['service'] as string) || undefined;

    if (!product || !environment) return [];

    const { versions } = await api.getDeploymentVersions({
      product,
      environment,
      service,
      limit: 50,
    });

    return versions.map((v) => ({
      id: v.version,
      label: `${v.version} — ${v.service} — deployed ${formatDistanceToNow(new Date(v.deployedAt), { addSuffix: true })}${v.deployerEmail ? ` by ${v.deployerEmail}` : ''}`,
    }));
  }

  // Unsupported sources fall back to mock data.
  return MOCK_RESOURCES;
}

export function ResourcePicker({ component, value, error, onChange, readOnly, allValues }: ComponentProps) {
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const [options, setOptions] = useState<ResourceOption[]>(MOCK_RESOURCES);
  const [loading, setLoading] = useState(false);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const source = component.source;

  // Re-fetch options when sibling values change (for dynamic sources).
  const product = allValues?.['product'] as string | undefined;
  const environment = allValues?.['environment'] as string | undefined;
  const service = allValues?.['service'] as string | undefined;

  const loadOptions = useCallback(async () => {
    if (!source) {
      setOptions(MOCK_RESOURCES);
      return;
    }
    setLoading(true);
    try {
      const opts = await fetchSourceOptions(source, allValues ?? {});
      setOptions(opts);
    } catch {
      setOptions([]);
    } finally {
      setLoading(false);
    }
  }, [source, product, environment, service]);

  useEffect(() => {
    loadOptions();
  }, [loadOptions]);

  const filtered = options.filter((r) =>
    r.label.toLowerCase().includes(query.toLowerCase()),
  );

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (wrapperRef.current && !wrapperRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

  function select(id: string) {
    onChange(id);
    setQuery('');
    setOpen(false);
  }

  // Find the label for the currently selected value so the input shows it.
  const selectedLabel = options.find((r) => r.id === value)?.label ?? (value as string) ?? '';

  return (
    <div ref={wrapperRef}>
      <label className="block text-sm font-medium mb-1.5" style={{ color: 'var(--text-primary)' }}>
        {component.label}
        {component.required && <span style={{ color: 'var(--accent)' }}> *</span>}
      </label>
      <div className="relative">
        {loading ? (
          <Loader2
            className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 animate-spin"
            style={{ color: 'var(--text-muted)' }}
          />
        ) : (
          <Search
            className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4"
            style={{ color: 'var(--text-muted)' }}
          />
        )}
        <input
          type="text"
          value={open ? query : selectedLabel}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpen(true);
          }}
          onFocus={() => setOpen(true)}
          placeholder={component.placeholder || 'Search resources...'}
          disabled={readOnly}
          className={`w-full pl-9 pr-3 py-2 text-sm rounded-lg border outline-none transition-colors ${
            error ? 'border-red-500' : 'focus:border-[var(--accent)]'
          }`}
          style={{
            backgroundColor: 'var(--bg-secondary)',
            borderColor: error ? undefined : 'var(--border-color)',
            color: 'var(--text-primary)',
          }}
        />
      </div>
      {open && !readOnly && (
        <div
          className="mt-1 rounded-lg border shadow-lg max-h-48 overflow-y-auto"
          style={{
            backgroundColor: 'var(--bg-secondary)',
            borderColor: 'var(--border-color)',
          }}
        >
          {loading ? (
            <div className="px-3 py-2 text-sm flex items-center gap-2" style={{ color: 'var(--text-muted)' }}>
              <Loader2 size={14} className="animate-spin" />
              Loading…
            </div>
          ) : filtered.length === 0 ? (
            <div className="px-3 py-2 text-sm" style={{ color: 'var(--text-muted)' }}>
              {source && (!product || !environment)
                ? 'Fill in product and environment first'
                : 'No resources found'}
            </div>
          ) : (
            filtered.map((r) => (
              <button
                key={r.id}
                type="button"
                onClick={() => select(r.id)}
                className="w-full text-left px-3 py-2 text-sm hover:opacity-80 transition-opacity"
                style={{
                  color: r.id === value ? 'var(--accent)' : 'var(--text-primary)',
                  backgroundColor: r.id === value ? 'var(--bg-tertiary)' : undefined,
                }}
              >
                {r.label}
              </button>
            ))
          )}
        </div>
      )}
      {error && <p className="mt-1 text-xs text-red-500">{error}</p>}
    </div>
  );
}
