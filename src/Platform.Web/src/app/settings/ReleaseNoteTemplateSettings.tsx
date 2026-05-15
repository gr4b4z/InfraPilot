import { useEffect, useState } from 'react';
import { Loader2, Save } from 'lucide-react';
import { api } from '@/lib/api';
import { useDeploymentStore } from '@/stores/deploymentStore';

// Edit the Handlebars template that backs the rendered release-notes output.
// Three scopes (most specific wins at render time):
//   1. Default (global fallback)
//   2. Per-product (any environment)
//   3. Per-product per-environment
//
// The editor loads the *exact* row at the chosen scope so operators see what
// would be saved there. If nothing is saved at that scope the editor starts
// empty and falling back to the parent scope is communicated explicitly.
export function ReleaseNoteTemplateSettings() {
  const { products, fetchProducts } = useDeploymentStore();
  const [product, setProduct] = useState<string>('');
  const [environment, setEnvironment] = useState<string>('');
  const [template, setTemplate] = useState<string>('');
  const [exactExists, setExactExists] = useState<boolean>(false);
  const [resolvedFromFallback, setResolvedFromFallback] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<string | null>(null);

  useEffect(() => { fetchProducts(); }, [fetchProducts]);

  // Load the exact-scope template AND a fallback-resolved template so the UI can
  // tell the user: "nothing is saved here yet; the effective template falls back to <X>".
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setSavedAt(null);
    Promise.all([
      api.getReleaseNoteTemplate({ product: product || undefined, environment: environment || undefined, exact: true }),
      api.getReleaseNoteTemplate({ product: product || undefined, environment: environment || undefined }),
    ])
      .then(([exact, resolved]) => {
        if (cancelled) return;
        const exists = exact.template.length > 0;
        setExactExists(exists);
        setTemplate(exists ? exact.template : resolved.template);
        setResolvedFromFallback(exists ? '' : describeFallbackScope(product, environment));
      })
      .catch((e) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [product, environment]);

  async function save() {
    setSaving(true); setError(null);
    try {
      await api.saveReleaseNoteTemplate(template, {
        product: product || undefined,
        environment: environment || undefined,
      });
      setSavedAt(new Date().toLocaleTimeString());
      setExactExists(true);
      setResolvedFromFallback('');
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  // "Delete" at this scope = save an empty string is not what we want (would
  // clobber rendering). Instead we can't really delete without a backend route,
  // so until that lands the button is hidden.
  // TODO: add DELETE /api/release-notes/template?product&environment to drop a row.
  void deleting;

  const productOptions = ['', ...products.map((p) => p.product)];
  const envOptions = product
    ? Object.keys(products.find((p) => p.product === product)?.environments ?? {})
    : [];
  const scopeLabel = describeScope(product, environment);

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Release Notes Template
        </h2>
        <p className="text-[12px] mt-1" style={{ color: 'var(--text-muted)' }}>
          Templates resolve in order: <strong>per (product, environment)</strong> → <strong>per product</strong> → <strong>default</strong>.
        </p>
      </div>

      <div className="flex items-end gap-3">
        <div className="flex flex-col gap-1">
          <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>Product</label>
          <select
            value={product}
            onChange={(e) => { setProduct(e.target.value); setEnvironment(''); }}
            className="px-2 py-1.5 rounded-md border text-[13px]"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
          >
            {productOptions.map((p) => (
              <option key={p} value={p}>{p === '' ? '(default — all products)' : p}</option>
            ))}
          </select>
        </div>

        <div className="flex flex-col gap-1">
          <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>Environment</label>
          <select
            value={environment}
            onChange={(e) => setEnvironment(e.target.value)}
            disabled={!product}
            className="px-2 py-1.5 rounded-md border text-[13px] disabled:opacity-50"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
          >
            <option value="">(all environments)</option>
            {envOptions.map((e) => <option key={e} value={e}>{e}</option>)}
          </select>
        </div>

        <div className="ml-auto flex items-center gap-2">
          <button
            onClick={save}
            disabled={saving || loading}
            className="inline-flex items-center gap-2 px-3 py-1.5 rounded-md text-[12px] font-medium disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)', color: 'white' }}
          >
            {saving ? <Loader2 size={12} className="animate-spin" /> : <Save size={12} />}
            Save to {scopeLabel}
          </button>
        </div>
      </div>

      {error && (
        <div className="px-3 py-2 rounded-lg text-[13px]" style={{ backgroundColor: 'var(--error-bg)', color: 'var(--error)' }}>
          {error}
        </div>
      )}

      <div className="px-3 py-2 rounded-lg text-[12px]" style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)', border: '1px solid var(--border-color)' }}>
        Editing: <strong>{scopeLabel}</strong>
        {!exactExists && resolvedFromFallback && (
          <> — <span style={{ color: 'var(--text-muted)' }}>nothing saved at this scope yet; showing template inherited from <strong>{resolvedFromFallback}</strong>. Saving creates an override here.</span></>
        )}
        {savedAt && <> · <span style={{ color: 'var(--accent)' }}>saved {savedAt}</span></>}
      </div>

      <div className="flex flex-col gap-1">
        <div className="text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>Template (Handlebars)</div>
        <textarea
          value={template}
          onChange={(e) => setTemplate(e.target.value)}
          disabled={loading}
          rows={28}
          className="w-full font-mono text-[12px] rounded-lg border p-3"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
        />
      </div>

      <details className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
        <summary style={{ cursor: 'pointer' }}>Available template fields</summary>
        <ul className="list-disc pl-5 mt-2 space-y-1">
          <li><code>{'{{product}}'}</code>, <code>{'{{environment}}'}</code>, <code>{'{{date}}'}</code>, <code>{'{{from}}'}</code>, <code>{'{{to}}'}</code></li>
          <li><code>{'{{#each services}}'}</code> — each iteration exposes:
            <ul className="list-disc pl-5 mt-1 space-y-0.5">
              <li><code>service</code>, <code>previousVersion</code>, <code>currentVersion</code>, <code>isRollback</code>, <code>deployedAt</code></li>
              <li><code>workItems</code> (each: <code>key</code>, <code>title</code>, <code>url</code>, <code>type</code>)</li>
              <li><code>pullRequests</code> / <code>pipelines</code> (each: <code>key</code>, <code>title</code>, <code>url</code>); first element exposed as <code>pullRequest</code> / <code>pipeline</code></li>
              <li><code>author</code>, <code>qa</code>, <code>triggeredBy</code> — each <code>{'{ displayName, email }'}</code></li>
              <li><code>participants</code> (each: <code>role</code>, <code>displayName</code>, <code>email</code>)</li>
            </ul>
          </li>
          <li>Use <code>{'{{{name}}}'}</code> (triple-mustache) for already-safe content (e.g. names with diacritics) so it isn't HTML-escaped.</li>
        </ul>
      </details>
    </div>
  );
}

function describeScope(product: string, environment: string): string {
  if (!product) return 'default';
  if (!environment) return product;
  return `${product} / ${environment}`;
}

function describeFallbackScope(product: string, environment: string): string {
  // If we're at (product, env) we fall back to (product) → default.
  // If we're at (product) we fall back to default.
  // If we're already at default there's no fallback (built-in default is in use).
  if (product && environment) return `${product} (product default)`;
  if (product) return 'default';
  return 'built-in default';
}
