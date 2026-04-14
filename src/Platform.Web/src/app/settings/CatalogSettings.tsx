import { useState, useEffect } from 'react';
import { api } from '@/lib/api';
import { Plus, Pencil, Trash2, Check, X, AlertCircle, ToggleLeft, ToggleRight } from 'lucide-react';

interface CatalogAdminItem {
  id: string;
  slug: string;
  name: string;
  description?: string;
  category: string;
  icon?: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

type View = 'list' | 'edit' | 'create';

export function CatalogSettings() {
  const [items, setItems] = useState<CatalogAdminItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [view, setView] = useState<View>('list');
  const [editSlug, setEditSlug] = useState<string | null>(null);
  const [yamlContent, setYamlContent] = useState('');
  const [errors, setErrors] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null);

  const loadItems = () => {
    setLoading(true);
    api.getCatalogAdmin()
      .then((data) => setItems(data.items || []))
      .catch(() => setItems([]))
      .finally(() => setLoading(false));
  };

  useEffect(() => { loadItems(); }, []);

  const handleToggle = async (slug: string, currentActive: boolean) => {
    try {
      await api.toggleCatalogItem(slug, !currentActive);
      setItems((prev) =>
        prev.map((i) => (i.slug === slug ? { ...i, isActive: !currentActive } : i))
      );
    } catch {
      // ignore
    }
  };

  const handleEdit = (slug: string) => {
    // Fetch the full item YAML from the detail endpoint — for now, we
    // load the item and let the user edit the YAML representation.
    // The backend stores YAML in CatalogItemVersion, but the admin
    // endpoint doesn't return it. We'll construct a template.
    const item = items.find((i) => i.slug === slug);
    if (!item) return;
    // Pre-fill with a YAML template from known fields
    const yaml = `id: ${item.slug}
name: ${item.name}
description: ${item.description || ''}
category: ${item.category}
icon: ${item.icon || ''}

inputs: []

# approval:
#   required: false

executor:
  type: manual
  parameters_map: {}
`;
    setYamlContent(yaml);
    setEditSlug(slug);
    setErrors([]);
    setView('edit');
  };

  const handleCreate = () => {
    setYamlContent(`id: new-item
name: New Catalog Item
description: Description of the service
category: infrastructure
icon: settings

inputs:
  - id: example_field
    component: TextInput
    label: Example field
    required: true

# approval:
#   required: false

executor:
  type: manual
  parameters_map: {}
`);
    setEditSlug(null);
    setErrors([]);
    setView('create');
  };

  const handleValidate = async () => {
    setErrors([]);
    try {
      const result = await api.validateCatalogYaml(yamlContent);
      if (!result.isValid) {
        setErrors(result.errors);
      } else {
        setErrors([]);
      }
      return result.isValid;
    } catch (err) {
      setErrors([(err as Error).message]);
      return false;
    }
  };

  const handleSave = async () => {
    setSaving(true);
    setErrors([]);
    try {
      const valid = await handleValidate();
      if (!valid) { setSaving(false); return; }

      if (view === 'create') {
        await api.createCatalogItem(yamlContent);
      } else if (editSlug) {
        await api.updateCatalogItem(editSlug, yamlContent);
      }
      setView('list');
      loadItems();
    } catch (err) {
      setErrors([(err as Error).message]);
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (slug: string) => {
    try {
      await api.deleteCatalogItem(slug);
      setDeleteConfirm(null);
      loadItems();
    } catch {
      // ignore
    }
  };

  const categoryBadge = (category: string) => {
    const colors: Record<string, string> = {
      'ci-cd': 'var(--accent)',
      infrastructure: 'var(--info)',
      access: 'var(--warning)',
      data: 'var(--success)',
      general: 'var(--text-muted)',
    };
    return (
      <span
        className="text-[11px] font-medium px-2 py-0.5 rounded-full"
        style={{
          backgroundColor: `color-mix(in srgb, ${colors[category] || 'var(--text-muted)'} 15%, transparent)`,
          color: colors[category] || 'var(--text-muted)',
        }}
      >
        {category}
      </span>
    );
  };

  if (view === 'edit' || view === 'create') {
    return (
      <div
        className="rounded-xl border p-5 space-y-4"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div className="flex items-center justify-between">
          <div>
            <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
              {view === 'create' ? 'New Catalog Item' : `Edit: ${editSlug}`}
            </h2>
            <p className="text-[12px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
              Define the catalog item in YAML format
            </p>
          </div>
        </div>

        {errors.length > 0 && (
          <div
            className="rounded-lg border p-3 space-y-1"
            style={{ borderColor: 'var(--danger)', backgroundColor: 'color-mix(in srgb, var(--danger) 8%, transparent)' }}
          >
            <div className="flex items-center gap-1.5 text-[12px] font-medium" style={{ color: 'var(--danger)' }}>
              <AlertCircle size={14} /> Validation errors
            </div>
            {errors.map((e, i) => (
              <p key={i} className="text-[12px]" style={{ color: 'var(--danger)' }}>
                {e}
              </p>
            ))}
          </div>
        )}

        <textarea
          value={yamlContent}
          onChange={(e) => setYamlContent(e.target.value)}
          className="w-full rounded-lg border p-3 text-[13px] leading-relaxed resize-y"
          style={{
            fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
            minHeight: '400px',
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
          spellCheck={false}
        />

        <div className="flex items-center gap-2">
          <button
            onClick={handleValidate}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg border transition-colors hover:opacity-90"
            style={{ borderColor: 'var(--border-color)', color: 'var(--text-primary)' }}
          >
            <Check size={14} /> Validate
          </button>
          <button
            onClick={handleSave}
            disabled={saving}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {saving ? 'Saving...' : 'Save'}
          </button>
          <button
            onClick={() => { setView('list'); setErrors([]); }}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg border transition-colors hover:opacity-90"
            style={{ borderColor: 'var(--border-color)', color: 'var(--text-muted)' }}
          >
            <X size={14} /> Cancel
          </button>
        </div>
      </div>
    );
  }

  return (
    <div
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            Service Catalog
          </h2>
          <p className="text-[12px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
            Manage catalog items available for service requests
          </p>
        </div>
        <button
          onClick={handleCreate}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          <Plus size={14} /> Add Item
        </button>
      </div>

      {loading ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>Loading...</p>
      ) : items.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>No catalog items found.</p>
      ) : (
        <div className="space-y-1">
          {items.map((item) => (
            <div
              key={item.slug}
              className="flex items-center justify-between px-3 py-2.5 rounded-lg hover:bg-opacity-50 transition-colors"
              style={{ backgroundColor: item.isActive ? 'transparent' : 'color-mix(in srgb, var(--text-muted) 5%, transparent)' }}
            >
              <div className="flex items-center gap-3 min-w-0 flex-1">
                <button
                  onClick={() => handleToggle(item.slug, item.isActive)}
                  title={item.isActive ? 'Disable' : 'Enable'}
                  className="shrink-0"
                  style={{ color: item.isActive ? 'var(--success)' : 'var(--text-muted)' }}
                >
                  {item.isActive ? <ToggleRight size={20} /> : <ToggleLeft size={20} />}
                </button>
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span
                      className="text-[13px] font-medium truncate"
                      style={{ color: item.isActive ? 'var(--text-primary)' : 'var(--text-muted)' }}
                    >
                      {item.name}
                    </span>
                    {categoryBadge(item.category)}
                    {!item.isActive && (
                      <span
                        className="text-[11px] font-medium px-2 py-0.5 rounded-full"
                        style={{
                          backgroundColor: 'color-mix(in srgb, var(--text-muted) 15%, transparent)',
                          color: 'var(--text-muted)',
                        }}
                      >
                        disabled
                      </span>
                    )}
                  </div>
                  <p
                    className="text-[11px] truncate"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    {item.slug}
                    {item.description ? ` — ${item.description}` : ''}
                  </p>
                </div>
              </div>
              <div className="flex items-center gap-1 shrink-0 ml-2">
                <button
                  onClick={() => handleEdit(item.slug)}
                  className="p-1.5 rounded-md transition-colors hover:bg-opacity-50"
                  style={{ color: 'var(--text-muted)' }}
                  title="Edit"
                >
                  <Pencil size={14} />
                </button>
                {deleteConfirm === item.slug ? (
                  <div className="flex items-center gap-1">
                    <button
                      onClick={() => handleDelete(item.slug)}
                      className="text-[11px] font-medium px-2 py-1 rounded-md"
                      style={{ backgroundColor: 'var(--danger)', color: 'white' }}
                    >
                      Confirm
                    </button>
                    <button
                      onClick={() => setDeleteConfirm(null)}
                      className="p-1 rounded-md"
                      style={{ color: 'var(--text-muted)' }}
                    >
                      <X size={14} />
                    </button>
                  </div>
                ) : (
                  <button
                    onClick={() => setDeleteConfirm(item.slug)}
                    className="p-1.5 rounded-md transition-colors hover:bg-opacity-50"
                    style={{ color: 'var(--text-muted)' }}
                    title="Delete"
                  >
                    <Trash2 size={14} />
                  </button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
