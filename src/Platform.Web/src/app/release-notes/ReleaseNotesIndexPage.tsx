import { useEffect } from 'react';
import { Link } from 'react-router-dom';
import { ScrollText, Loader2 } from 'lucide-react';
import { useDeploymentStore } from '@/stores/deploymentStore';

export function ReleaseNotesIndexPage() {
  const { products, loading, fetchProducts } = useDeploymentStore();
  useEffect(() => { fetchProducts(); }, [fetchProducts]);

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Release Notes
        </h1>
        <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
          Pick a product to view its release notes.
        </p>
      </div>

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
        </div>
      ) : products.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-20 text-center">
          <ScrollText size={40} style={{ color: 'var(--text-muted)' }} />
          <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>No products with deployments yet</p>
        </div>
      ) : (
        <div className="grid grid-cols-2 lg:grid-cols-3 gap-3">
          {products.map((p) => (
            <Link
              key={p.product}
              to={`/release-notes/${p.product}`}
              className="rounded-xl border p-4 transition-colors hover:opacity-80"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
            >
              <div className="font-semibold text-[14px]" style={{ color: 'var(--text-primary)' }}>{p.product}</div>
              <div className="text-[12px] mt-1" style={{ color: 'var(--text-muted)' }}>
                {Object.keys(p.environments).length} environment(s)
              </div>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}
