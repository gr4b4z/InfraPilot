import { useState, useRef, useEffect } from 'react';
import { User } from 'lucide-react';
import type { ComponentProps } from './A2UIRenderer';

const MOCK_USERS = [
  { id: 'user-1', name: 'Alice Chen', email: 'alice@swo.dev', initials: 'AC' },
  { id: 'user-2', name: 'Bob Martinez', email: 'bob@swo.dev', initials: 'BM' },
  { id: 'user-3', name: 'Carol Williams', email: 'carol@swo.dev', initials: 'CW' },
  { id: 'user-4', name: 'David Kim', email: 'david@swo.dev', initials: 'DK' },
  { id: 'user-5', name: 'Eva Novak', email: 'eva@swo.dev', initials: 'EN' },
];

export function UserPicker({ component, value, error, onChange, readOnly }: ComponentProps) {
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const wrapperRef = useRef<HTMLDivElement>(null);

  const filtered = MOCK_USERS.filter(
    (u) =>
      u.name.toLowerCase().includes(query.toLowerCase()) ||
      u.email.toLowerCase().includes(query.toLowerCase()),
  );

  const selectedUser = MOCK_USERS.find((u) => u.id === value);

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (wrapperRef.current && !wrapperRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

  function select(id: string) {
    onChange(id);
    setQuery('');
    setOpen(false);
  }

  return (
    <div ref={wrapperRef}>
      <label className="block text-sm font-medium mb-1.5" style={{ color: 'var(--text-primary)' }}>
        {component.label}
        {component.required && <span style={{ color: 'var(--accent)' }}> *</span>}
      </label>
      <div className="relative">
        <User
          className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4"
          style={{ color: 'var(--text-muted)' }}
        />
        <input
          type="text"
          value={open ? query : (selectedUser?.name || (value as string) || '')}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpen(true);
          }}
          onFocus={() => setOpen(true)}
          placeholder={component.placeholder || 'Search users...'}
          disabled={readOnly}
          className={`w-full pl-9 pr-3 py-2 text-sm rounded-lg border outline-none transition-colors ${
            error ? 'border-red-500' : 'focus:border-[var(--accent)]'
          }`}
          style={{
            backgroundColor: 'var(--bg-secondary)',
            borderColor: error ? undefined : 'var(--border-color)',
            color: 'var(--text-primary)',
          }}
        />
      </div>
      {open && !readOnly && (
        <div
          className="mt-1 rounded-lg border shadow-lg max-h-48 overflow-y-auto"
          style={{
            backgroundColor: 'var(--bg-secondary)',
            borderColor: 'var(--border-color)',
          }}
        >
          {filtered.length === 0 ? (
            <div className="px-3 py-2 text-sm" style={{ color: 'var(--text-muted)' }}>
              No users found
            </div>
          ) : (
            filtered.map((u) => (
              <button
                key={u.id}
                type="button"
                onClick={() => select(u.id)}
                className="w-full text-left px-3 py-2 text-sm flex items-center gap-2.5 hover:opacity-80 transition-opacity"
                style={{
                  color: u.id === value ? 'var(--accent)' : 'var(--text-primary)',
                  backgroundColor: u.id === value ? 'var(--bg-tertiary)' : undefined,
                }}
              >
                <span
                  className="flex-shrink-0 w-7 h-7 rounded-full flex items-center justify-center text-xs font-medium text-white"
                  style={{ backgroundColor: 'var(--color-swo-purple)' }}
                >
                  {u.initials}
                </span>
                <span className="flex flex-col">
                  <span className="font-medium">{u.name}</span>
                  <span className="text-xs" style={{ color: 'var(--text-muted)' }}>{u.email}</span>
                </span>
              </button>
            ))
          )}
        </div>
      )}
      {error && <p className="mt-1 text-xs text-red-500">{error}</p>}
    </div>
  );
}
