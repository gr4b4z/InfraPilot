import { useState, useEffect } from 'react';
import { useAuthStore } from '@/stores/authStore';
import { api, type FeatureFlag } from '@/lib/api';
import { formatDistanceToNow } from 'date-fns';

function formatFlagKey(key: string): string {
  const stripped = key.startsWith('features.') ? key.slice('features.'.length) : key;
  return stripped
    .split('.')
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join(' > ');
}

export function FeatureFlagSettings() {
  const isAdmin = useAuthStore((s) => s.user?.isAdmin) ?? false;
  const [flags, setFlags] = useState<FeatureFlag[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!isAdmin) return;
    let cancelled = false;
    (async () => {
      try {
        const result = await api.listFeatureFlags();
        if (!cancelled) {
          setFlags(result.flags);
          setError(null);
        }
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : 'Failed to load feature flags');
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [isAdmin]);

  if (!isAdmin) return null;

  const handleToggle = async (flag: FeatureFlag) => {
    const newEnabled = !flag.enabled;

    // Optimistic update
    setFlags((prev) =>
      prev.map((f) => (f.key === flag.key ? { ...f, enabled: newEnabled } : f))
    );
    setError(null);

    try {
      const updated = await api.setFeatureFlag(flag.key, newEnabled);
      setFlags((prev) =>
        prev.map((f) => (f.key === flag.key ? { ...f, enabled: updated.enabled } : f))
      );
    } catch (e) {
      // Revert on failure
      setFlags((prev) =>
        prev.map((f) => (f.key === flag.key ? { ...f, enabled: flag.enabled } : f))
      );
      setError(e instanceof Error ? e.message : 'Failed to toggle feature flag');
    }
  };

  return (
    <div
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Feature Flags
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Toggle platform features on or off. Changes take effect immediately.
        </p>
      </div>

      {loading && (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="flex items-center justify-between py-2">
              <div className="space-y-1.5">
                <div
                  className="h-4 w-32 rounded animate-pulse"
                  style={{ backgroundColor: 'var(--bg-tertiary, #e5e7eb)' }}
                />
                <div
                  className="h-3 w-48 rounded animate-pulse"
                  style={{ backgroundColor: 'var(--bg-tertiary, #e5e7eb)' }}
                />
              </div>
              <div
                className="h-6 w-11 rounded-full animate-pulse"
                style={{ backgroundColor: 'var(--bg-tertiary, #e5e7eb)' }}
              />
            </div>
          ))}
        </div>
      )}

      {!loading && flags.length === 0 && !error && (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          No feature flags configured.
        </p>
      )}

      {!loading && flags.length > 0 && (
        <div className="space-y-1">
          {flags.map((flag) => (
            <div
              key={flag.key}
              className="flex items-center justify-between rounded-lg px-2 py-2.5"
            >
              <div className="space-y-0.5">
                <div className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
                  {formatFlagKey(flag.key)}
                </div>
                <div className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  {flag.key}
                </div>
                <div className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  Last updated by {flag.updatedBy},{' '}
                  {formatDistanceToNow(new Date(flag.updatedAt), { addSuffix: true })}
                </div>
              </div>
              <button
                onClick={() => handleToggle(flag)}
                className="relative inline-flex h-6 w-11 items-center rounded-full transition-colors"
                style={{
                  backgroundColor: flag.enabled
                    ? 'var(--success)'
                    : 'var(--bg-tertiary, #d1d5db)',
                }}
              >
                <span
                  className="inline-block h-4 w-4 transform rounded-full bg-white transition-transform shadow-sm"
                  style={{
                    transform: flag.enabled ? 'translateX(24px)' : 'translateX(4px)',
                  }}
                />
              </button>
            </div>
          ))}
        </div>
      )}

      {error && (
        <div
          className="text-[13px] rounded-lg px-3 py-2"
          style={{ color: 'var(--danger, #dc2626)', backgroundColor: 'var(--danger-muted, #fee2e2)' }}
        >
          {error}
        </div>
      )}
    </div>
  );
}
