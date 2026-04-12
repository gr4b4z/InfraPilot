import type { ComponentProps } from './A2UIRenderer';

const DEFAULT_ENVIRONMENTS = [
  { id: 'dev', label: 'Development' },
  { id: 'staging', label: 'Staging' },
  { id: 'production', label: 'Production' },
];

export function EnvironmentSelector({ component, value, error, onChange, readOnly }: ComponentProps) {
  const environments = component.options?.length ? component.options : DEFAULT_ENVIRONMENTS;
  const selected = (value as string) || '';

  return (
    <div>
      <label className="block text-sm font-medium mb-1.5" style={{ color: 'var(--text-primary)' }}>
        {component.label}
        {component.required && <span style={{ color: 'var(--accent)' }}> *</span>}
      </label>
      <div className="flex flex-wrap gap-2">
        {environments.map((env) => {
          const isSelected = selected === env.id;
          return (
            <button
              key={env.id}
              type="button"
              onClick={() => !readOnly && onChange(env.id)}
              disabled={readOnly}
              className={`px-4 py-1.5 text-sm font-medium rounded-full border transition-colors ${
                readOnly ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'
              }`}
              style={{
                backgroundColor: isSelected ? 'var(--accent)' : 'var(--bg-secondary)',
                borderColor: isSelected ? 'var(--accent)' : 'var(--border-color)',
                color: isSelected ? '#ffffff' : 'var(--text-primary)',
              }}
            >
              {env.label}
            </button>
          );
        })}
      </div>
      {error && <p className="mt-1 text-xs text-red-500">{error}</p>}
    </div>
  );
}
