import { useState, useEffect } from 'react';
import {
  useSettingsStore,
  DEFAULT_ACTIVITY_TEMPLATE,
  type EnvironmentConfig,
  type ActivityTemplateLine,
  type RoleConfig,
} from '@/stores/settingsStore';
import { useAuthStore } from '@/stores/authStore';
import { api } from '@/lib/api';
import { GripVertical, Plus, Trash2, Check, RotateCcw } from 'lucide-react';
import { CatalogSettings } from './CatalogSettings';
import { FeatureFlagSettings } from './FeatureFlagSettings';
import { PromotionSettings } from './PromotionSettings';

// Mirrors the server-side RoleNormalizer so admin-entered keys match what the backend stores.
function canonicaliseRoleKey(input: string): string {
  if (!input) return '';
  let s = input.trim();
  s = s.replace(/([a-z0-9])([A-Z])/g, '$1-$2'); // camelCase boundary
  s = s.toLowerCase();
  s = s.replace(/[\s_]+/g, '-');
  s = s.replace(/[^a-z0-9-]/g, '-');
  s = s.replace(/-+/g, '-').replace(/^-|-$/g, '');
  return s;
}

export function SettingsPage() {
  const {
    environments,
    roles,
    activityTemplate,
    setEnvironments,
    setRoles,
    setActivityTemplate,
  } = useSettingsStore();

  const [envItems, setEnvItems] = useState<EnvironmentConfig[]>(environments);
  const [roleItems, setRoleItems] = useState<RoleConfig[]>(roles);
  const [templateLines, setTemplateLines] = useState<ActivityTemplateLine[]>(activityTemplate);
  const [envSaved, setEnvSaved] = useState(false);
  const [rolesSaved, setRolesSaved] = useState(false);
  const [templateSaved, setTemplateSaved] = useState(false);
  const [dragIndex, setDragIndex] = useState<number | null>(null);

  useEffect(() => {
    setEnvItems(environments);
  }, [environments]);

  useEffect(() => {
    setRoleItems(roles);
  }, [roles]);

  useEffect(() => {
    setTemplateLines(activityTemplate);
  }, [activityTemplate]);

  // ── Environments ──

  const handleEnvSave = () => {
    const cleaned = envItems.filter((i) => i.key.trim() !== '');
    setEnvironments(cleaned);
    setEnvItems(cleaned);
    setEnvSaved(true);
    setTimeout(() => setEnvSaved(false), 2000);
  };

  const updateEnvItem = (index: number, field: keyof EnvironmentConfig, value: string) => {
    setEnvItems((prev) => prev.map((item, i) => (i === index ? { ...item, [field]: value } : item)));
  };

  const removeEnvItem = (index: number) => {
    setEnvItems((prev) => prev.filter((_, i) => i !== index));
  };

  const addEnvItem = () => {
    setEnvItems((prev) => [...prev, { key: '', displayName: '' }]);
  };

  const handleDragStart = (index: number) => setDragIndex(index);
  const handleDragOver = (e: React.DragEvent, index: number) => {
    e.preventDefault();
    if (dragIndex === null || dragIndex === index) return;
    setEnvItems((prev) => {
      const next = [...prev];
      const [moved] = next.splice(dragIndex, 1);
      next.splice(index, 0, moved);
      return next;
    });
    setDragIndex(index);
  };
  const handleDragEnd = () => setDragIndex(null);

  // ── Roles ──

  const handleRolesSave = () => {
    // Normalise on save: trim both fields, canonicalise keys to lower-kebab so the
    // dictionary matches exactly what the backend stores. Drop rows with an empty key.
    const cleaned = roleItems
      .map((r) => ({
        key: canonicaliseRoleKey(r.key),
        displayName: r.displayName.trim(),
      }))
      .filter((r) => r.key.length > 0);
    setRoles(cleaned);
    setRoleItems(cleaned);
    setRolesSaved(true);
    setTimeout(() => setRolesSaved(false), 2000);
  };

  const updateRoleItem = (index: number, field: keyof RoleConfig, value: string) => {
    setRoleItems((prev) => prev.map((item, i) => (i === index ? { ...item, [field]: value } : item)));
  };

  const removeRoleItem = (index: number) => {
    setRoleItems((prev) => prev.filter((_, i) => i !== index));
  };

  const addRoleItem = () => {
    setRoleItems((prev) => [...prev, { key: '', displayName: '' }]);
  };

  // ── Activity Template ──

  const handleTemplateSave = () => {
    const cleaned = templateLines.filter((l) => l.template.trim() !== '');
    setActivityTemplate(cleaned);
    setTemplateLines(cleaned);
    setTemplateSaved(true);
    setTimeout(() => setTemplateSaved(false), 2000);
  };

  const updateTemplateLine = (index: number, field: keyof ActivityTemplateLine, value: string) => {
    setTemplateLines((prev) =>
      prev.map((item, i) => (i === index ? { ...item, [field]: value } : item))
    );
  };

  const removeTemplateLine = (index: number) => {
    setTemplateLines((prev) => prev.filter((_, i) => i !== index));
  };

  const addTemplateLine = () => {
    setTemplateLines((prev) => [...prev, { template: '', style: 'muted' }]);
  };

  const resetTemplate = () => {
    setTemplateLines(DEFAULT_ACTIVITY_TEMPLATE);
  };

  return (
    <div className="space-y-6">
      <div>
        <h1
          className="text-xl font-semibold tracking-tight"
          style={{ color: 'var(--text-primary)' }}
        >
          Settings
        </h1>
        <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
          Configure platform preferences
        </p>
      </div>

      {/* ── Environments ── */}
      <div
        className="rounded-xl border p-5 space-y-4"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div>
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            Environments
          </h2>
          <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
            Define the environments and their display order. Drag to reorder.
          </p>
        </div>

        {/* Env grid */}
        <div className="space-y-1.5">
          <div
            className="grid grid-cols-[28px_1fr_1fr_32px] gap-2 px-1 text-[11px] font-medium uppercase tracking-wider"
            style={{ color: 'var(--text-muted)' }}
          >
            <span />
            <span>Key</span>
            <span>Display Name</span>
            <span />
          </div>

          {envItems.map((item, index) => (
            <div
              key={index}
              draggable
              onDragStart={() => handleDragStart(index)}
              onDragOver={(e) => handleDragOver(e, index)}
              onDragEnd={handleDragEnd}
              className="grid grid-cols-[28px_1fr_1fr_32px] gap-2 items-center rounded-lg p-1.5 transition-colors"
              style={{ backgroundColor: dragIndex === index ? 'var(--accent-muted)' : undefined }}
            >
              <span
                className="cursor-grab flex items-center justify-center"
                style={{ color: 'var(--text-muted)' }}
              >
                <GripVertical size={14} />
              </span>
              <input
                type="text"
                value={item.key}
                onChange={(e) => updateEnvItem(index, 'key', e.target.value)}
                placeholder="e.g. staging"
                className="px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                }}
              />
              <input
                type="text"
                value={item.displayName}
                onChange={(e) => updateEnvItem(index, 'displayName', e.target.value)}
                placeholder="e.g. Staging"
                className="px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                }}
              />
              <button
                onClick={() => removeEnvItem(index)}
                className="p-1 rounded-lg transition-colors hover:opacity-80"
                style={{ color: 'var(--text-muted)' }}
              >
                <Trash2 size={14} />
              </button>
            </div>
          ))}
        </div>

        <button
          onClick={addEnvItem}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
          style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
        >
          <Plus size={14} />
          Add Environment
        </button>

        <div
          className="flex items-center gap-3 pt-2 border-t"
          style={{ borderColor: 'var(--border-color)' }}
        >
          <button
            onClick={handleEnvSave}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            Save
          </button>
          {envSaved && (
            <span
              className="inline-flex items-center gap-1 text-[13px]"
              style={{ color: 'var(--success)' }}
            >
              <Check size={14} /> Saved
            </span>
          )}
        </div>
      </div>

      {/* ── Roles ── */}
      <div
        className="rounded-xl border p-5 space-y-4"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div>
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            Participant Roles
          </h2>
          <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
            Map role keys from deploy-event ingest and promotion assignments to friendly display
            names. The platform canonicalises role strings to lower-kebab-case by default
            (<code>triggered-by</code>, <code>qa</code>); unknown keys fall back to a humanised
            form of the key.
          </p>
        </div>

        <div className="space-y-1.5">
          <div
            className="grid grid-cols-[1fr_1fr_32px] gap-2 px-1 text-[11px] font-medium uppercase tracking-wider"
            style={{ color: 'var(--text-muted)' }}
          >
            <span>Key</span>
            <span>Display Name</span>
            <span />
          </div>

          {roleItems.map((item, index) => (
            <div
              key={index}
              className="grid grid-cols-[1fr_1fr_32px] gap-2 items-center rounded-lg p-1.5"
            >
              <input
                type="text"
                value={item.key}
                onChange={(e) => updateRoleItem(index, 'key', e.target.value)}
                placeholder="e.g. triggered-by"
                className="px-2.5 py-1.5 rounded-lg border text-[13px] font-mono outline-none transition-colors focus:border-[var(--accent)]"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                }}
              />
              <input
                type="text"
                value={item.displayName}
                onChange={(e) => updateRoleItem(index, 'displayName', e.target.value)}
                placeholder="e.g. Triggered by"
                className="px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                }}
              />
              <button
                onClick={() => removeRoleItem(index)}
                className="p-1 rounded-lg transition-colors hover:opacity-80"
                style={{ color: 'var(--text-muted)' }}
              >
                <Trash2 size={14} />
              </button>
            </div>
          ))}
        </div>

        <button
          onClick={addRoleItem}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
          style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
        >
          <Plus size={14} />
          Add Role
        </button>

        <div
          className="flex items-center gap-3 pt-2 border-t"
          style={{ borderColor: 'var(--border-color)' }}
        >
          <button
            onClick={handleRolesSave}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            Save
          </button>
          {rolesSaved && (
            <span
              className="inline-flex items-center gap-1 text-[13px]"
              style={{ color: 'var(--success)' }}
            >
              <Check size={14} /> Saved
            </span>
          )}
        </div>
      </div>

      {/* ── Activity Card Template ── */}
      <div
        className="rounded-xl border p-5 space-y-4"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div>
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            Activity Card Template
          </h2>
          <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
            Configure which fields appear on deployment activity cards. Each line is a template with
            placeholders. Lines where all placeholders are empty are hidden.
          </p>
        </div>

        {/* Placeholder reference */}
        <div
          className="rounded-lg p-3 text-[12px] space-y-1"
          style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-muted)' }}
        >
          <div className="font-medium" style={{ color: 'var(--text-secondary)' }}>
            Available placeholders:
          </div>
          <div className="grid grid-cols-2 gap-x-6 gap-y-0.5">
            <span>
              <code className="text-[11px]">{'{label:workItemTitle}'}</code> — Jira issue title
            </span>
            <span>
              <code className="text-[11px]">{'{label:prTitle}'}</code> — PR title
            </span>
            <span>
              <code className="text-[11px]">{'{label:workItemStatus}'}</code> — Jira status
            </span>
            <span>
              <code className="text-[11px]">{'{ref:work-item:key}'}</code> — Jira ticket key
            </span>
            <span>
              <code className="text-[11px]">{'{participant:PR Author}'}</code> — PR author name
            </span>
            <span>
              <code className="text-[11px]">{'{participant:PR Reviewer}'}</code> — PR reviewer
            </span>
            <span>
              <code className="text-[11px]">{'{participant:QA}'}</code> — QA person
            </span>
            <span>
              <code className="text-[11px]">{'{time}'}</code> — relative time
            </span>
            <span>
              <code className="text-[11px]">{'{service}'}</code>,{' '}
              <code className="text-[11px]">{'{environment}'}</code>,{' '}
              <code className="text-[11px]">{'{version}'}</code>,{' '}
              <code className="text-[11px]">{'{source}'}</code>
            </span>
            <span>
              <code className="text-[11px]">{'{ref:pull-request:url}'}</code>,{' '}
              <code className="text-[11px]">{'{ref:repository:revision}'}</code>
            </span>
          </div>
        </div>

        <div className="space-y-1.5">
          <div
            className="grid grid-cols-[1fr_120px_32px] gap-2 px-1 text-[11px] font-medium uppercase tracking-wider"
            style={{ color: 'var(--text-muted)' }}
          >
            <span>Template</span>
            <span>Style</span>
            <span />
          </div>

          {templateLines.map((line, index) => (
            <div
              key={index}
              className="grid grid-cols-[1fr_120px_32px] gap-2 items-center rounded-lg p-1.5"
            >
              <input
                type="text"
                value={line.template}
                onChange={(e) => updateTemplateLine(index, 'template', e.target.value)}
                placeholder="e.g. {ref:work-item:key} — {label:workItemTitle}"
                className="px-2.5 py-1.5 rounded-lg border text-[13px] font-mono outline-none transition-colors focus:border-[var(--accent)]"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                }}
              />
              <select
                value={line.style}
                onChange={(e) => updateTemplateLine(index, 'style', e.target.value)}
                className="px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                }}
              >
                <option value="primary">Primary</option>
                <option value="secondary">Secondary</option>
                <option value="muted">Muted</option>
              </select>
              <button
                onClick={() => removeTemplateLine(index)}
                className="p-1 rounded-lg transition-colors hover:opacity-80"
                style={{ color: 'var(--text-muted)' }}
              >
                <Trash2 size={14} />
              </button>
            </div>
          ))}
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={addTemplateLine}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
            style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
          >
            <Plus size={14} />
            Add Line
          </button>
          <button
            onClick={resetTemplate}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
            style={{ color: 'var(--text-muted)' }}
          >
            <RotateCcw size={13} />
            Reset to Default
          </button>
        </div>

        <div
          className="flex items-center gap-3 pt-2 border-t"
          style={{ borderColor: 'var(--border-color)' }}
        >
          <button
            onClick={handleTemplateSave}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            Save
          </button>
          {templateSaved && (
            <span
              className="inline-flex items-center gap-1 text-[13px]"
              style={{ color: 'var(--success)' }}
            >
              <Check size={14} /> Saved
            </span>
          )}
        </div>
      </div>

      {/* ── Feature Flags ── */}
      <FeatureFlagSettings />

      {/* ── Service Catalog ── */}
      <CatalogSettings />

      {/* ── Promotions ── */}
      <PromotionSettings />

      {/* ── Deployment Maintenance (admin only) ── */}
      <DeploymentMaintenanceCard />
    </div>
  );
}

// ── Deployment Maintenance ──

function DeploymentMaintenanceCard() {
  const isAdmin = useAuthStore((s) => s.user?.isAdmin) ?? false;
  const [scanResult, setScanResult] = useState<{ groups: number; rows: number } | null>(null);
  const [scanning, setScanning] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [removedResult, setRemovedResult] = useState<{ groups: number; rows: number } | null>(null);
  const [error, setError] = useState<string | null>(null);

  if (!isAdmin) return null;

  const handleScan = async () => {
    setScanning(true);
    setError(null);
    setRemovedResult(null);
    try {
      const result = await api.getDeploymentDuplicatesPreview();
      setScanResult(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to scan for duplicates');
    } finally {
      setScanning(false);
    }
  };

  const handleRemove = async () => {
    setRemoving(true);
    setError(null);
    try {
      const result = await api.removeDeploymentDuplicates();
      setRemovedResult(result);
      setScanResult(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to remove duplicates');
    } finally {
      setRemoving(false);
    }
  };

  const handleCancel = () => {
    setScanResult(null);
    setError(null);
  };

  return (
    <div
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Deployment Maintenance
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Duplicate deployment events can accumulate when CI systems retry ingest webhooks. Scan to
          see how many exist, then remove them. Duplicates are rows matching on product, service,
          environment, version, deployedAt and source — the earliest ingested row per group is kept.
        </p>
      </div>

      <div className="flex items-center gap-3 flex-wrap">
        {/* Idle or post-remove — show Scan button */}
        {!scanResult && (
          <button
            onClick={handleScan}
            disabled={scanning}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {scanning ? 'Scanning…' : 'Scan for duplicates'}
          </button>
        )}

        {/* Scan found zero duplicates */}
        {scanResult && scanResult.rows === 0 && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
              No duplicates found.
            </span>
            <button
              onClick={handleCancel}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Dismiss
            </button>
          </>
        )}

        {/* Scan found duplicates — two-step confirm */}
        {scanResult && scanResult.rows > 0 && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
              Found <strong>{scanResult.rows}</strong>{' '}
              {scanResult.rows === 1 ? 'duplicate' : 'duplicates'} across{' '}
              <strong>{scanResult.groups}</strong>{' '}
              {scanResult.groups === 1 ? 'group' : 'groups'}.
            </span>
            <button
              onClick={handleRemove}
              disabled={removing}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
              style={{ backgroundColor: 'var(--danger, #dc2626)' }}
            >
              <Trash2 size={14} />
              {removing ? 'Removing…' : 'Remove duplicates'}
            </button>
            <button
              onClick={handleCancel}
              disabled={removing}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Cancel
            </button>
          </>
        )}

        {/* Post-remove success */}
        {removedResult && (
          <span
            className="inline-flex items-center gap-1 text-[13px]"
            style={{ color: 'var(--success)' }}
          >
            <Check size={14} />
            Removed {removedResult.rows}{' '}
            {removedResult.rows === 1 ? 'duplicate' : 'duplicates'} across{' '}
            {removedResult.groups} {removedResult.groups === 1 ? 'group' : 'groups'}.
          </span>
        )}
      </div>

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
