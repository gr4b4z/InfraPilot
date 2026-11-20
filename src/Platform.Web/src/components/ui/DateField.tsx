import { useRef } from 'react';
import type { CSSProperties } from 'react';
import { CalendarDays } from 'lucide-react';

/**
 * A date (or date and time) field with a calendar button of its own. The browser's own picker icon
 * isn't something to rely on — some Chromium builds don't draw it at all, and it ignores the theme —
 * so the field draws one and opens the native picker itself, from the button or a click in the field.
 * The value stays the native one: "yyyy-MM-dd", or "yyyy-MM-ddTHH:mm" for `datetime-local`.
 */
export function DateField({
  type = 'date',
  value,
  onChange,
  className = '',
  style,
  ariaLabel,
}: {
  type?: 'date' | 'datetime-local';
  value: string;
  onChange: (value: string) => void;
  className?: string;
  style?: CSSProperties;
  ariaLabel?: string;
}) {
  const ref = useRef<HTMLInputElement>(null);
  const open = () => {
    const input = ref.current;
    if (!input) return;
    input.focus();
    try {
      input.showPicker?.();
    } catch {
      // Not allowed here (or not supported): the field still takes typed input.
    }
  };
  return (
    <span className="relative flex items-center">
      <input
        ref={ref}
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onClick={open}
        className={`date-field w-full pr-8 ${className}`}
        style={style}
        aria-label={ariaLabel}
      />
      <button
        type="button"
        onClick={open}
        tabIndex={-1}
        className="absolute right-2 p-0.5 rounded hover:opacity-70"
        style={{ color: 'var(--text-muted)' }}
        aria-label={type === 'date' ? 'Pick a date' : 'Pick a date and time'}
      >
        <CalendarDays size={14} />
      </button>
    </span>
  );
}
