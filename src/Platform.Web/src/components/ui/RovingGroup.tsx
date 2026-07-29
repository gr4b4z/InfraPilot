import { useCallback, useEffect, useRef, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from 'react';

/** Controls a strip can contain. Disabled buttons are skipped — they aren't destinations. */
const ITEM_SELECTOR = 'button:not([disabled]),a[href]';

/**
 * A strip of related controls that behaves as one tab stop.
 *
 * Tab is for moving between the regions of a page — filters, the tab strip, the list, the actions —
 * not for walking every control inside them. A six-button strip like "All pending / Awaiting my
 * approval / Approved · awaiting deploy / …" costs six Tab presses to cross on the way to the list,
 * which is what makes keyboard use tedious rather than merely possible.
 *
 * So the strip owns its own arrow keys: Left/Right (and Up/Down, which read the same way on a
 * horizontal strip) move between controls, Home/End jump to the ends, and Tab leaves the strip
 * entirely for the next region.
 *
 * Focus follows selection, which is right for these strips: they are filters, and every one of them
 * applies immediately on click. Arrowing onto a tab therefore activates it — the ARIA "tabs with
 * automatic activation" pattern — so there is never a state where the focused tab and the content
 * below it disagree.
 *
 * Tabbing *into* the strip lands on the control that is currently applied rather than always the
 * first one, which is what makes a filter strip feel like it remembers where you were.
 *
 * Implemented over the DOM rather than an index registry: the children are plain buttons rendered by
 * the caller, so nothing has to thread an index through.
 *
 * ```tsx
 * <RovingGroup ariaLabel="Promotion views" className="flex gap-2">
 *   {tabs.map((t) => <button key={t} aria-pressed={t === view} onClick={…}>{t}</button>)}
 * </RovingGroup>
 * ```
 */
export function RovingGroup({
  children,
  ariaLabel,
  className,
  style,
}: {
  children: ReactNode;
  ariaLabel: string;
  className?: string;
  style?: React.CSSProperties;
}) {
  const ref = useRef<HTMLDivElement>(null);

  const items = useCallback((): HTMLElement[] => {
    const root = ref.current;
    if (!root) return [];
    return [...root.querySelectorAll<HTMLElement>(ITEM_SELECTOR)]
      // Skip anything hidden — a strip can carry controls that only render at some breakpoints.
      .filter((el) => el.offsetParent !== null);
  }, []);

  // No dependency array: the caller re-renders this strip whenever the selection changes, which is
  // exactly when the entry point needs recomputing. Cheap — a handful of nodes and no state.
  useEffect(() => {
    const all = items();
    if (all.length === 0) return;
    // While focus is inside, the arrow handler owns the tab order; resetting it here would fight
    // the move that just happened.
    if (all.some((el) => el === document.activeElement)) return;
    const pressed = all.findIndex(
      (el) =>
        el.getAttribute('aria-pressed') === 'true' || el.getAttribute('aria-selected') === 'true',
    );
    const entry = pressed === -1 ? 0 : pressed;
    all.forEach((el, i) => { el.tabIndex = i === entry ? 0 : -1; });
  });

  const onKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.ctrlKey || event.metaKey || event.altKey) return;

    const all = items();
    if (all.length === 0) return;
    const current = all.indexOf(document.activeElement as HTMLElement);
    if (current === -1) return;

    const to = (index: number) => {
      event.preventDefault();
      const clamped = Math.max(0, Math.min(index, all.length - 1));
      const next = all[clamped];
      all.forEach((el) => { el.tabIndex = el === next ? 0 : -1; });
      next.focus();
      // Focus follows selection — see the note above.
      next.click();
    };

    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        return to(current + 1);
      case 'ArrowLeft':
      case 'ArrowUp':
        return to(current - 1);
      case 'Home':
        return to(0);
      case 'End':
        return to(all.length - 1);
      default:
        return;
    }
  };

  return (
    <div
      ref={ref}
      role="group"
      aria-label={ariaLabel}
      className={className}
      style={style}
      onKeyDown={onKeyDown}
    >
      {children}
    </div>
  );
}
