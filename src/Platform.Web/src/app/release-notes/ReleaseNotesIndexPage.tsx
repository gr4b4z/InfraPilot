import { useEffect } from 'react';
import { Link } from 'react-router-dom';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { useKeyboardListRow } from '@/hooks/keyboardList';
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
        // A grid, so left/right move across a row and up/down between rows — `columns` has to match
        // the widest breakpoint's column count for the arithmetic to line up with what is on screen.
        <KeyboardList
          className="grid grid-cols-2 lg:grid-cols-3 gap-3"
          count={products.length}
          columns={3}
          ariaLabel="Products with release notes"
        >
          {products.map((p, index) => (
            <ProductCard
              key={p.product}
              index={index}
              product={p.product}
              environments={Object.keys(p.environments).length}
            />
          ))}
        </KeyboardList>
      )}
    </div>
  );
}

/** One product tile. Already a link, so it activates itself; this only adds the arrow navigation. */
function ProductCard({
  index,
  product,
  environments,
}: {
  index: number;
  product: string;
  environments: number;
}) {
  const rowProps = useKeyboardListRow(index, () => {}, {
    role: null,
    selfActivating: true,
    label: `${product} — ${environments} environment(s)`,
  });
  return (
            <Link
              {...rowProps}
              to={`/release-notes/${product}`}
              className="rounded-xl border p-4 transition-colors hover:opacity-80"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
            >
              <div className="font-semibold text-[14px]" style={{ color: 'var(--text-primary)' }}>{product}</div>
              <div className="text-[12px] mt-1" style={{ color: 'var(--text-muted)' }}>
                {environments} environment(s)
              </div>
            </Link>
  );
}
