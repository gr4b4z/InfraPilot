import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { Check, AlertTriangle, Loader2 } from 'lucide-react';
import { api } from '@/lib/api';
import { useFeatureFlag, FeatureFlag } from '@/stores/featureFlagsStore';

/**
 * Per-product rollback enrollment. A product can use rollbacks only when the global
 * `features.rollbacks` flag is on AND it is enrolled here. Promotion-enrolled products
 * (those with at least one promotion policy) are listed so operators can opt them in —
 * "products with promotions can additionally enable rollbacks".
 */
export function RollbackSettings() {
  const flagOn = useFeatureFlag(FeatureFlag.Rollbacks);

  const [promotionProducts, setPromotionProducts] = useState<string[]>([]);
  const [enabled, setEnabled] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    Promise.all([api.listPromotionPolicies(), api.getRollbackEnabledProducts()])
      .then(([policies, rollbacks]) => {
        if (cancelled) return;
        const promo = [...new Set(policies.policies.map((p) => p.product))].sort();
        setPromotionProducts(promo);
        setEnabled(new Set(rollbacks.products));
      })
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : String(e)))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
    };
  }, []);

  // Show promotion-enrolled products plus any already-enrolled rollback product that no longer
  // has a promotion policy (so toggling it off is still possible — nothing silently disappears).
  const rows = useMemo(() => {
    const all = new Set([...promotionProducts, ...enabled]);
    return [...all].sort().map((product) => ({
      product,
      hasPromotion: promotionProducts.includes(product),
    }));
  }, [promotionProducts, enabled]);

  const toggle = (product: string) =>
    setEnabled((prev) => {
      const next = new Set(prev);
      if (next.has(product)) next.delete(product);
      else next.add(product);
      return next;
    });

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const result = await api.setRollbackEnabledProducts([...enabled]);
      setEnabled(new Set(result.products));
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <section
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Rollbacks
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Choose which products can use rollbacks. Rollbacks reuse a product's promotion approval
          rules, so only promotion-enrolled products are listed.
        </p>
      </div>

      {!flagOn && (
        <div
          className="flex items-start gap-2 px-3 py-2 rounded-lg text-[12px]"
          style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--text-secondary)' }}
        >
          <AlertTriangle size={14} className="shrink-0 mt-0.5" />
          <span>
            The <strong>Rollbacks</strong> feature is globally off. Per-product enrollment here has
            no effect until you enable it in{' '}
            <Link to="/settings/feature-flags" className="underline" style={{ color: 'var(--accent)' }}>
              Feature Flags
            </Link>
            .
          </span>
        </div>
      )}

      {loading ? (
        <div className="flex items-center gap-2 text-[13px] py-4" style={{ color: 'var(--text-muted)' }}>
          <Loader2 size={14} className="animate-spin" /> Loading…
        </div>
      ) : rows.length === 0 ? (
        <p className="text-[13px] py-4" style={{ color: 'var(--text-muted)' }}>
          No products have promotions enabled yet. Configure a promotion policy first, then enroll it
          in rollbacks here.
        </p>
      ) : (
        <div className="space-y-1.5">
          {rows.map(({ product, hasPromotion }) => (
            <label
              key={product}
              className="flex items-center gap-3 rounded-lg border p-2.5 cursor-pointer transition-colors"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <input
                type="checkbox"
                checked={enabled.has(product)}
                onChange={() => toggle(product)}
                className="rounded"
              />
              <span className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
                {product}
              </span>
              {!hasPromotion && (
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  (no promotion policy)
                </span>
              )}
            </label>
          ))}
        </div>
      )}

      <div className="flex items-center gap-3 pt-2 border-t" style={{ borderColor: 'var(--border-color)' }}>
        <button
          onClick={save}
          disabled={saving || loading}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-60"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          {saving ? 'Saving…' : 'Save'}
        </button>
        {saved && (
          <span className="inline-flex items-center gap-1 text-[13px]" style={{ color: 'var(--success)' }}>
            <Check size={14} /> Saved
          </span>
        )}
        {error && (
          <span className="text-[13px]" style={{ color: 'var(--danger)' }}>{error}</span>
        )}
      </div>
    </section>
  );
}
