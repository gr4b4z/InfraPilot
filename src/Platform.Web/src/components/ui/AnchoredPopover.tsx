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
      if (event.key === 'Escape') onClose();
    };

    document.addEventListener('mousedown', onDocumentPointerDown);
    document.addEventListener('touchstart', onDocumentPointerDown);
    document.addEventListener('keydown', onKeyDown);
    // Capture phase: a scroll inside any ancestor container fires here too, not just on window.
    window.addEventListener('scroll', onClose, true);
    window.addEventListener('resize', reposition);

    return () => {
      document.removeEventListener('mousedown', onDocumentPointerDown);
      document.removeEventListener('touchstart', onDocumentPointerDown);
      document.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('scroll', onClose, true);
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
      aria-label={ariaLabel}
    >
      {children}
    </div>,
    document.body,
  );
}
