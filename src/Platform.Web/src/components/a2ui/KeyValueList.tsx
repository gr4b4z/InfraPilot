import { Plus, Trash2 } from 'lucide-react';
import type { ComponentProps } from './A2UIRenderer';

interface KVPair {
  key: string;
  value: string;
}

export function KeyValueList({ component, value, onChange, readOnly }: ComponentProps) {
  const pairs: KVPair[] = Array.isArray(value) ? value : [];

  const addPair = () => {
    onChange([...pairs, { key: '', value: '' }]);
  };

  const updatePair = (index: number, field: 'key' | 'value', val: string) => {
    const updated = [...pairs];
    updated[index] = { ...updated[index], [field]: val };
    onChange(updated);
  };

  const removePair = (index: number) => {
    onChange(pairs.filter((_, i) => i !== index));
  };

  return (
    <div>
      <label className="block text-sm font-medium mb-1.5" style={{ color: 'var(--text-primary)' }}>
        {component.label}
      </label>
      <div className="space-y-2">
        {pairs.map((pair, i) => (
          <div key={i} className="flex gap-2">
            <input
              type="text"
              placeholder="Key"
              value={pair.key}
              onChange={(e) => updatePair(i, 'key', e.target.value)}
              disabled={readOnly}
              className="flex-1 px-3 py-2 text-sm rounded-lg border outline-none"
              style={{
                backgroundColor: 'var(--bg-secondary)',
                borderColor: 'var(--border-color)',
                color: 'var(--text-primary)',
              }}
            />
            <input
              type="text"
              placeholder="Value"
              value={pair.value}
              onChange={(e) => updatePair(i, 'value', e.target.value)}
              disabled={readOnly}
              className="flex-1 px-3 py-2 text-sm rounded-lg border outline-none"
              style={{
                backgroundColor: 'var(--bg-secondary)',
                borderColor: 'var(--border-color)',
                color: 'var(--text-primary)',
              }}
            />
            {!readOnly && (
              <button onClick={() => removePair(i)} className="p-2 rounded-lg hover:bg-red-500/10 text-red-500">
                <Trash2 size={16} />
              </button>
            )}
          </div>
        ))}
      </div>
      {!readOnly && (
        <button
          onClick={addPair}
          className="mt-2 flex items-center gap-1.5 text-sm font-medium px-3 py-1.5 rounded-lg transition-colors hover:bg-[var(--bg-secondary)]"
          style={{ color: 'var(--accent)' }}
        >
          <Plus size={14} /> Add pair
        </button>
      )}
    </div>
  );
}
