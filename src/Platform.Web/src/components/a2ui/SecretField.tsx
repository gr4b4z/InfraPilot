import { useState } from 'react';
import { Eye, EyeOff } from 'lucide-react';
import type { ComponentProps } from './A2UIRenderer';

export function SecretField({ component, value, error, onChange, readOnly }: ComponentProps) {
  const [visible, setVisible] = useState(false);

  return (
    <div>
      <label className="block text-sm font-medium mb-1.5" style={{ color: 'var(--text-primary)' }}>
        {component.label}
        {component.required && <span style={{ color: 'var(--accent)' }}> *</span>}
      </label>
      <div className="relative">
        <input
          type={visible ? 'text' : 'password'}
          value={(value as string) || ''}
          onChange={(e) => onChange(e.target.value)}
          placeholder={component.placeholder}
          disabled={readOnly}
          className={`w-full px-3 py-2 pr-10 text-sm rounded-lg border outline-none transition-colors ${
            error ? 'border-red-500' : 'focus:border-[var(--accent)]'
          }`}
          style={{
            backgroundColor: 'var(--bg-secondary)',
            borderColor: error ? undefined : 'var(--border-color)',
            color: 'var(--text-primary)',
          }}
        />
        <button
          type="button"
          onClick={() => setVisible((v) => !v)}
          className="absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded hover:opacity-80 transition-opacity"
          style={{ color: 'var(--text-muted)' }}
          tabIndex={-1}
        >
          {visible ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
        </button>
      </div>
      {error && <p className="mt-1 text-xs text-red-500">{error}</p>}
    </div>
  );
}
