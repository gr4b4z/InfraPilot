import { useEffect, useRef, useState } from 'react';
import { GripVertical, Plus, Trash2, Check, ChevronDown, Wand2 } from 'lucide-react';
import { useSettingsStore, type EnvironmentConfig } from '@/stores/settingsStore';
import { ENV_COLOR_PRESETS, autoEnvColor, envColorStyles, normalizeHexColor } from '@/lib/envColor';
import { AnchoredPopover } from '@/components/ui/AnchoredPopover';

export function EnvironmentsSettings() {
  const { environments, setEnvironments } = useSettingsStore();
  const [items, setItems] = useState<EnvironmentConfig[]>(environments);
  const [saved, setSaved] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dragIndex, setDragIndex] = useState<number | null>(null);
  // Index of the row whose colour picker is open, or null. Only one at a time.
  const [pickerIndex, setPickerIndex] = useState<number | null>(null);

  useEffect(() => {
    setItems(environments);
  }, [environments]);

  const save = async () => {
    const cleaned = items
      .filter((i) => i.key.trim() !== '')
      .map((i) => ({
        key: i.key.trim(),
        displayName: i.displayName.trim(),
        // Normalise here too so a half-typed hex never round-trips as a broken colour; the
        // server applies the same rule, and null means "derive from the key".
        color: normalizeHexColor(i.color),
        isProduction: i.isProduction ?? false,
      }));
    setSaving(true);
    setError(null);
    try {
      await setEnvironments(cleaned);
      setItems(cleaned);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  const updateItem = (index: number, field: keyof EnvironmentConfig, value: string | boolean | null) => {
    setItems((prev) => prev.map((item, i) => (i === index ? { ...item, [field]: value } : item)));
  };
  const removeItem = (index: number) => {
    setItems((prev) => prev.filter((_, i) => i !== index));
    setPickerIndex(null);
  };
  const addItem = () => setItems((prev) => [...prev, { key: '', displayName: '', color: null }]);

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
    setPickerIndex(null);
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
          Define the environments and their display order. Drag to reorder. The colour marks every
          item targeting that environment — promotions, rollbacks, deployment activity. "Prod"
          marks production stages — the environments executive analytics report on; several may be
          marked (multi-region). With none marked, the last environment in the list is assumed.
        </p>
      </div>

      {/* Five columns — two of them free-text — need ~520px before the inputs stop being usable,
          so on a narrow screen the editor scrolls sideways rather than compressing. The colour
          picker escapes this container through a portal (see ColorCell) so the scroll box can't
          clip it. */}
      <div className="overflow-x-auto">
      <div className="space-y-1.5 min-w-[520px]">
        <div
          className="grid grid-cols-[28px_1fr_1fr_150px_44px_32px] gap-2 px-1 text-[11px] font-medium uppercase tracking-wider"
          style={{ color: 'var(--text-muted)' }}
        >
          <span />
          <span>Key</span>
          <span>Display Name</span>
          <span>Colour</span>
          <span title="Production stage">Prod</span>
          <span />
        </div>

        {items.map((item, index) => (
          <div
            key={index}
            draggable
            onDragStart={() => handleDragStart(index)}
            onDragOver={(e) => handleDragOver(e, index)}
            onDragEnd={handleDragEnd}
            className="grid grid-cols-[28px_1fr_1fr_150px_44px_32px] gap-2 items-center rounded-lg p-1.5 transition-colors"
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
              className="min-w-0 px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
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
              className="min-w-0 px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
              }}
            />
            <ColorCell
              env={item}
              open={pickerIndex === index}
              onToggle={() => setPickerIndex(pickerIndex === index ? null : index)}
              onClose={() => setPickerIndex(null)}
              onChange={(color) => updateItem(index, 'color', color)}
            />
            <label
              className="flex items-center justify-center cursor-pointer"
              title="Production stage — executive analytics report on this environment"
            >
              <input
                type="checkbox"
                checked={item.isProduction ?? false}
                onChange={(e) => updateItem(index, 'isProduction', e.target.checked)}
                className="accent-[var(--accent)]"
                aria-label={`Mark ${item.key || 'environment'} as production stage`}
              />
            </label>
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
          disabled={saving}
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

/**
 * Colour cell for one environment row.
 *
 * The trigger is a live preview of the badge that will appear across the app, so the admin
 * picks against the real thing rather than a naked swatch. An environment with no explicit
 * colour still previews — in its auto-derived colour — and the picker offers "Auto" to
 * return to it.
 */
function ColorCell({
  env,
  open,
  onToggle,
  onClose,
  onChange,
}: {
  env: EnvironmentConfig;
  open: boolean;
  onToggle: () => void;
  onClose: () => void;
  onChange: (color: string | null) => void;
}) {
  const anchorRef = useRef<HTMLButtonElement>(null);
  const explicit = normalizeHexColor(env.color);
  const resolved = explicit ?? autoEnvColor(env.key);
  const styles = envColorStyles(resolved);
  const label = env.displayName.trim() || env.key.trim() || 'Environment';

  // Outside-click / Escape / scroll dismissal all come from AnchoredPopover — a picker left open
  // while the admin edits another row would obscure it, and there's no explicit "done" action.
  return (
    <div>
      <button
        ref={anchorRef}
        type="button"
        onClick={onToggle}
        aria-haspopup="dialog"
        aria-expanded={open}
        title={explicit ? `Colour ${explicit}` : `Auto colour ${resolved} (derived from the key)`}
        className="w-full flex items-center justify-between gap-1.5 px-2 py-1.5 rounded-lg border text-[13px] transition-colors hover:border-[var(--border-strong)]"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
      >
        <span
          className="inline-flex items-center gap-1.5 min-w-0 font-semibold rounded-full px-2 py-0.5"
          style={{
            fontSize: 11,
            color: styles.fg,
            backgroundColor: styles.bg,
            border: `1px solid ${styles.border}`,
          }}
        >
          <span
            style={{
              width: 6,
              height: 6,
              borderRadius: '50%',
              backgroundColor: styles.solid,
              flexShrink: 0,
            }}
          />
          <span className="truncate">{label}</span>
        </span>
        <ChevronDown size={13} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
      </button>

      {open && (
        <AnchoredPopover
          anchorRef={anchorRef}
          onClose={onClose}
          align="right"
          width={208}
          ariaLabel="Pick environment colour"
          className="p-3 space-y-3"
          style={{ backgroundColor: 'var(--bg-elevated)', boxShadow: 'var(--shadow-lg)' }}
        >
          <div className="grid grid-cols-7 gap-1.5">
            {ENV_COLOR_PRESETS.map((preset) => {
              const selected = explicit === preset.value;
              return (
                <button
                  key={preset.value}
                  type="button"
                  title={preset.name}
                  aria-label={preset.name}
                  aria-pressed={selected}
                  onClick={() => onChange(preset.value)}
                  className="flex items-center justify-center rounded-md transition-transform hover:scale-110"
                  style={{
                    width: 22,
                    height: 22,
                    backgroundColor: preset.value,
                    outline: selected ? '2px solid var(--text-primary)' : 'none',
                    outlineOffset: 1,
                  }}
                >
                  {selected && <Check size={12} color="#fff" strokeWidth={3} />}
                </button>
              );
            })}
          </div>

          <div className="flex items-center gap-1.5">
            <input
              type="color"
              value={resolved}
              onChange={(e) => onChange(e.target.value.toLowerCase())}
              aria-label="Custom colour"
              className="rounded border cursor-pointer"
              style={{
                width: 28,
                height: 28,
                padding: 2,
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
              }}
            />
            <input
              type="text"
              // Free text while typing (so a partial "#2f" isn't swallowed); normalisation
              // happens on save and on the server.
              value={env.color ?? ''}
              onChange={(e) => onChange(e.target.value === '' ? null : e.target.value)}
              placeholder={resolved}
              spellCheck={false}
              className="flex-1 min-w-0 px-2 py-1.5 rounded-lg border text-[12px] font-mono outline-none transition-colors focus:border-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
              }}
            />
          </div>

          <button
            type="button"
            onClick={() => onChange(null)}
            disabled={!explicit}
            className="w-full inline-flex items-center justify-center gap-1.5 px-2 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80 disabled:opacity-40"
            style={{ color: 'var(--text-secondary)', backgroundColor: 'var(--bg-secondary)' }}
            title="Derive the colour from the environment key"
          >
            <Wand2 size={12} />
            Auto
          </button>
        </AnchoredPopover>
      )}
    </div>
  );
}
