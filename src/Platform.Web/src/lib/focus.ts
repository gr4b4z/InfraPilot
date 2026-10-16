/**
 * Finding the focusable controls inside an overlay.
 *
 * Shared by {@link AnchoredPopover} and {@link Dialog} so their focus traps can't drift apart — they
 * had a copy each, and both copies had the same bug.
 */

/** Everything that can hold focus. `[tabindex="-1"]` is excluded: those are programmatic targets. */
const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

/**
 * True when an element is rendered — has a box on screen.
 *
 * Not `offsetParent !== null`, which is the obvious test and the wrong one here: `offsetParent` is
 * `null` for *any* element inside a `position: fixed` ancestor, and both overlays are fixed. That made
 * the check discard every control in the overlay, so the trap concluded there was nothing to focus and
 * swallowed Tab — leaving controls beyond the first unreachable by keyboard.
 *
 * `getClientRects()` asks the question actually being asked ("does this take up space?") and is
 * indifferent to positioning.
 */
export function isRendered(el: HTMLElement): boolean {
  return el.getClientRects().length > 0;
}

/**
 * The focusable, rendered controls inside `root`, in tab order.
 *
 * The currently focused element is kept even if it measures as unrendered, so a control that is
 * mid-transition can't drop out of the trap while the user is standing on it.
 */
export function focusableWithin(root: HTMLElement): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)]
    .filter((el) => isRendered(el) || el === document.activeElement);
}

/** The first focusable control inside `root`, for moving focus in when an overlay opens. */
export function firstFocusableWithin(root: HTMLElement): HTMLElement | null {
  return focusableWithin(root)[0] ?? null;
}

/**
 * Keeps Tab inside `root`, wrapping at both ends. Returns whether the keystroke was handled, so the
 * caller can leave the event alone otherwise.
 */
export function trapTab(root: HTMLElement, event: KeyboardEvent): boolean {
  const focusable = focusableWithin(root);
  if (focusable.length === 0) {
    // Nothing to move to — hold focus rather than letting Tab escape behind the overlay.
    event.preventDefault();
    return true;
  }
  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  const active = document.activeElement;

  if (!event.shiftKey && active === last) {
    event.preventDefault();
    first.focus();
    return true;
  }
  if (event.shiftKey && (active === first || active === root)) {
    event.preventDefault();
    last.focus();
    return true;
  }
  // Somewhere in the middle: the browser's own Tab does the right thing.
  return false;
}
