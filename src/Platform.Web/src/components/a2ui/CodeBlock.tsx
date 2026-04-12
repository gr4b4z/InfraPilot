import type { ComponentProps } from './A2UIRenderer';

export function CodeBlock({ component, value, error, onChange, readOnly }: ComponentProps) {
  return (
    <div>
      <div className="flex items-center justify-between mb-1.5">
        <label className="block text-sm font-medium" style={{ color: 'var(--text-primary)' }}>
          {component.label}
          {component.required && <span style={{ color: 'var(--accent)' }}> *</span>}
        </label>
        {component.language && (
          <span
            className="text-xs px-2 py-0.5 rounded-full font-medium"
            style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-muted)' }}
          >
            {component.language}
          </span>
        )}
      </div>
      <textarea
        value={(value as string) || ''}
        onChange={(e) => onChange(e.target.value)}
        placeholder={component.placeholder}
        disabled={readOnly}
        rows={10}
        spellCheck={false}
        className={`w-full px-4 py-3 text-sm rounded-lg border outline-none transition-colors font-mono leading-relaxed resize-y ${
          error ? 'border-red-500' : 'focus:border-[var(--accent)]'
        }`}
        style={{
          backgroundColor: 'var(--bg-tertiary)',
          borderColor: error ? undefined : 'var(--border-color)',
          color: 'var(--text-primary)',
          tabSize: 2,
        }}
      />
      {error && <p className="mt-1 text-xs text-red-500">{error}</p>}
    </div>
  );
}
