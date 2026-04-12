import { useState, useRef, useEffect } from 'react';
import { Search } from 'lucide-react';
import type { ComponentProps } from './A2UIRenderer';

const MOCK_RESOURCES = [
  { id: 'vm-web-01', label: 'vm-web-01 (Web Server)' },
  { id: 'vm-api-01', label: 'vm-api-01 (API Server)' },
  { id: 'db-primary', label: 'db-primary (PostgreSQL)' },
  { id: 'redis-cache', label: 'redis-cache (Cache Cluster)' },
  { id: 'lb-public', label: 'lb-public (Load Balancer)' },
  { id: 'k8s-cluster', label: 'k8s-cluster (Kubernetes)' },
];

export function ResourcePicker({ component, value, error, onChange, readOnly }: ComponentProps) {
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const wrapperRef = useRef<HTMLDivElement>(null);

  const filtered = MOCK_RESOURCES.filter((r) =>
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

  return (
    <div ref={wrapperRef}>
      <label className="block text-sm font-medium mb-1.5" style={{ color: 'var(--text-primary)' }}>
        {component.label}
        {component.required && <span style={{ color: 'var(--accent)' }}> *</span>}
      </label>
      <div className="relative">
        <Search
          className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4"
          style={{ color: 'var(--text-muted)' }}
        />
        <input
          type="text"
          value={open ? query : ((value as string) || '')}
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
          {filtered.length === 0 ? (
            <div className="px-3 py-2 text-sm" style={{ color: 'var(--text-muted)' }}>
              No resources found
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
