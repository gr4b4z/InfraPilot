import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { useDeploymentStore } from '@/stores/deploymentStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { formatDistanceToNow } from 'date-fns';
import { Rocket, Loader2, CheckCircle, AlertTriangle } from 'lucide-react';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import type { ProductSummary } from '@/lib/types';

export function DeploymentsPage() {
  const { products, loading, fetchProducts } = useDeploymentStore();
  const { getOrderedEnvironments } = useSettingsStore();
  const navigate = useNavigate();

  useEffect(() => {
    fetchProducts();
  }, [fetchProducts]);

  const allEnvs = getOrderedEnvironments(
    Array.from(new Set(products.flatMap((p) => Object.keys(p.environments))))
  );

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Deployments
        </h1>
        <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
          Product overview — current deployment state across environments
        </p>
      </div>

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
        </div>
      ) : products.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-20 text-center">
          <Rocket size={40} style={{ color: 'var(--text-muted)' }} />
          <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>No deployments recorded yet</p>
        </div>
      ) : (
        <div className="rounded-xl border overflow-x-auto" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
          <table className="w-full min-w-max text-[13px]">
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border-color)' }}>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Product</th>
                {/* Colour-coded so a column can be picked out at a glance in a wide table. */}
                {allEnvs.map((env) => (
                  <th key={env} className="text-center px-4 py-3 font-medium">
                    <EnvLabel env={env} />
                  </th>
                ))}
              </tr>
            </thead>
            <KeyboardList as="tbody" count={products.length} ariaLabel="Products">
              {products.map((product, index) => (
                  <ProductRow
                    key={product.product}
                    index={index}
                    product={product}
                    allEnvs={allEnvs}
                    onOpen={() => navigate(`/deployments/${product.product}`)}
                  />
              ))}
            </KeyboardList>
          </table>
        </div>
      )}
    </div>
  );
}

/**
 * One product's row in the overview. Focusable so the matrix can be walked with the arrow keys and
 * opened with Enter — the row is the only way into a product's deployments, and as a bare
 * `<tr onClick>` it had no keyboard route at all.
 */
function ProductRow({
  index,
  product,
  allEnvs,
  onOpen,
}: {
  index: number;
  product: ProductSummary;
  allEnvs: string[];
  onOpen: () => void;
}) {
  // `role: null` keeps the implicit row semantics — see useKeyboardListRow.
  const rowProps = useKeyboardListRow(index, onOpen, {
    role: null,
    label: `${product.product} — open deployments`,
  });

  return (
    <tr
      {...rowProps}
      className="cursor-pointer transition-colors hover:opacity-80"
      style={{ borderBottom: '1px solid var(--border-color)' }}
    >
      <td className="px-4 py-3 font-medium" style={{ color: 'var(--text-primary)' }}>
        {product.product}
      </td>
      {allEnvs.map((env) => {
        const summary = product.environments[env];
        if (!summary) {
          return (
            <td key={env} className="text-center px-4 py-3" style={{ color: 'var(--text-muted)' }}>
              —
            </td>
          );
        }
        const allDeployed = summary.deployedServices === summary.totalServices;
        return (
          <td key={env} className="text-center px-4 py-2">
            <div className="inline-flex flex-col items-center gap-0.5">
              <span className="inline-flex items-center gap-1" style={{ color: allDeployed ? 'var(--success)' : 'var(--warning)' }}>
                {allDeployed ? <CheckCircle size={13} /> : <AlertTriangle size={13} />}
                {summary.deployedServices}/{summary.totalServices}
              </span>
              {summary.lastDeployedAt && (
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  {formatDistanceToNow(new Date(summary.lastDeployedAt), { addSuffix: true })}
                </span>
              )}
            </div>
          </td>
        );
      })}
    </tr>
  );
}
