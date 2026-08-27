import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { X } from 'lucide-react';

const SEPARATORS = /[,\n\r\t;]/;

const inputStyle = {
  borderColor: 'var(--border-color)',
  backgroundColor: 'var(--bg-primary)',
  color: 'var(--text-primary)',
};

/** Splits a typed or pasted string on the separators people actually use in a list of names. */
function splitValues(raw: string): string[] {
  return raw
    .split(/[,\n\r\t;]+/)
    .map((v) => v.trim())
    .filter((v) => v.length > 0);
}

/**
 * A set of short free-text values, shown as removable chips with a suggestion list.
 *
 * Built for the webhook filter dimensions, where the two halves of the problem pull in opposite
 * directions: the value is usually one the platform already knows (so remembering the exact spelling
 * of a service should not be the operator's job), but a subscription is often wired *before* the
 * first deploy that would match it — so anything typed has to be accepted. Suggestions narrow while
 * typing and never gate what can be committed.
 *
 * Commit is Enter, a separator, or blur; a paste of a comma- or newline-separated list lands as
 * several chips at once, which is how a list of products arrives from a spreadsheet or a chat
 * message. Backspace on an empty box removes the last chip — the standard gesture, and the only way
 * to correct a typo without reaching for the mouse.
 */
export function MultiValueInput({
  values,
  onChange,
  suggestions = [],
  placeholder,
  ariaLabel,
}: {
  values: string[];
  onChange: (next: string[]) => void;
  /** Known values, offered as a dropdown. Never a constraint on what can be entered. */
  suggestions?: string[];
  placeholder?: string;
  ariaLabel: string;
}) {
  const [draft, setDraft] = useState('');
  const [focused, setFocused] = useState(false);
  const [active, setActive] = useState(-1);
  const listId = useId();
  const blurTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const listRef = useRef<HTMLDivElement | null>(null);

  const has = (value: string) => values.some((v) => v.toLowerCase() === value.trim().toLowerCase());

  const add = (raw: string) => {
    const incoming = splitValues(raw).filter(
      // De-duplicated against the existing chips and against the rest of this same paste, so a list
      // with a repeat in it does not produce two identical chips.
      (v, i, all) => !has(v) && all.findIndex((o) => o.toLowerCase() === v.toLowerCase()) === i
    );
    if (incoming.length > 0) onChange([...values, ...incoming]);
    setDraft('');
    setActive(-1);
  };

  const remove = (value: string) => onChange(values.filter((v) => v !== value));

  const filtered = useMemo(() => {
    const needle = draft.trim().toLowerCase();
    const taken = new Set(values.map((v) => v.toLowerCase()));
    const available = suggestions.filter((s) => !taken.has(s.toLowerCase()));
    return needle ? available.filter((s) => s.toLowerCase().includes(needle)) : available;
  }, [suggestions, values, draft]);

  const activeIndex = active < filtered.length ? active : -1;
  const open = focused && filtered.length > 0;

  useEffect(() => {
    if (activeIndex < 0) return;
    listRef.current?.querySelectorAll('button')[activeIndex]?.scrollIntoView({ block: 'nearest' });
  }, [activeIndex]);

  return (
    <div className="relative">
      <div
        className="flex flex-wrap items-center gap-1.5 px-2 py-1.5 rounded-lg border transition-colors focus-within:border-[var(--accent)]"
        style={inputStyle}
      >
        {values.map((value) => (
          <span
            key={value}
            className="inline-flex items-center gap-1 px-2 py-0.5 rounded-md text-[12px] font-medium"
            style={{
              backgroundColor: 'var(--accent-muted)',
              color: 'var(--accent)',
              border: '1px solid var(--accent)',
            }}
          >
            {value}
            <button
              type="button"
              onClick={() => remove(value)}
              aria-label={`Remove ${value} from ${ariaLabel}`}
              className="transition-opacity hover:opacity-60"
            >
              <X size={11} />
            </button>
          </span>
        ))}
        <input
          type="text"
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-activedescendant={activeIndex >= 0 ? `${listId}-${activeIndex}` : undefined}
          aria-label={ariaLabel}
          value={draft}
          // A separator commits what precedes it and leaves the rest in the box — which is what
          // typing "a, b" one character at a time has to feel like, and what makes a pasted list
          // land as chips rather than as one long value.
          onChange={(e) => {
            const next = e.target.value;
            if (SEPARATORS.test(next)) {
              const parts = splitValues(next);
              const trailing = /[,\n\r\t;]\s*$/.test(next) ? '' : (parts.pop() ?? '');
              if (parts.length > 0) add(parts.join(','));
              setDraft(trailing);
              return;
            }
            setActive(-1);
            setDraft(next);
          }}
          onFocus={() => {
            setActive(-1);
            setFocused(true);
          }}
          onMouseDown={() => setFocused(true)}
          onBlur={() => {
            blurTimer.current = setTimeout(() => {
              setFocused(false);
              // Blur commits: a half-typed value left in the box reads as an entry that was saved,
              // and nothing on screen says otherwise.
              if (draft.trim()) add(draft);
            }, 150);
          }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
              e.preventDefault();
              if (!focused) {
                setFocused(true);
                return;
              }
              const next = activeIndex + (e.key === 'ArrowDown' ? 1 : -1);
              setActive(next < 0 ? filtered.length - 1 : next >= filtered.length ? 0 : next);
            } else if (e.key === 'Enter') {
              e.preventDefault();
              if (open && activeIndex >= 0) add(filtered[activeIndex]);
              else if (draft.trim()) add(draft);
            } else if (e.key === 'Escape') {
              setFocused(false);
            } else if (e.key === 'Backspace' && draft === '' && values.length > 0) {
              remove(values[values.length - 1]);
            }
          }}
          placeholder={values.length === 0 ? placeholder : ''}
          className="flex-1 min-w-[8rem] bg-transparent text-[13px] outline-none py-0.5"
          style={{ color: 'var(--text-primary)' }}
        />
      </div>
      {open && (
        <div
          ref={listRef}
          id={listId}
          role="listbox"
          aria-label={ariaLabel}
          className="absolute z-20 mt-1 top-full left-0 right-0 max-h-56 overflow-y-auto rounded-lg border shadow-lg"
          style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
          onMouseDown={() => {
            if (blurTimer.current) clearTimeout(blurTimer.current);
          }}
        >
          {filtered.map((value, i) => (
            <button
              key={value}
              id={`${listId}-${i}`}
              type="button"
              role="option"
              aria-selected={false}
              onClick={() => add(value)}
              onMouseEnter={() => setActive(i)}
              className="w-full text-left px-3 py-1.5 text-[13px] truncate transition-colors"
              style={{
                color: 'var(--text-primary)',
                backgroundColor: i === activeIndex ? 'var(--bg-secondary)' : 'transparent',
              }}
            >
              {value}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
