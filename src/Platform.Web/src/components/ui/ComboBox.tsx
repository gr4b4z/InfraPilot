import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { ChevronDown, X } from 'lucide-react';

export interface ComboOption {
  value: string;
  /** Secondary line under the value — what picking this option means, or how many rows it holds. */
  hint?: string;
}

const inputClass =
  'px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]';

const inputStyle = {
  borderColor: 'var(--border-color)',
  backgroundColor: 'var(--bg-primary)',
  color: 'var(--text-primary)',
};

/**
 * A single-value combo box: a free-text input that offers known values as a dropdown.
 *
 * Two surfaces use it, and both need the same compromise between a picker and a text field. The
 * policy form's scope fields must stay typeable — a policy is often created *before* the first
 * build or deploy that would match it, so restricting to known values would make exactly the
 * new-product case impossible. The build registry's filters are the mirror image: the value always
 * exists already, but there are far too many branches to remember one, so the list is the point and
 * typing is how you get to it.
 *
 * The dropdown opens on focus with every option (an empty input is the "what can I put here?"
 * moment), narrows by case-insensitive substring as the user types, and each option can carry a
 * hint line. Arrow keys walk the list, Enter takes the highlighted option, Escape closes without
 * changing anything. Blur is delayed so an option's mousedown wins over the input's blur.
 */
export function ComboBox({
  value,
  onChange,
  options,
  placeholder,
  ariaLabel,
  clearable = false,
  className,
}: {
  value: string;
  onChange: (next: string) => void;
  options: ComboOption[];
  placeholder?: string;
  ariaLabel: string;
  /**
   * Shows an X that empties the field. For filters, where empty is a meaningful state ("any
   * product"); left off for required form fields, where clearing is not an outcome worth a button.
   */
  clearable?: boolean;
  className?: string;
}) {
  const [focused, setFocused] = useState(false);
  const [active, setActive] = useState(-1);
  const listId = useId();
  const blurTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const listRef = useRef<HTMLDivElement | null>(null);

  const filtered = useMemo(() => {
    const needle = value.trim().toLowerCase();
    // An exact match means the user already picked (or typed) a known value — show the full list
    // again so the field still works as a browser, not just a filter.
    const matches = needle
      ? options.filter((o) => o.value.toLowerCase().includes(needle))
      : options;
    return matches.length > 0 || !needle ? matches : options;
  }, [options, value]);

  // The highlight is an index into a list that shrinks as the user types (and as the caller's
  // options arrive), so a stored index can outlive the option it pointed at. Reading it through
  // the current list's length means a stale one lands on "nothing highlighted" rather than on
  // whatever slid into that slot.
  const activeIndex = active < filtered.length ? active : -1;

  useEffect(() => {
    if (activeIndex < 0) return;
    listRef.current?.querySelectorAll('button')[activeIndex]?.scrollIntoView({ block: 'nearest' });
  }, [activeIndex]);

  const open = focused && filtered.length > 0;

  const commit = (next: string) => {
    onChange(next);
    setFocused(false);
  };

  return (
    <div className={`relative ${className ?? ''}`}>
      <input
        type="text"
        role="combobox"
        aria-expanded={open}
        aria-controls={listId}
        // The highlighted option, named so a screen reader reads it as the arrow keys move rather
        // than leaving the caller to infer the list moved under them.
        aria-activedescendant={activeIndex >= 0 ? `${listId}-${activeIndex}` : undefined}
        aria-label={ariaLabel}
        value={value}
        onChange={(e) => {
          // Typing re-narrows the list, so whatever was highlighted is no longer what the arrow
          // keys walked to.
          setActive(-1);
          onChange(e.target.value);
        }}
        onFocus={() => {
          setActive(-1);
          setFocused(true);
        }}
        onBlur={() => {
          blurTimer.current = setTimeout(() => setFocused(false), 150);
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
            if (open && activeIndex >= 0) {
              e.preventDefault();
              commit(filtered[activeIndex].value);
            } else {
              setFocused(false);
            }
          } else if (e.key === 'Escape') {
            setFocused(false);
          }
        }}
        placeholder={placeholder}
        className={`${inputClass} w-full pr-7`}
        style={inputStyle}
      />
      {clearable && value ? (
        <button
          type="button"
          onClick={() => commit('')}
          aria-label={`Clear ${ariaLabel}`}
          title={`Clear ${ariaLabel}`}
          className="absolute right-2 top-1/2 -translate-y-1/2 transition-opacity hover:opacity-60"
          style={{ color: 'var(--text-muted)' }}
        >
          <X size={13} />
        </button>
      ) : (
        <ChevronDown
          size={13}
          className="absolute right-2.5 top-1/2 -translate-y-1/2 pointer-events-none"
          style={{ color: 'var(--text-muted)' }}
        />
      )}
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
          {filtered.map((o, i) => (
            <button
              key={o.value}
              id={`${listId}-${i}`}
              type="button"
              role="option"
              aria-selected={o.value === value}
              // The hint is decoration on the pick; without this the option announces as
              // "provisioner 2 builds", which is not what picking it means.
              aria-label={o.value}
              onClick={() => commit(o.value)}
              onMouseEnter={() => setActive(i)}
              className="w-full text-left px-3 py-1.5 text-[13px] flex flex-col transition-colors"
              style={{
                color: 'var(--text-primary)',
                backgroundColor: i === activeIndex ? 'var(--bg-secondary)' : 'transparent',
              }}
            >
              <span className="font-medium truncate">{o.value}</span>
              {o.hint && (
                <span className="text-[11px] truncate" style={{ color: 'var(--text-muted)' }}>
                  {o.hint}
                </span>
              )}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
