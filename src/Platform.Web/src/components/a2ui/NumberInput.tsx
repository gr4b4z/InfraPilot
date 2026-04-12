import type { ComponentProps } from './A2UIRenderer';

export function NumberInput({ component, value, error, onChange, readOnly }: ComponentProps) {
  return (
    <div>
      <label className="block text-sm font-medium mb-1.5" style={{ color: 'var(--text-primary)' }}>
        {component.label}
        {component.required && <span style={{ color: 'var(--accent)' }}> *</span>}
      </label>
      <input
        type="number"
        value={(value as number) ?? ''}
        onChange={(e) => onChange(e.target.valueAsNumber)}
        min={component.min}
        max={component.max}
        step={component.step}
        disabled={readOnly}
        className={`w-full px-3 py-2 text-sm rounded-lg border outline-none transition-colors ${
          error ? 'border-red-500' : 'focus:border-[var(--accent)]'
        }`}
        style={{
          backgroundColor: 'var(--bg-secondary)',
          borderColor: error ? undefined : 'var(--border-color)',
          color: 'var(--text-primary)',
        }}
      />
      {error && <p className="mt-1 text-xs text-red-500">{error}</p>}
    </div>
  );
}
