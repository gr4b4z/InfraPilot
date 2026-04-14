import { useState, useEffect } from 'react';
import {
  useSettingsStore,
  DEFAULT_ACTIVITY_TEMPLATE,
  type EnvironmentConfig,
  type ActivityTemplateLine,
} from '@/stores/settingsStore';
import { GripVertical, Plus, Trash2, Check, RotateCcw, X } from 'lucide-react';
import { CatalogSettings } from './CatalogSettings';

export function SettingsPage() {
  const {
    defaultEnvironments,
    productEnvironments,
    activityTemplate,
    setDefaultEnvironments,
    setProductEnvironments,
    removeProductEnvironments,
    setActivityTemplate,
  } = useSettingsStore();

  // Which product config is being edited: null = default
  const [selectedProduct, setSelectedProduct] = useState<string | null>(null);
  const [newProductName, setNewProductName] = useState('');
  const [showAddProduct, setShowAddProduct] = useState(false);

  const currentEnvs =
    selectedProduct === null
      ? defaultEnvironments
      : productEnvironments.find((pe) => pe.product === selectedProduct)?.environments ??
        defaultEnvironments;

  const [envItems, setEnvItems] = useState<EnvironmentConfig[]>(currentEnvs);
  const [templateLines, setTemplateLines] = useState<ActivityTemplateLine[]>(activityTemplate);
  const [envSaved, setEnvSaved] = useState(false);
  const [templateSaved, setTemplateSaved] = useState(false);
  const [dragIndex, setDragIndex] = useState<number | null>(null);

  // Sync envItems when switching product tabs
  useEffect(() => {
    const envs =
      selectedProduct === null
        ? defaultEnvironments
        : productEnvironments.find((pe) => pe.product === selectedProduct)?.environments ??
          defaultEnvironments;
    setEnvItems(envs);
  }, [selectedProduct, defaultEnvironments, productEnvironments]);

  useEffect(() => {
    setTemplateLines(activityTemplate);
  }, [activityTemplate]);

  // ── Environments ──

  const handleEnvSave = () => {
    const cleaned = envItems.filter((i) => i.key.trim() !== '');
    if (selectedProduct === null) {
      setDefaultEnvironments(cleaned);
    } else {
      setProductEnvironments(selectedProduct, cleaned);
    }
    setEnvItems(cleaned);
    setEnvSaved(true);
    setTimeout(() => setEnvSaved(false), 2000);
  };

  const handleRemoveProductConfig = () => {
    if (selectedProduct === null) return;
    removeProductEnvironments(selectedProduct);
    setSelectedProduct(null);
  };

  const handleAddProduct = () => {
    const name = newProductName.trim();
    if (!name) return;
    // Initialize with a copy of defaults
    setProductEnvironments(name, [...defaultEnvironments]);
    setNewProductName('');
    setShowAddProduct(false);
    setSelectedProduct(name);
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

  const configuredProducts = productEnvironments.map((pe) => pe.product);

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
            Define the environments and their display order per product. Products without a
            specific config use the default. Drag to reorder.
          </p>
        </div>

        {/* Product tabs */}
        <div className="flex items-center gap-1.5 flex-wrap">
          <button
            onClick={() => setSelectedProduct(null)}
            className="px-3 py-1.5 text-[12px] font-medium rounded-lg transition-all"
            style={{
              backgroundColor: selectedProduct === null ? 'var(--accent-muted)' : 'transparent',
              color: selectedProduct === null ? 'var(--accent)' : 'var(--text-muted)',
              border:
                selectedProduct === null
                  ? '1px solid var(--accent)'
                  : '1px solid var(--border-color)',
            }}
          >
            Default
          </button>
          {configuredProducts.map((p) => (
            <button
              key={p}
              onClick={() => setSelectedProduct(p)}
              className="px-3 py-1.5 text-[12px] font-medium rounded-lg transition-all"
              style={{
                backgroundColor: selectedProduct === p ? 'var(--accent-muted)' : 'transparent',
                color: selectedProduct === p ? 'var(--accent)' : 'var(--text-muted)',
                border:
                  selectedProduct === p
                    ? '1px solid var(--accent)'
                    : '1px solid var(--border-color)',
              }}
            >
              {p}
            </button>
          ))}

          {showAddProduct ? (
            <div className="inline-flex items-center gap-1.5">
              <input
                type="text"
                value={newProductName}
                onChange={(e) => setNewProductName(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && handleAddProduct()}
                placeholder="product name"
                autoFocus
                className="px-2 py-1 rounded-lg border text-[12px] outline-none transition-colors focus:border-[var(--accent)]"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                  width: 140,
                }}
              />
              <button
                onClick={handleAddProduct}
                className="p-1 rounded-lg hover:opacity-80"
                style={{ color: 'var(--accent)' }}
              >
                <Check size={14} />
              </button>
              <button
                onClick={() => {
                  setShowAddProduct(false);
                  setNewProductName('');
                }}
                className="p-1 rounded-lg hover:opacity-80"
                style={{ color: 'var(--text-muted)' }}
              >
                <X size={14} />
              </button>
            </div>
          ) : (
            <button
              onClick={() => setShowAddProduct(true)}
              className="px-2.5 py-1.5 text-[12px] font-medium rounded-lg transition-all hover:opacity-80"
              style={{ color: 'var(--text-muted)', border: '1px dashed var(--border-color)' }}
            >
              <Plus size={12} className="inline -mt-0.5 mr-0.5" />
              Product
            </button>
          )}
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
          {selectedProduct !== null && (
            <button
              onClick={handleRemoveProductConfig}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              <Trash2 size={13} />
              Remove Override
            </button>
          )}
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

      {/* ── Service Catalog ── */}
      <CatalogSettings />
    </div>
  );
}
