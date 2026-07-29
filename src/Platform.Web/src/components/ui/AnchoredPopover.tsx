import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

/**
 * A popover that escapes its container.
 *
 * An absolutely-positioned popover inside a card is at the mercy of its ancestors. Two things break
 * it, and both are common here:
 *
 *   - **A transform on an ancestor.** `.card-hover:hover` applies `translateY(-1px)`, which makes the
 *     hovered card a stacking context. The popover's `z-index` becomes local to that card, so the
 *     *next* card in the list — later in the DOM, no z-index needed — paints straight over it.
 *   - **`overflow: hidden` on an ancestor.** Clips the popover to the card's box.
 *
 * Rendering into `document.body` with `position: fixed` sidesteps both: there is no ancestor left to
 * clip against or to trap the stacking order. The cost is that position must be computed rather than
 * inherited, which is what this component does — measuring the anchor, flipping above it when there's
 * no room below, and clamping to the viewport so a popover on a right-hand card can't run off-screen.
 *
 * Usage — the trigger owns the open state, and hands over the element to anchor against:
 *
 * ```tsx
 * const anchorRef = useRef<HTMLButtonElement>(null);
 * <button ref={anchorRef} onClick={() => setOpen(v => !v)}>…</button>
 * {open && (
 *   <AnchoredPopover anchorRef={anchorRef} onClose={() => setOpen(false)}>
 *     …
 *   </AnchoredPopover>
 * )}
 * ```
 *
 * Closes on outside click, on Escape, and on scroll of any ancestor (rather than trying to follow a
 * scrolling anchor, which invites the popover drifting away from what it belongs to).
 */

/**
 * Everything that can hold focus inside a popover. `[tabindex="-1"]` is excluded — those are
 * programmatic focus targets, not tab stops.
 */
const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

/** Gap between the anchor and the popover, in px. */
const OFFSET = 4;
/** Keep this much clear of the viewport edge so a popover never sits flush against it. */
const VIEWPORT_MARGIN = 8;

export function AnchoredPopover({
  anchorRef,
  onClose,
  children,
  align = 'left',
  className = '',
  style,
  /** Fixed width in px. Without one the popover sizes to its content. */
  width,
  /** Accessible name for the dialog. Supply one whenever the trigger's label isn't enough. */
  ariaLabel,
}: {
  anchorRef: React.RefObject<HTMLElement | null>;
  onClose: () => void;
  children: ReactNode;
  align?: 'left' | 'right';
  className?: string;
  style?: React.CSSProperties;
  width?: number;
  ariaLabel?: string;
}) {
  const popoverRef = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState<{ top: number; left: number } | null>(null);

  const reposition = useCallback(() => {
    const anchor = anchorRef.current;
    const popover = popoverRef.current;
    if (!anchor || !popover) return;

    const a = anchor.getBoundingClientRect();
    const p = popover.getBoundingClientRect();

    // Below the anchor by default; above it when the space below can't hold the popover but the
    // space above can. A popover that opens off the bottom of the window is the same bug in a
    // different costume.
    const spaceBelow = window.innerHeight - a.bottom;
    const openUp = spaceBelow < p.height + OFFSET + VIEWPORT_MARGIN && a.top > p.height + OFFSET;
    const top = openUp ? a.top - p.height - OFFSET : a.bottom + OFFSET;

    const preferredLeft = align === 'right' ? a.right - p.width : a.left;
    const maxLeft = window.innerWidth - p.width - VIEWPORT_MARGIN;
    const left = Math.max(VIEWPORT_MARGIN, Math.min(preferredLeft, maxLeft));

    setPosition({ top, left });
  }, [anchorRef, align]);

  // Layout effect so the first measured paint is the positioned one — with a plain effect the
  // popover would be visible at (0,0) for a frame.
  useLayoutEffect(() => {
    reposition();
  }, [reposition]);

  // Restore focus to whatever opened the popover. Captured on mount rather than read at close time,
  // because by then focus is inside the popover that is about to be removed — and an element removed
  // while focused hands focus to <body>, dropping the keyboard user back at the top of the page.
  const previouslyFocused = useRef<HTMLElement | null>(null);

  useEffect(() => {
    // Both nodes are captured here rather than read during cleanup: by then this popover is being
    // torn down, and the anchor that opened it is the element focus belongs back on.
    const anchor = anchorRef.current;
    const popover = popoverRef.current;
    previouslyFocused.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;

    return () => {
      const restoreTo = anchor?.isConnected ? anchor : previouslyFocused.current;
      if (!restoreTo?.isConnected) return;
      // Restore when focus was ours to give back. Testing `contains(activeElement)` alone is not
      // enough: removing the focused node hands focus to <body> before this cleanup runs, so by now
      // the popover contains nothing and the check would decline to restore — dropping the keyboard
      // user at the top of the document, which is the bug this exists to prevent. Treat a bare
      // <body> as "nobody claimed it". Anything else did, and we leave it alone.
      const active = document.activeElement;
      const focusWasOurs = active === null || active === document.body || !!popover?.contains(active);
      if (focusWasOurs) restoreTo.focus();
    };
  }, [anchorRef]);

  // Move focus in on open, so a keyboard user lands on the search box rather than having to Tab
  // through the whole page to reach a popover that is already on screen. Falls back to the popover
  // itself when it holds nothing focusable, which keeps Escape and the Tab trap working.
  useEffect(() => {
    const popover = popoverRef.current;
    if (!popover) return;
    // Skip if the content already claimed focus itself (several callers use autoFocus).
    if (popover.contains(document.activeElement)) return;
    const first = popover.querySelector<HTMLElement>(FOCUSABLE_SELECTOR);
    (first ?? popover).focus();
  }, []);

  useEffect(() => {
    const onDocumentPointerDown = (event: MouseEvent | TouchEvent) => {
      const target = event.target as Node | null;
      if (!target) return;
      // A click on the trigger is the trigger's business — it toggles. Treating it as an outside
      // click too would close and reopen in the same gesture.
      if (popoverRef.current?.contains(target) || anchorRef.current?.contains(target)) return;
      onClose();
    };

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose();
        return;
      }
      if (event.key !== 'Tab') return;

      // Trap Tab. Without this, tabbing past the last control walks into the page behind an open
      // popover — the focus ring disappears behind the overlay and the popover stays open around
      // nothing.
      const popover = popoverRef.current;
      if (!popover) return;
      const focusable = [...popover.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)]
        .filter((el) => el.offsetParent !== null || el === document.activeElement);
      if (focusable.length === 0) {
        event.preventDefault();
        return;
      }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      } else if (event.shiftKey && (document.activeElement === first || document.activeElement === popover)) {
        event.preventDefault();
        last.focus();
      }
    };

    // Closing on scroll keeps the popover from drifting away from its anchor, but a keyboard user
    // arrowing through a long result list scrolls it — and dismissing the popover they are reading
    // would make it unusable. Only an outside scroll closes it.
    const onScroll = (event: Event) => {
      const target = event.target as Node | null;
      if (target && popoverRef.current?.contains(target)) return;
      onClose();
    };

    document.addEventListener('mousedown', onDocumentPointerDown);
    document.addEventListener('touchstart', onDocumentPointerDown);
    document.addEventListener('keydown', onKeyDown);
    // Capture phase: a scroll inside any ancestor container fires here too, not just on window.
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', reposition);

    return () => {
      document.removeEventListener('mousedown', onDocumentPointerDown);
      document.removeEventListener('touchstart', onDocumentPointerDown);
      document.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', reposition);
    };
  }, [anchorRef, onClose, reposition]);

  return createPortal(
    <div
      ref={popoverRef}
      className={`fixed rounded-lg border shadow-lg ${className}`}
      style={{
        // Rendered off-screen until measured rather than hidden: it has to be laid out for its size
        // to be known, and `visibility` would leave it stealing the first click.
        top: position?.top ?? -9999,
        left: position?.left ?? -9999,
        width,
        // A fixed width wider than a phone would run off the edge; the clamp in `reposition` only
        // moves the box, it can't shrink it.
        maxWidth: `calc(100vw - ${VIEWPORT_MARGIN * 2}px)`,
        // Above the app shell (sidebar/topbar) — this is the topmost thing on screen while open.
        zIndex: 1000,
        backgroundColor: 'var(--bg-secondary)',
        borderColor: 'var(--border-color)',
        ...style,
      }}
      role="dialog"
      aria-modal="true"
      aria-label={ariaLabel}
      // Focus fallback for a popover with nothing focusable inside, so Escape and the Tab trap
      // still have somewhere to stand. Not a tab stop.
      tabIndex={-1}
    >
      {children}
    </div>,
    document.body,
  );
}
