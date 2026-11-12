import { useEffect, useRef, useState } from 'react';
import { Loader2, X } from 'lucide-react';
import { api } from '@/lib/api';
import type { Release, ReleaseInput, ReleaseLink } from '@/lib/api';
import { Dialog } from '@/components/ui/Dialog';
import { MultiValueInput } from '@/components/ui/MultiValueInput';
import { DateField } from '@/components/ui/DateField';
import { inputClass, inputStyle, labelClass, labelStyle } from '@/app/settings/formStyles';
import { useSettingsStore } from '@/stores/settingsStore';
import { cleanLinks, fromLocalInput, toLocalInput } from '@/components/releases/releaseStatus';
import { LinksEditor } from '@/components/releases/ReleaseLinks';

/**
 * Open a release, or change one's details.
 *
 * Opening one asks for three things only: the version, the products and the release date. Everything
 * else is a detail added later with Edit, and has a default until then — the name ("Release 5.11"),
 * staging and prod, every service of its products, no links or notes. Nobody is named as its release
 * manager: those are the approvers of its products' promotions to production. The checklist comes from
 * the standard template; steps are moved on the release page.
 *
 * "Already started?" (Edit only) takes a date in the past, which is how a release that is under way
 * (or over) is recorded after the fact — the portal reads deploys and promotions from that point on
 * for the services that are named rather than matched by version.
 */
export function ReleaseForm({
  release,
  onClose,
  onSaved,
}: {
  release?: Release;
  onClose: () => void;
  onSaved: (saved: Release) => void;
}) {
  const isNew = !release;
  const { activeEnvironments } = useSettingsStore();
  const versionRef = useRef<HTMLInputElement>(null);

  const [version, setVersion] = useState(release?.version ?? '');
  const [title, setTitle] = useState(release?.title ?? '');
  const [products, setProducts] = useState<string[]>(release?.products ?? []);
  const [targetDate, setTargetDate] = useState(release?.targetDate ?? '');
  const [staging, setStaging] = useState(release?.stagingEnvironment ?? 'staging');
  const [production, setProduction] = useState(release?.productionEnvironment ?? 'prod');
  const [startedAt, setStartedAt] = useState(toLocalInput(release?.startedAt));
  const [notes, setNotes] = useState(release?.notes ?? '');
  const [links, setLinks] = useState<ReleaseLink[]>(release?.links.length ? release.links : [{ label: null, url: '' }]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [knownProducts, setKnownProducts] = useState<string[]>([]);
  useEffect(() => {
    api.getDeploymentProducts().then((l) => setKnownProducts(l.map((p) => p.product))).catch(() => setKnownProducts([]));
  }, []);

  // The environments to choose from: the configured ones, plus whatever the release already names.
  const envOptions = [...new Set([...activeEnvironments.map((e) => e.key), staging, production])];
  const envName = (key: string) => activeEnvironments.find((e) => e.key === key)?.displayName ?? key;

  const line = version.trim().replace(/^v/i, '');
  const validLine = /^\d{1,4}\.\d{1,4}$/.test(line);
  const canSave = validLine && products.length > 0 && staging !== production && !saving;

  const save = async () => {
    setSaving(true);
    setError(null);
    // A new release sends only what was asked for; the API fills in the rest.
    const payload: ReleaseInput = isNew ? { version: line, products, targetDate: targetDate || null } : {
      version: line,
      products,
      title: title.trim() || null,
      stagingEnvironment: staging,
      productionEnvironment: production,
      targetDate: targetDate || null,
      notes: notes.trim() || null,
      startedAt: fromLocalInput(startedAt),
      links: cleanLinks(links),
    };
    try {
      const saved = release ? await api.updateRelease(release.id, payload) : await api.createRelease(payload);
      onSaved(saved);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save the release');
      setSaving(false);
    }
  };

  const field = 'flex flex-col gap-1';
  return (
    <Dialog onClose={onClose} ariaLabel={release ? 'Edit release' : 'New release'} initialFocusRef={versionRef} width={isNew ? 520 : 640}>
      <div className="flex items-center justify-between px-5 py-3 border-b" style={{ borderColor: 'var(--border-color)' }}>
        <h2 className="text-[15px] font-semibold">{release ? `Edit ${release.title}` : 'New release'}</h2>
        <button onClick={onClose} className="p-1 rounded hover:bg-[var(--bg-secondary)]" aria-label="Close">
          <X size={16} />
        </button>
      </div>

      <form
        className="px-5 py-4 space-y-4 max-h-[70vh] overflow-y-auto"
        onSubmit={(e) => { e.preventDefault(); if (canSave) void save(); }}
      >
        <div className={`grid grid-cols-1 gap-3 ${isNew ? 'sm:grid-cols-2' : 'sm:grid-cols-3'}`}>
          <label className={field}>
            <span className={labelClass} style={labelStyle}>Version</span>
            <input
              ref={versionRef}
              value={version}
              onChange={(e) => setVersion(e.target.value)}
              placeholder="5.11"
              className={`${inputClass} font-mono`}
              style={inputStyle}
              aria-invalid={version !== '' && !validLine}
            />
            {version !== '' && !validLine && (
              <span className="text-[11px]" style={{ color: 'var(--danger)' }}>A release line such as 5.11 or 6.0</span>
            )}
          </label>
          {isNew ? (
            <label className={field}>
              <span className={labelClass} style={labelStyle}>Release date</span>
              <DateField value={targetDate} onChange={setTargetDate} className={inputClass} style={inputStyle} ariaLabel="Release date" />
            </label>
          ) : (
            <label className={`${field} sm:col-span-2`}>
              <span className={labelClass} style={labelStyle}>Name</span>
              <input
                value={title}
                onChange={(e) => setTitle(e.target.value)}
                placeholder={validLine ? `Release ${line}` : 'Release 5.11'}
                className={inputClass}
                style={inputStyle}
              />
            </label>
          )}
        </div>

        <div className={field}>
          <span className={labelClass} style={labelStyle}>Products</span>
          <MultiValueInput
            values={products}
            onChange={setProducts}
            suggestions={knownProducts}
            placeholder="mpt, mpt-extensions…"
            ariaLabel="Products"
          />
          <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            Every service of these products with a {validLine ? line : 'x.y'} version is part of the release.
          </span>
        </div>

        {isNew && (
          <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            Every service of these products is part of the release; leave some out later from the release's
            <b>Services</b> tile. The name, environments, links and notes can be added with <b>Edit</b>. It is run by the
            release managers of its products: the people who approve their promotions to production.
          </p>
        )}

        {!isNew && (<>
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <label className={field}>
            <span className={labelClass} style={labelStyle}>Release date</span>
            <DateField value={targetDate} onChange={setTargetDate} className={inputClass} style={inputStyle} ariaLabel="Release date" />
          </label>
          <label className={field}>
            <span className={labelClass} style={labelStyle}>Tested on</span>
            <select value={staging} onChange={(e) => setStaging(e.target.value)} className={inputClass} style={inputStyle}>
              {envOptions.map((k) => <option key={k} value={k}>{envName(k)}</option>)}
            </select>
          </label>
          <label className={field}>
            <span className={labelClass} style={labelStyle}>Ships to</span>
            <select value={production} onChange={(e) => setProduction(e.target.value)} className={inputClass} style={inputStyle}>
              {envOptions.map((k) => <option key={k} value={k}>{envName(k)}</option>)}
            </select>
          </label>
        </div>

        <label className={field}>
          <span className={labelClass} style={labelStyle}>Already started?</span>
          <DateField type="datetime-local" value={startedAt} onChange={setStartedAt} className={inputClass} style={inputStyle} ariaLabel="Already started" />
          <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            When planning or the version cut began. Leave empty for a release that starts now.
          </span>
        </label>

        <div className={field}>
          <span className={labelClass} style={labelStyle}>Links</span>
          <LinksEditor links={links} onChange={setLinks} />
          <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            The Jira release version, the release plan, the retro. Pipeline runs are picked up from the deployments on their own.
          </span>
        </div>

        <label className={field}>
          <span className={labelClass} style={labelStyle}>Notes</span>
          <textarea
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            rows={3}
            placeholder="Scope, downtime, decisions"
            className={inputClass}
            style={inputStyle}
          />
        </label>
        </>)}

        {error && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{error}</p>}

        <div className="flex justify-end gap-2 pt-1">
          <button type="button" onClick={onClose} className="px-3 py-1.5 rounded-lg text-[13px]" style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}>
            Cancel
          </button>
          <button
            type="submit"
            disabled={!canSave}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium text-white disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {saving && <Loader2 size={13} className="animate-spin" />}
            {release ? 'Save' : 'Create release'}
          </button>
        </div>
      </form>
    </Dialog>
  );
}
