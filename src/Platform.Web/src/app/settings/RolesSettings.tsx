import { useEffect, useState } from 'react';
import { Plus, Trash2, Check } from 'lucide-react';
import { useSettingsStore, type RoleConfig } from '@/stores/settingsStore';

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

export function RolesSettings() {
  const { roles, setRoles } = useSettingsStore();
  const [items, setItems] = useState<RoleConfig[]>(roles);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    setItems(roles);
  }, [roles]);

  const save = () => {
    const cleaned = items
      .map((r) => ({
        key: canonicaliseRoleKey(r.key),
        displayName: r.displayName.trim(),
      }))
      .filter((r) => r.key.length > 0);
    setRoles(cleaned);
    setItems(cleaned);
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  };

  const updateItem = (index: number, field: keyof RoleConfig, value: string) => {
    setItems((prev) => prev.map((item, i) => (i === index ? { ...item, [field]: value } : item)));
  };
  const removeItem = (index: number) => setItems((prev) => prev.filter((_, i) => i !== index));
  const addItem = () => setItems((prev) => [...prev, { key: '', displayName: '' }]);

  return (
    <section
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

        {items.map((item, index) => (
          <div key={index} className="grid grid-cols-[1fr_1fr_32px] gap-2 items-center rounded-lg p-1.5">
            <input
              type="text"
              value={item.key}
              onChange={(e) => updateItem(index, 'key', e.target.value)}
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
              onChange={(e) => updateItem(index, 'displayName', e.target.value)}
              placeholder="e.g. Triggered by"
              className="px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
              }}
            />
            <button
              onClick={() => removeItem(index)}
              className="p-1 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              <Trash2 size={14} />
            </button>
          </div>
        ))}
      </div>

      <button
        onClick={addItem}
        className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
        style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
      >
        <Plus size={14} />
        Add Role
      </button>

      <div className="flex items-center gap-3 pt-2 border-t" style={{ borderColor: 'var(--border-color)' }}>
        <button
          onClick={save}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          Save
        </button>
        {saved && (
          <span className="inline-flex items-center gap-1 text-[13px]" style={{ color: 'var(--success)' }}>
            <Check size={14} /> Saved
          </span>
        )}
      </div>
    </section>
  );
}
