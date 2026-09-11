import { useEffect, useState } from 'react';
import { hiddenThemeInfo, useHiddenThemeStore } from '@/stores/hiddenThemeStore';

/** How long the confirmation stays up. Long enough to read a tagline, short enough not to nag. */
const VISIBLE_MS = 2800;

/**
 * The one piece of UI the hidden themes have: a brief toast naming the theme you just switched to,
 * and how to get back. Without it the chord feels like a glitch — the whole app recolours with no
 * acknowledgement — and a colleague who bumps into it by accident has no idea what happened or how
 * to undo it. Announced as a live region for the same reason.
 *
 * Mounted by {@link KeyboardLayer}; renders nothing until the first switch.
 */
export function HiddenThemeToast() {
  const theme = useHiddenThemeStore((s) => s.theme);
  const changedAt = useHiddenThemeStore((s) => s.changedAt);
  // The switch the toast has already timed out for. Visibility is derived from this rather than
  // held as its own flag, so the effect below only ever sets state from the timer callback.
  const [expiredAt, setExpiredAt] = useState(0);

  useEffect(() => {
    // changedAt is 0 until the first switch of this session; a persisted theme on load stays quiet.
    if (changedAt === 0) return;
    const timer = window.setTimeout(() => setExpiredAt(changedAt), VISIBLE_MS);
    return () => window.clearTimeout(timer);
  }, [changedAt]);

  const visible = changedAt !== 0 && expiredAt !== changedAt;
  if (!visible) return null;

  const info = theme ? hiddenThemeInfo(theme) : null;

  return (
    <div
      role="status"
      aria-live="polite"
      className="fixed bottom-6 left-1/2 -translate-x-1/2 z-[1300] flex items-center gap-3 px-4 py-2.5 rounded-full text-[13px] pointer-events-none"
      style={{
        backgroundColor: 'var(--bg-elevated)',
        color: 'var(--text-primary)',
        border: '1px solid var(--border-strong)',
        boxShadow: 'var(--shadow-lg)',
      }}
    >
      <span
        aria-hidden
        className="inline-block w-2.5 h-2.5 rounded-full shrink-0"
        style={{ backgroundColor: 'var(--accent)' }}
      />
      {info ? (
        <>
          <span className="font-semibold">{info.name}</span>
          <span style={{ color: 'var(--text-secondary)' }}>{info.tagline}</span>
          <Hint keys="t 0" label="to leave" />
        </>
      ) : (
        <>
          <span className="font-semibold">Back to normal</span>
          <Hint keys="t t" label="to wander again" />
        </>
      )}
    </div>
  );
}

function Hint({ keys, label }: { keys: string; label: string }) {
  return (
    <span className="flex items-center gap-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
      {keys.split(' ').map((key, i) => (
        <kbd
          key={`${key}-${i}`}
          className="px-1.5 py-0.5 rounded text-[10px] font-mono font-medium"
          style={{
            backgroundColor: 'var(--bg-secondary)',
            color: 'var(--text-primary)',
            border: '1px solid var(--border-color)',
          }}
        >
          {key}
        </kbd>
      ))}
      {label}
    </span>
  );
}
