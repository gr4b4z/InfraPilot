import { useEffect, useState } from 'react';
import { GripVertical, Plus, Trash2, Check } from 'lucide-react';
import { useSettingsStore, type EnvironmentConfig } from '@/stores/settingsStore';

export function EnvironmentsSettings() {
  const { environments, setEnvironments } = useSettingsStore();
  const [items, setItems] = useState<EnvironmentConfig[]>(environments);
  const [saved, setSaved] = useState(false);
  const [dragIndex, setDragIndex] = useState<number | null>(null);

  useEffect(() => {
    setItems(environments);
  }, [environments]);

  const save = () => {
    const cleaned = items.filter((i) => i.key.trim() !== '');
    setEnvironments(cleaned);
    setItems(cleaned);
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  };

  const updateItem = (index: number, field: keyof EnvironmentConfig, value: string) => {
    setItems((prev) => prev.map((item, i) => (i === index ? { ...item, [field]: value } : item)));
  };
  const removeItem = (index: number) => setItems((prev) => prev.filter((_, i) => i !== index));
  const addItem = () => setItems((prev) => [...prev, { key: '', displayName: '' }]);

  const handleDragStart = (index: number) => setDragIndex(index);
  const handleDragOver = (e: React.DragEvent, index: number) => {
    e.preventDefault();
    if (dragIndex === null || dragIndex === index) return;
    setItems((prev) => {
      const next = [...prev];
      const [moved] = next.splice(dragIndex, 1);
      next.splice(index, 0, moved);
      return next;
    });
    setDragIndex(index);
  };
  const handleDragEnd = () => setDragIndex(null);

  return (
    <section
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

        {items.map((item, index) => (
          <div
            key={index}
            draggable
            onDragStart={() => handleDragStart(index)}
            onDragOver={(e) => handleDragOver(e, index)}
            onDragEnd={handleDragEnd}
            className="grid grid-cols-[28px_1fr_1fr_32px] gap-2 items-center rounded-lg p-1.5 transition-colors"
            style={{ backgroundColor: dragIndex === index ? 'var(--accent-muted)' : undefined }}
          >
            <span className="cursor-grab flex items-center justify-center" style={{ color: 'var(--text-muted)' }}>
              <GripVertical size={14} />
            </span>
            <input
              type="text"
              value={item.key}
              onChange={(e) => updateItem(index, 'key', e.target.value)}
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
              onChange={(e) => updateItem(index, 'displayName', e.target.value)}
              placeholder="e.g. Staging"
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
        Add Environment
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
