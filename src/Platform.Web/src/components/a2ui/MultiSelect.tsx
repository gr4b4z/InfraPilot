import type { ComponentProps } from './A2UIRenderer';

export function MultiSelect({ component, value, error, onChange, readOnly }: ComponentProps) {
  const selected = (value as string[]) || [];

  function toggle(optionId: string) {
    if (readOnly) return;
    const next = selected.includes(optionId)
      ? selected.filter((v) => v !== optionId)
      : [...selected, optionId];
    onChange(next);
  }

  return (
    <div>
      <label className="block text-sm font-medium mb-1.5" style={{ color: 'var(--text-primary)' }}>
        {component.label}
        {component.required && <span style={{ color: 'var(--accent)' }}> *</span>}
      </label>
      <div
        className="rounded-lg border p-3 space-y-2"
        style={{
          backgroundColor: 'var(--bg-secondary)',
          borderColor: error ? '#EF4444' : 'var(--border-color)',
        }}
      >
        {component.options?.map((opt) => {
          const checked = selected.includes(opt.id);
          return (
            <label
              key={opt.id}
              className={`flex items-center gap-2.5 text-sm cursor-pointer ${
                readOnly ? 'opacity-50 cursor-not-allowed' : ''
              }`}
              style={{ color: 'var(--text-primary)' }}
            >
              <span
                className={`flex-shrink-0 w-4.5 h-4.5 rounded border flex items-center justify-center transition-colors ${
                  checked ? 'border-transparent' : ''
                }`}
                style={{
                  backgroundColor: checked ? 'var(--accent)' : 'transparent',
                  borderColor: checked ? 'var(--accent)' : 'var(--border-strong)',
                }}
              >
                {checked && (
                  <svg className="w-3 h-3 text-white" viewBox="0 0 12 12" fill="none">
                    <path d="M2.5 6L5 8.5L9.5 3.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                )}
              </span>
              <input
                type="checkbox"
                checked={checked}
                onChange={() => toggle(opt.id)}
                disabled={readOnly}
                className="sr-only"
              />
              {opt.label}
            </label>
          );
        })}
        {(!component.options || component.options.length === 0) && (
          <p className="text-xs" style={{ color: 'var(--text-muted)' }}>No options available</p>
        )}
      </div>
      {error && <p className="mt-1 text-xs text-red-500">{error}</p>}
    </div>
  );
}
