import { useState, useEffect, useRef } from 'react';
import { X } from 'lucide-react';
import { api, type PromotionPolicyGroupRef } from '@/lib/api';
import { inputClass, inputStyle } from './formStyles';

/**
 * Directory pickers shared by the promotion and rollback policy editors. Both surfaces configure
 * approvers out of the same vocabulary — AD groups plus explicit user emails — matched by the same
 * server-side rules, so they pick them with the same controls.
 *
 * Moved here verbatim from PromotionSettings when rollbacks gained their own policy editor; the
 * debounce and fallback behaviour is unchanged.
 */

/** Removable chip row shared by the group/user pickers. `label(v)` resolves a display label. */
function ChipRow({
  values,
  label,
  onRemove,
}: {
  values: string[];
  label: (v: string) => string;
  onRemove: (v: string) => void;
}) {
  if (values.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-1.5">
      {values.map((v) => (
        <span
          key={v}
          className="inline-flex items-center gap-1 text-[12px] font-medium px-2.5 py-1 rounded-full border"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-secondary)',
            color: 'var(--text-primary)',
          }}
          title={label(v) !== v ? v : undefined}
        >
          {label(v)}
          <button
            type="button"
            onClick={() => onRemove(v)}
            className="hover:opacity-80 transition-colors"
            style={{ color: 'var(--text-muted)' }}
          >
            <X size={12} />
          </button>
        </span>
      ))}
    </div>
  );
}

/**
 * Typeahead picker for user emails. Debounced search against /promotions/users/search; selecting
 * a hit adds the user's *email* to `values` (that's what the gate matches on). Falls back to
 * manual entry of an unmatched email so local-dev / edge cases still work.
 */
export function UserPicker({
  values,
  onChange,
  placeholder = 'Search directory (name or email)...',
}: {
  values: string[];
  onChange: (next: string[]) => void;
  placeholder?: string;
}) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<
    Array<{ id: string; displayName: string; email: string }>
  >([]);
  const [searching, setSearching] = useState(false);
  const [focused, setFocused] = useState(false);
  const blurTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) {
      setResults([]);
      return;
    }
    let cancelled = false;
    setSearching(true);
    const timer = setTimeout(async () => {
      try {
        const res = await api.searchPromotionUsers(q);
        if (!cancelled) setResults(res.users || []);
      } catch {
        if (!cancelled) setResults([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [query]);

  const add = (email: string) => {
    const v = email.trim();
    if (!v || values.includes(v)) return;
    onChange([...values, v]);
    setQuery('');
    setResults([]);
  };

  const remove = (v: string) => onChange(values.filter((x) => x !== v));

  const trimmed = query.trim();
  const manualOk = trimmed.includes('@') && trimmed.includes('.');

  return (
    <div className="space-y-1.5">
      <ChipRow values={values} label={(v) => v} onRemove={remove} />
      <div className="relative">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onFocus={() => setFocused(true)}
          onBlur={() => {
            blurTimer.current = setTimeout(() => setFocused(false), 150);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              if (results.length === 0 && manualOk) add(trimmed);
            }
          }}
          placeholder={placeholder}
          className={`${inputClass} w-full`}
          style={inputStyle}
        />
        {focused && trimmed.length >= 2 && (
          <div
            className="absolute z-20 mt-1 top-full left-0 right-0 max-h-48 overflow-y-auto rounded-lg border shadow-lg"
            style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
            onMouseDown={() => {
              if (blurTimer.current) clearTimeout(blurTimer.current);
            }}
          >
            {searching && (
              <div className="px-3 py-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
                Searching...
              </div>
            )}
            {!searching && results.length === 0 && (
              <button
                type="button"
                onClick={() => add(trimmed)}
                disabled={!manualOk}
                className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80 disabled:opacity-40"
                style={{ color: 'var(--text-primary)' }}
              >
                <span className="font-medium">Use &ldquo;{trimmed}&rdquo; as email</span>
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  {manualOk ? 'No directory matches — added as-is.' : 'No directory matches.'}
                </span>
              </button>
            )}
            {!searching &&
              results.map((u) => (
                <button
                  key={u.id}
                  type="button"
                  onClick={() => add(u.email)}
                  className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-primary)' }}
                >
                  <span className="font-medium truncate">{u.displayName}</span>
                  <span className="text-[11px] truncate" style={{ color: 'var(--text-muted)' }}>
                    {u.email}
                  </span>
                </button>
              ))}
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * Typeahead picker for AD groups. Debounced search against /promotions/groups/search; selecting a
 * hit stores both the group's object *id* (the approval-time Graph lookup keys off the id) and its
 * display *name* as `{ id, name }`. The chip label shows the name, so a saved policy reloads showing
 * group names rather than raw object GUIDs. Unmatched manual entries are stored as `{id, name}` with
 * the typed text used for both. Falls back to manual entry too.
 */
export function GroupPicker({
  values,
  onChange,
}: {
  values: PromotionPolicyGroupRef[];
  onChange: (next: PromotionPolicyGroupRef[]) => void;
}) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Array<{ id: string; displayName: string }>>([]);
  const [searching, setSearching] = useState(false);
  const [focused, setFocused] = useState(false);
  const blurTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) {
      setResults([]);
      return;
    }
    let cancelled = false;
    setSearching(true);
    const timer = setTimeout(async () => {
      try {
        const res = await api.searchPromotionGroups(q);
        if (!cancelled) setResults(res.groups || []);
      } catch {
        if (!cancelled) setResults([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [query]);

  const add = (id: string, displayName?: string) => {
    const trimmedId = id.trim();
    if (!trimmedId || values.some((g) => g.id === trimmedId)) return;
    onChange([...values, { id: trimmedId, name: (displayName ?? trimmedId).trim() }]);
    setQuery('');
    setResults([]);
  };

  const remove = (id: string) => onChange(values.filter((g) => g.id !== id));

  const trimmed = query.trim();

  return (
    <div className="space-y-1.5">
      <ChipRow
        values={values.map((g) => g.id)}
        label={(id) => values.find((g) => g.id === id)?.name ?? id}
        onRemove={remove}
      />
      <div className="relative">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onFocus={() => setFocused(true)}
          onBlur={() => {
            blurTimer.current = setTimeout(() => setFocused(false), 150);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              if (results.length === 0 && trimmed) add(trimmed);
            }
          }}
          placeholder="Search groups..."
          className={`${inputClass} w-full`}
          style={inputStyle}
        />
        {focused && trimmed.length >= 2 && (
          <div
            className="absolute z-20 mt-1 top-full left-0 right-0 max-h-48 overflow-y-auto rounded-lg border shadow-lg"
            style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
            onMouseDown={() => {
              if (blurTimer.current) clearTimeout(blurTimer.current);
            }}
          >
            {searching && (
              <div className="px-3 py-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
                Searching...
              </div>
            )}
            {!searching && results.length === 0 && (
              <button
                type="button"
                onClick={() => add(trimmed)}
                className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
                style={{ color: 'var(--text-primary)' }}
              >
                <span className="font-medium">Use &ldquo;{trimmed}&rdquo; as group</span>
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  No directory matches — added as-is.
                </span>
              </button>
            )}
            {!searching &&
              results.map((g) => (
                <button
                  key={g.id}
                  type="button"
                  onClick={() => add(g.id, g.displayName)}
                  className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-primary)' }}
                >
                  <span className="font-medium truncate">{g.displayName}</span>
                </button>
              ))}
          </div>
        )}
      </div>
    </div>
  );
}
