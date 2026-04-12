import type { ComponentProps } from './A2UIRenderer';

export function Toggle({ component, value, onChange, readOnly }: ComponentProps) {
  const isOn = value === true || value === 'true';

  return (
    <div className="flex items-center justify-between py-1">
      <label className="text-sm font-medium" style={{ color: 'var(--text-primary)' }}>
        {component.label}
      </label>
      <button
        type="button"
        role="switch"
        aria-checked={isOn}
        onClick={() => !readOnly && onChange(!isOn)}
        className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
          readOnly ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'
        }`}
        style={{ backgroundColor: isOn ? 'var(--accent)' : 'var(--border-strong)' }}
      >
        <span
          className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
            isOn ? 'translate-x-6' : 'translate-x-1'
          }`}
        />
      </button>
    </div>
  );
}
