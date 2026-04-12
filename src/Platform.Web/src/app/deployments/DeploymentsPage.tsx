import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { useDeploymentStore } from '@/stores/deploymentStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { formatDistanceToNow } from 'date-fns';
import { Rocket, Loader2, CheckCircle, AlertTriangle } from 'lucide-react';

export function DeploymentsPage() {
  const { products, loading, fetchProducts } = useDeploymentStore();
  const { getDisplayName, getOrderedEnvironments } = useSettingsStore();
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
        <div className="rounded-xl border overflow-hidden" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
          <table className="w-full text-[13px]">
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border-color)' }}>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Product</th>
                {allEnvs.map((env) => (
                  <th key={env} className="text-center px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>
                    {getDisplayName(env)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {products.map((product) => (
                  <tr
                    key={product.product}
                    className="cursor-pointer transition-colors hover:opacity-80"
                    style={{ borderBottom: '1px solid var(--border-color)' }}
                    onClick={() => navigate(`/deployments/${product.product}`)}
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
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
