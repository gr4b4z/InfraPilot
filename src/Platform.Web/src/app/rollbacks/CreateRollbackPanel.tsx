import { useEffect, useMemo, useState } from 'react';
import { api } from '@/lib/api';
import type { RollbackInput, RollbackPreview, RollbackMode, DeploymentVersion } from '@/lib/api';
import { useSettingsStore } from '@/stores/settingsStore';
import { X, ArrowRight, Loader2, AlertTriangle, Undo2 } from 'lucide-react';

interface Prefill {
  product: string;
  targetEnv: string;
  service: string;
}

/**
 * Slide-over panel for composing a rollback. Two modes:
 *  - Manual: pick a target version for a single service (prefilled from a deploy
 *    event when deep-linked).
 *  - Align: pick a reference environment and exclude services; the backend
 *    resolves which services move and to what version.
 *
 * Both modes run `previewRollback` to surface the resolved items (eligible +
 * skipped-with-reason) before the user commits with `createRollback`.
 */
export function CreateRollbackPanel({
  prefill,
  onClose,
  onCreated,
}: {
  prefill: Prefill;
  onClose: () => void;
  onCreated: () => void;
}) {
  const { environments, getDisplayName } = useSettingsStore();

  const [products, setProducts] = useState<string[]>([]);
  const [product, setProduct] = useState(prefill.product);
  const [targetEnv, setTargetEnv] = useState(prefill.targetEnv);
  // A prefilled service implies a single-service manual rollback.
  const [mode, setMode] = useState<RollbackMode>(prefill.service ? 'Manual' : 'Align');
  const [referenceEnv, setReferenceEnv] = useState('');
  const [reason, setReason] = useState('');
  const [excluded, setExcluded] = useState<Set<string>>(new Set());

  // Manual mode: single service + chosen target version. Prefilled when deep-linked from a
  // deployment; otherwise the user picks a service from those deployed in the target env.
  const [manualService, setManualService] = useState(prefill.service);
  const [manualVersion, setManualVersion] = useState('');
  const [services, setServices] = useState<string[]>([]);
  const [versions, setVersions] = useState<DeploymentVersion[]>([]);

  const [preview, setPreview] = useState<RollbackPreview | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .getRollbackEnabledProducts()
      .then((d) => setProducts(d.products || []))
      .catch(() => setProducts([]));
  }, []);

  // Manual mode (not deep-linked): load the services deployed in the target env so the user
  // can pick which one to roll back.
  useEffect(() => {
    if (mode !== 'Manual' || prefill.service || !product || !targetEnv) {
      setServices([]);
      return;
    }
    api
      .getDeploymentState({ product, environment: targetEnv })
      .then((rows) => setServices([...new Set(rows.map((r) => r.service))].sort()))
      .catch(() => setServices([]));
  }, [mode, product, targetEnv, prefill.service]);

  // Manual mode: load version history for the chosen service so the user can
  // pick which version to roll back to.
  useEffect(() => {
    if (mode !== 'Manual' || !product || !targetEnv || !manualService) {
      setVersions([]);
      return;
    }
    api
      .getDeploymentVersions({ product, environment: targetEnv, service: manualService, limit: 50 })
      .then((d) => setVersions(d.versions || []))
      .catch(() => setVersions([]));
  }, [mode, product, targetEnv, manualService]);

  // Reset any stale preview when the inputs that shape it change. NOTE: `excluded` is
  // intentionally NOT here — it's a client-side selection over an existing preview, so toggling
  // a service's checkbox must not wipe the list (it would force a re-preview every click).
  useEffect(() => {
    setPreview(null);
  }, [product, targetEnv, mode, referenceEnv, manualVersion]);

  const envOptions = useMemo(() => environments.map((e) => e.key), [environments]);

  const buildBody = (applyExclusions: boolean): RollbackInput => {
    const body: RollbackInput = {
      product,
      targetEnv,
      mode,
      reason: reason.trim() || undefined,
    };
    if (mode === 'Align') {
      body.referenceEnv = referenceEnv;
      // Preview shows every differing service; exclusions are applied only at create time so
      // ticking a checkbox is a pure client-side selection (no re-preview needed).
      body.exclude = applyExclusions ? Array.from(excluded) : [];
    } else {
      body.items = manualService && manualVersion
        ? [{ service: manualService, toVersion: manualVersion }]
        : [];
    }
    return body;
  };

  const canPreview =
    !!product &&
    !!targetEnv &&
    (mode === 'Align'
      ? !!referenceEnv && referenceEnv !== targetEnv
      : !!manualService && !!manualVersion);

  const handlePreview = async () => {
    setError(null);
    setPreviewing(true);
    try {
      const result = await api.previewRollback(buildBody(false));
      setPreview(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Preview failed');
      setPreview(null);
    } finally {
      setPreviewing(false);
    }
  };

  const handleSubmit = async () => {
    setError(null);
    setSubmitting(true);
    try {
      await api.createRollback(buildBody(true));
      onCreated();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create rollback');
    } finally {
      setSubmitting(false);
    }
  };

  // Eligible = backend-eligible AND not unchecked by the operator. Recomputes live as checkboxes
  // toggle, so the count and the Create button reflect the current selection without a re-preview.
  const eligibleCount = preview?.items.filter((i) => i.eligible && !excluded.has(i.service)).length ?? 0;

  return (
    <>
      <div className="fixed inset-0 z-40" style={{ backgroundColor: 'rgba(0,0,0,0.35)' }} onClick={onClose} />
      <div
        className="fixed inset-y-0 right-0 w-[480px] z-50 border-l shadow-lg overflow-y-auto"
        style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
      >
        <div className="p-5 space-y-5">
          {/* Header */}
          <div className="flex items-start justify-between">
            <div className="flex items-center gap-2">
              <Undo2 size={18} style={{ color: 'var(--accent)' }} />
              <h2 className="text-base font-semibold" style={{ color: 'var(--text-primary)' }}>
                New rollback
              </h2>
            </div>
            <button
              onClick={onClose}
              className="p-1 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              <X size={18} />
            </button>
          </div>

          {/* Product */}
          <Field label="Product">
            <select
              value={product}
              onChange={(e) => setProduct(e.target.value)}
              className="w-full rounded-lg border px-3 py-2 text-[13px]"
              style={selectStyle}
            >
              <option value="">Select a product…</option>
              {[...new Set([...(product ? [product] : []), ...products])].sort().map((p) => (
                <option key={p} value={p}>
                  {p}
                </option>
              ))}
            </select>
          </Field>

          {/* Target env */}
          <Field label="Target environment" hint="The environment that will be rolled back">
            <select
              value={targetEnv}
              onChange={(e) => setTargetEnv(e.target.value)}
              className="w-full rounded-lg border px-3 py-2 text-[13px]"
              style={selectStyle}
            >
              <option value="">Select an environment…</option>
              {envOptions.map((env) => (
                <option key={env} value={env}>
                  {getDisplayName(env)}
                </option>
              ))}
            </select>
          </Field>

          {/* Mode */}
          <Field label="Mode">
            <div className="inline-flex rounded-lg p-0.5 gap-0.5" style={{ backgroundColor: 'var(--bg-secondary)', border: '1px solid var(--border-color)' }}>
              {(['Manual', 'Align'] as RollbackMode[]).map((m) => (
                <button
                  key={m}
                  onClick={() => setMode(m)}
                  className="px-3.5 py-1.5 text-[13px] font-medium rounded-md transition-all"
                  style={{
                    backgroundColor: mode === m ? 'var(--bg-primary)' : 'transparent',
                    color: mode === m ? 'var(--text-primary)' : 'var(--text-muted)',
                  }}
                >
                  {m}
                </button>
              ))}
            </div>
          </Field>

          {/* Manual: service + version */}
          {mode === 'Manual' && (
            <>
              <Field label="Service">
                {prefill.service ? (
                  // Deep-linked from a deployment — service is fixed.
                  <div
                    className="rounded-lg border px-3 py-2 text-[13px]"
                    style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
                  >
                    {manualService}
                  </div>
                ) : (
                  <select
                    value={manualService}
                    onChange={(e) => {
                      setManualService(e.target.value);
                      setManualVersion('');
                    }}
                    className="w-full rounded-lg border px-3 py-2 text-[13px]"
                    style={selectStyle}
                    disabled={!product || !targetEnv || services.length === 0}
                  >
                    <option value="">
                      {!product || !targetEnv
                        ? 'Pick a product and environment first…'
                        : services.length === 0
                          ? 'No services deployed here'
                          : 'Select a service…'}
                    </option>
                    {[...new Set([...(manualService ? [manualService] : []), ...services])].map((s) => (
                      <option key={s} value={s}>
                        {s}
                      </option>
                    ))}
                  </select>
                )}
              </Field>
              {manualService && (
                <Field label="Roll back to version">
                  <select
                    value={manualVersion}
                    onChange={(e) => setManualVersion(e.target.value)}
                    className="w-full rounded-lg border px-3 py-2 text-[13px]"
                    style={selectStyle}
                    disabled={versions.length === 0}
                  >
                    <option value="">{versions.length === 0 ? 'No prior versions found' : 'Select a version…'}</option>
                    {versions.map((v) => (
                      <option key={v.id} value={v.version}>
                        v{v.version}
                      </option>
                    ))}
                  </select>
                </Field>
              )}
            </>
          )}

          {/* Align: reference env + exclusions */}
          {mode === 'Align' && (
            <Field label="Reference environment" hint="Target services will be aligned to the versions running here">
              <select
                value={referenceEnv}
                onChange={(e) => setReferenceEnv(e.target.value)}
                className="w-full rounded-lg border px-3 py-2 text-[13px]"
                style={selectStyle}
              >
                <option value="">Select a reference…</option>
                {envOptions
                  .filter((env) => env !== targetEnv)
                  .map((env) => (
                    <option key={env} value={env}>
                      {getDisplayName(env)}
                    </option>
                  ))}
              </select>
            </Field>
          )}

          {/* Reason */}
          <Field label="Reason" hint="Optional — surfaced on the request and to approvers">
            <textarea
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              rows={2}
              placeholder="Why are you rolling back?"
              className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none"
              style={selectStyle}
            />
          </Field>

          {error && (
            <div
              className="flex items-start gap-2 px-3 py-2 rounded-lg text-[12px]"
              style={{ backgroundColor: 'var(--danger-bg)', color: 'var(--danger)' }}
            >
              <AlertTriangle size={14} className="shrink-0 mt-0.5" />
              <span>{error}</span>
            </div>
          )}

          {/* Preview */}
          {preview && (
            <div className="space-y-2">
              <h3 className="text-[11px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
                Preview — {eligibleCount} of {preview.items.length} eligible
              </h3>
              <div className="space-y-1.5">
                {preview.items.map((item) => (
                  <div
                    key={item.service}
                    className="flex items-center gap-2 text-[12px] px-2.5 py-1.5 rounded-lg border"
                    style={{
                      borderColor: 'var(--border-color)',
                      backgroundColor: item.eligible ? 'var(--bg-primary)' : 'var(--bg-secondary)',
                      opacity: item.eligible ? 1 : 0.7,
                    }}
                  >
                    {mode === 'Align' && (
                      <input
                        type="checkbox"
                        checked={!excluded.has(item.service)}
                        onChange={() =>
                          setExcluded((prev) => {
                            const next = new Set(prev);
                            if (next.has(item.service)) next.delete(item.service);
                            else next.add(item.service);
                            return next;
                          })
                        }
                        title="Include in rollback"
                        className="rounded shrink-0"
                      />
                    )}
                    <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
                      {item.service}
                    </span>
                    {item.eligible ? (
                      <>
                        <span className="font-mono text-[11px]">v{item.fromVersion}</span>
                        <ArrowRight size={11} style={{ color: 'var(--text-muted)' }} />
                        <span className="font-mono text-[11px]">v{item.toVersion}</span>
                      </>
                    ) : (
                      <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                        skipped{item.skipReason ? ` — ${item.skipReason}` : ''}
                      </span>
                    )}
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Actions */}
          <div className="flex items-center gap-2 pt-2">
            <button
              onClick={handlePreview}
              disabled={!canPreview || previewing}
              className="flex items-center gap-1.5 px-3 py-2 rounded-lg text-[13px] font-medium transition-opacity"
              style={{
                border: '1px solid var(--border-color)',
                color: 'var(--text-primary)',
                opacity: !canPreview || previewing ? 0.5 : 1,
              }}
            >
              {previewing && <Loader2 size={13} className="animate-spin" />}
              {preview ? 'Refresh preview' : 'Preview'}
            </button>
            <button
              onClick={handleSubmit}
              disabled={!preview || eligibleCount === 0 || submitting}
              className="flex items-center gap-1.5 px-3 py-2 rounded-lg text-[13px] font-medium transition-opacity"
              style={{
                backgroundColor: 'var(--accent)',
                color: '#fff',
                opacity: !preview || eligibleCount === 0 || submitting ? 0.5 : 1,
              }}
            >
              {submitting && <Loader2 size={13} className="animate-spin" />}
              Create rollback
            </button>
          </div>
        </div>
      </div>
    </>
  );
}

const selectStyle: React.CSSProperties = {
  borderColor: 'var(--border-color)',
  backgroundColor: 'var(--bg-primary)',
  color: 'var(--text-primary)',
};

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="space-y-1.5">
      <label className="block text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
        {label}
      </label>
      {children}
      {hint && (
        <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          {hint}
        </p>
      )}
    </div>
  );
}
