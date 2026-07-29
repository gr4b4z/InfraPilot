import { useEffect, useRef, type ReactNode, type RefObject } from 'react';
import { createPortal } from 'react-dom';

/**
 * A centred modal dialog with the focus behaviour a keyboard user needs.
 *
 * Three things that a plain absolutely-positioned div doesn't give you, and that a dialog opened by a
 * keyboard shortcut can't do without:
 *
 *   - **Focus goes in.** A dialog summoned by `/` must put the caret in its input; otherwise the user
 *     has triggered something they then have to Tab across the page to reach.
 *   - **Tab stays in.** Without a trap, Tab walks out into the page behind the overlay and the focus
 *     ring vanishes under it.
 *   - **Focus comes back.** On close, focus returns to whatever had it — the row you were on — rather
 *     than being dropped on `<body>`, which sends the next Tab to the top of the document.
 *
 * Rendered through a portal for the same reason {@link AnchoredPopover} is: no ancestor transform or
 * `overflow: hidden` can clip or re-stack it.
 */
const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

export function Dialog({
  children,
  onClose,
  ariaLabel,
  /** Control to focus on open. Falls back to the first focusable element. */
  initialFocusRef,
  width = 560,
}: {
  children: ReactNode;
  onClose: () => void;
  ariaLabel: string;
  initialFocusRef?: RefObject<HTMLElement | null>;
  width?: number;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  const previouslyFocused = useRef<HTMLElement | null>(null);

  useEffect(() => {
    // Captured here rather than read in cleanup, when this panel is already being torn down.
    const panel = panelRef.current;
    previouslyFocused.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;

    // Timeout so the focus target exists even when it renders in the same commit.
    const timer = window.setTimeout(() => {
      const target = initialFocusRef?.current
        ?? panelRef.current?.querySelector<HTMLElement>(FOCUSABLE_SELECTOR)
        ?? panelRef.current;
      target?.focus();
    }, 0);

    return () => {
      window.clearTimeout(timer);
      const restoreTo = previouslyFocused.current;
      if (!restoreTo?.isConnected) return;
      // Same reasoning as AnchoredPopover: unmounting the focused node drops focus to <body>, so a
      // bare <body> means nobody has claimed it and it's ours to give back. If something else holds
      // focus, leave it there.
      const active = document.activeElement;
      if (active === null || active === document.body || panel?.contains(active)) restoreTo.focus();
    };
  }, [initialFocusRef]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        // Stop here: an open dialog owns Escape, so a shortcut layer further out doesn't also act.
        event.stopPropagation();
        onClose();
        return;
      }
      if (event.key !== 'Tab') return;

      const panel = panelRef.current;
      if (!panel) return;
      const focusable = [...panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)]
        .filter((el) => el.offsetParent !== null || el === document.activeElement);
      if (focusable.length === 0) { event.preventDefault(); return; }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      } else if (event.shiftKey && (document.activeElement === first || document.activeElement === panel)) {
        event.preventDefault();
        last.focus();
      }
    };

    // Capture phase so this runs before the app-level hotkey listener on `document`.
    document.addEventListener('keydown', onKeyDown, true);
    return () => document.removeEventListener('keydown', onKeyDown, true);
  }, [onClose]);

  return createPortal(
    <div
      className="fixed inset-0 z-[1100] flex items-start justify-center p-4 sm:pt-[12vh]"
      style={{ backgroundColor: 'var(--bg-overlay)' }}
      onMouseDown={(event) => {
        // Only a click on the backdrop itself closes — a drag that ends outside the panel shouldn't.
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={ariaLabel}
        tabIndex={-1}
        className="w-full rounded-xl border shadow-xl overflow-hidden"
        style={{
          maxWidth: width,
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          boxShadow: 'var(--shadow-xl)',
        }}
      >
        {children}
      </div>
    </div>,
    document.body,
  );
}
