import { useMemo, useRef, useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { inputClass, inputStyle } from './formStyles';

export interface ComboOption {
  value: string;
  /** Secondary line under the value — what picking this option means. */
  hint?: string;
}

/**
 * A single-value combo box: a free-text input that offers known values as a dropdown. Built for
 * the policy form's scope fields, where the right value usually already exists somewhere (a
 * product that deploys, a configured environment) but must stay typeable — a policy is often
 * created *before* the first build or deploy that will match it, so restricting to known values
 * would make exactly the new-product case impossible.
 *
 * The dropdown opens on focus with every option (an empty input is the "what can I put here?"
 * moment), narrows by case-insensitive substring as the user types, and each option can carry a
 * hint line saying what picking it means. Follows the UserPicker dropdown pattern (blur-delay so
 * option mousedown wins over input blur).
 */
export function ComboBox({
  value,
  onChange,
  options,
  placeholder,
  ariaLabel,
}: {
  value: string;
  onChange: (next: string) => void;
  options: ComboOption[];
  placeholder?: string;
  ariaLabel: string;
}) {
  const [focused, setFocused] = useState(false);
  const blurTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const filtered = useMemo(() => {
    const needle = value.trim().toLowerCase();
    // An exact match means the user already picked (or typed) a known value — show the full list
    // again so the field still works as a browser, not just a filter.
    const matches = needle
      ? options.filter((o) => o.value.toLowerCase().includes(needle))
      : options;
    return matches.length > 0 || !needle ? matches : options;
  }, [options, value]);

  return (
    <div className="relative">
      <input
        type="text"
        role="combobox"
        aria-expanded={focused && filtered.length > 0}
        aria-label={ariaLabel}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onFocus={() => setFocused(true)}
        onBlur={() => {
          blurTimer.current = setTimeout(() => setFocused(false), 150);
        }}
        placeholder={placeholder}
        className={`${inputClass} w-full pr-7`}
        style={inputStyle}
      />
      <ChevronDown
        size={13}
        className="absolute right-2.5 top-1/2 -translate-y-1/2 pointer-events-none"
        style={{ color: 'var(--text-muted)' }}
      />
      {focused && filtered.length > 0 && (
        <div
          className="absolute z-20 mt-1 top-full left-0 right-0 max-h-56 overflow-y-auto rounded-lg border shadow-lg"
          style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
          onMouseDown={() => {
            if (blurTimer.current) clearTimeout(blurTimer.current);
          }}
        >
          {filtered.map((o) => (
            <button
              key={o.value}
              type="button"
              onClick={() => {
                onChange(o.value);
                setFocused(false);
              }}
              className="w-full text-left px-3 py-1.5 text-[13px] flex flex-col transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-primary)' }}
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
