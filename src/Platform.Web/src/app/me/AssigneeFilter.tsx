import { useEffect, useMemo, useRef, useState } from 'react';
import { ChevronDown, Search, User, Users, UserX } from 'lucide-react';
import { api } from '@/lib/api';

/**
 * Picker for narrowing the My-queue list by who's assigned to the candidates.
 *
 * Pure display narrowing — server-side authorisation (group membership, excluded role,
 * not-yet-decided) is unchanged. The "assignee role set" is a server-side configurable
 * subset of participant roles (default: qa, reviewer, assignee) — see the backend
 * PromotionAssigneeRoleSettings service.
 *
 * Self-contained popover; intentionally NOT reusing the a2ui UserPicker. Search input
 * and styling mirror the InlineUserPicker in PromotionDetailPage but with the picker's
 * scope reduced (no role editor — only the user / mode selector).
 */
export type AssigneeFilterValue =
  | { mode: 'all' }
  | { mode: 'me' }
  | { mode: 'unassigned' }
  | { mode: 'person'; email: string; displayName: string };

export function AssigneeFilter({
  value,
  onChange,
}: {
  value: AssigneeFilterValue;
  onChange: (next: AssigneeFilterValue) => void;
}) {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);

  // Close on outside click. Bound only while open so we don't pay for it when not in use.
  useEffect(() => {
    if (!open) return;
    const onDocClick = (e: MouseEvent) => {
      if (!containerRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDocClick);
    return () => document.removeEventListener('mousedown', onDocClick);
  }, [open]);

  const label = useMemo(() => formatLabel(value), [value]);
  const Icon = useMemo(() => iconFor(value), [value]);

  return (
    <div className="relative inline-block" ref={containerRef}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="inline-flex items-center gap-2 rounded-lg border px-3 py-1.5 text-[12px] font-medium transition-opacity hover:opacity-80"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          color: 'var(--text-primary)',
        }}
      >
        <Icon size={13} style={{ color: 'var(--text-muted)' }} />
        <span style={{ color: 'var(--text-muted)' }}>Assignee:</span>
        <span>{label}</span>
        <ChevronDown size={13} style={{ color: 'var(--text-muted)' }} />
      </button>

      {open && (
        <AssigneePopover
          value={value}
          onPick={(next) => {
            onChange(next);
            setOpen(false);
          }}
          onClose={() => setOpen(false)}
        />
      )}
    </div>
  );
}

function AssigneePopover({
  value,
  onPick,
  onClose,
}: {
  value: AssigneeFilterValue;
  onPick: (next: AssigneeFilterValue) => void;
  onClose: () => void;
}) {
  const [showPersonInput, setShowPersonInput] = useState(value.mode === 'person');

  return (
    <div
      className="absolute z-20 mt-1 top-full left-0 rounded-lg border shadow-lg p-1 w-72"
      style={{
        backgroundColor: 'var(--bg-primary)',
        borderColor: 'var(--border-color)',
      }}
    >
      <RadioRow
        label="Anyone"
        sub="No filter."
        Icon={Users}
        selected={value.mode === 'all'}
        onSelect={() => onPick({ mode: 'all' })}
      />
      <RadioRow
        label="Assigned to me"
        sub="Only candidates where I'm a named QA / reviewer / assignee."
        Icon={User}
        selected={value.mode === 'me'}
        onSelect={() => onPick({ mode: 'me' })}
      />
      <RadioRow
        label="Unassigned"
        sub="No one is named in any assignee role."
        Icon={UserX}
        selected={value.mode === 'unassigned'}
        onSelect={() => onPick({ mode: 'unassigned' })}
      />

      <div className="border-t my-1" style={{ borderColor: 'var(--border-color)' }} />

      <button
        type="button"
        onClick={() => setShowPersonInput(true)}
        className="w-full flex items-start gap-2 rounded-md px-2 py-2 text-left transition-opacity hover:opacity-80"
        style={{
          backgroundColor:
            value.mode === 'person' || showPersonInput ? 'var(--bg-secondary)' : 'transparent',
          color: 'var(--text-primary)',
        }}
      >
        <Search size={13} style={{ color: 'var(--text-muted)', marginTop: 2 }} />
        <div className="flex-1 min-w-0">
          <div className="text-[13px] font-medium">Specific person</div>
          <div className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            Search the directory.
          </div>
        </div>
      </button>

      {(showPersonInput || value.mode === 'person') && (
        <div className="px-1 pb-1">
          <PersonSearch
            currentValue={value.mode === 'person' ? value : null}
            onPick={(p) => onPick({ mode: 'person', email: p.email, displayName: p.displayName })}
          />
        </div>
      )}

      <div className="flex justify-end px-1 pb-1">
        <button
          type="button"
          onClick={onClose}
          className="px-2 py-1 rounded-md text-[11px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          Close
        </button>
      </div>
    </div>
  );
}

function RadioRow({
  label,
  sub,
  Icon,
  selected,
  onSelect,
}: {
  label: string;
  sub: string;
  Icon: typeof Users;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      className="w-full flex items-start gap-2 rounded-md px-2 py-2 text-left transition-opacity hover:opacity-80"
      style={{
        backgroundColor: selected ? 'var(--bg-secondary)' : 'transparent',
        color: 'var(--text-primary)',
      }}
    >
      <Icon size={13} style={{ color: 'var(--text-muted)', marginTop: 2 }} />
      <div className="flex-1 min-w-0">
        <div className="text-[13px] font-medium flex items-center gap-2">
          <span>{label}</span>
          {selected && (
            <span
              className="text-[10px] px-1.5 py-0.5 rounded-full"
              style={{ backgroundColor: 'var(--accent-bg)', color: 'var(--accent)' }}
            >
              selected
            </span>
          )}
        </div>
        <div className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          {sub}
        </div>
      </div>
    </button>
  );
}

/**
 * Self-contained directory search. Mirrors the look-and-feel of InlineUserPicker on
 * PromotionDetailPage (debounced search, manual-email fallback for local-auth dev) but
 * trimmed down — no role editor, since the caller of AssigneeFilter only cares about
 * who is assigned, not the role itself.
 */
function PersonSearch({
  currentValue,
  onPick,
}: {
  currentValue: { email: string; displayName: string } | null;
  onPick: (picked: { email: string; displayName: string }) => void;
}) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Array<{ id: string; displayName: string; email: string }>>(
    [],
  );
  const [searching, setSearching] = useState(false);

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
        if (!cancelled) setResults(res.users);
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

  const submitManual = () => {
    const q = query.trim();
    if (!q.includes('@') || !q.includes('.')) return;
    onPick({ email: q, displayName: q });
  };

  return (
    <div>
      {currentValue && (
        <div
          className="flex items-center gap-2 rounded-md px-2 py-1.5 mb-1.5 text-[12px]"
          style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
        >
          <User size={11} style={{ color: 'var(--text-muted)' }} />
          <span className="font-medium truncate">{currentValue.displayName}</span>
          <span className="text-[10px] truncate" style={{ color: 'var(--text-muted)' }}>
            {currentValue.email}
          </span>
        </div>
      )}
      <input
        autoFocus
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder="Search directory (name or email)..."
        className="w-full rounded-md border px-2 py-1.5 text-[12px] outline-none"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-secondary)',
          color: 'var(--text-primary)',
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && results.length === 0) submitManual();
        }}
      />
      {query.trim().length >= 2 && (
        <div
          className="mt-1 max-h-40 overflow-y-auto rounded-md border"
          style={{ borderColor: 'var(--border-color)' }}
        >
          {searching && (
            <div className="px-2 py-1.5 text-[11px]" style={{ color: 'var(--text-muted)' }}>
              Searching...
            </div>
          )}
          {!searching && results.length === 0 && (
            <button
              type="button"
              onClick={submitManual}
              className="w-full text-left px-2 py-1.5 text-[12px] flex flex-col transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-primary)' }}
            >
              <span className="font-medium">Use &ldquo;{query.trim()}&rdquo; as email</span>
              <span className="text-[10px]" style={{ color: 'var(--text-muted)' }}>
                No directory matches — sent as-is.
              </span>
            </button>
          )}
          {!searching &&
            results.map((u) => (
              <button
                key={u.id}
                type="button"
                onClick={() => onPick({ email: u.email, displayName: u.displayName })}
                className="w-full text-left px-2 py-1.5 text-[12px] flex flex-col transition-opacity hover:opacity-80"
                style={{ color: 'var(--text-primary)' }}
              >
                <span className="font-medium truncate">{u.displayName}</span>
                <span className="text-[10px] truncate" style={{ color: 'var(--text-muted)' }}>
                  {u.email}
                </span>
              </button>
            ))}
        </div>
      )}
    </div>
  );
}

function formatLabel(v: AssigneeFilterValue): string {
  switch (v.mode) {
    case 'all':
      return 'Anyone';
    case 'me':
      return 'Me';
    case 'unassigned':
      return 'Unassigned';
    case 'person':
      return v.displayName || v.email;
  }
}

function iconFor(v: AssigneeFilterValue) {
  switch (v.mode) {
    case 'all':
      return Users;
    case 'me':
      return User;
    case 'unassigned':
      return UserX;
    case 'person':
      return User;
    default:
      return Users;
  }
}

// ── localStorage helpers exported for MyQueuePage to keep persistence colocated ───────────
export const ASSIGNEE_FILTER_STORAGE_KEY = 'me.queue.assigneeFilter';

export function loadAssigneeFilter(): AssigneeFilterValue {
  try {
    const raw = window.localStorage.getItem(ASSIGNEE_FILTER_STORAGE_KEY);
    if (!raw) return { mode: 'all' };
    const parsed = JSON.parse(raw);
    if (
      parsed &&
      typeof parsed === 'object' &&
      (parsed.mode === 'all' ||
        parsed.mode === 'me' ||
        parsed.mode === 'unassigned' ||
        (parsed.mode === 'person' &&
          typeof parsed.email === 'string' &&
          typeof parsed.displayName === 'string'))
    ) {
      return parsed as AssigneeFilterValue;
    }
  } catch {
    // Ignore — corrupted entry; fall through to default.
  }
  return { mode: 'all' };
}

export function saveAssigneeFilter(value: AssigneeFilterValue): void {
  try {
    window.localStorage.setItem(ASSIGNEE_FILTER_STORAGE_KEY, JSON.stringify(value));
  } catch {
    // Ignore — quota or disabled storage; the page just won't persist across reloads.
  }
}

