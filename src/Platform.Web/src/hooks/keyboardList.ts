import { createContext, useCallback, useContext, type KeyboardEvent as ReactKeyboardEvent } from 'react';

/**
 * Context and row hook behind {@link KeyboardList}.
 *
 * Split out of the component file so that file exports only a component: Vite's react-refresh
 * boundary can't hot-reload a module that mixes components with hooks and helpers, and the whole
 * shell would do a full page reload on every edit to it.
 */

export interface KeyboardListContextValue {
  activeIndex: number;
  setActiveIndex: (index: number) => void;
  register: (index: number, element: HTMLElement | null) => void;
  /** Columns per row. 1 for a vertical list; a matrix sets this to enable Left/Right. */
  columns: number;
}

export const KeyboardListContext = createContext<KeyboardListContextValue | null>(null);

/** Marks a row element so the shortcut layer can find the focused row and its actions. */
export const KEYBOARD_ROW_ATTR = 'data-kbd-row';

/**
 * Marks a list container, so a list that runs out of rows can hand off to the next one on the page.
 * Matched in document order, which is the order the reader sees regardless of the React tree.
 */
export const KEYBOARD_LIST_ATTR = 'data-kbd-list';

/** Selector for any keyboard-list row. */
export const KEYBOARD_ROW_SELECTOR = `[${KEYBOARD_ROW_ATTR}]`;

/**
 * The row the row-action shortcuts should act on, or null.
 *
 * Focus wins when it is inside a row. Otherwise it falls back to the list's roving entry point — the
 * one row left tabbable — which is what makes `o` and `a` work when focus has wandered to the filter
 * strip or the page heading. Requiring focus to be exactly on the row was the reason `o` looked
 * broken: on a freshly loaded page nothing was focused, so every row action silently did nothing.
 *
 * The fallback is safe for the destructive bindings because `A` and `R` confirm before they commit,
 * and the confirmation names the promotion it is about to act on.
 */
export function activeKeyboardRow(): HTMLElement | null {
  const active = document.activeElement;
  if (active instanceof HTMLElement) {
    const focusedRow = active.closest<HTMLElement>(KEYBOARD_ROW_SELECTOR);
    if (focusedRow) return focusedRow;
  }
  return document.querySelector<HTMLElement>(`${KEYBOARD_ROW_SELECTOR}[tabindex="0"]`);
}

/**
 * Puts focus on a list when nothing else has a claim on the arrow keys.
 *
 * The rule this serves: if there is no focus and nothing going on, the arrows should iterate *some*
 * list. Auto-focus on mount covers arrival, but not every way of ending up idle — clicking a heading,
 * dismissing something, or a list that populated after a slow fetch all leave the caret nowhere
 * useful. Rather than enumerate those, this runs off the arrow key itself: press one with focus
 * adrift, and it lands on the nearest list's entry row, which then handles the keystroke normally.
 *
 * Returns whether it moved focus, so the caller can decide whether the key was consumed.
 */
export function focusIdleKeyboardList(): boolean {
  const active = document.activeElement;
  if (active instanceof HTMLElement) {
    // Already somewhere with its own arrow behaviour, or somewhere we must not disturb.
    if (active.closest(KEYBOARD_ROW_SELECTOR)) return false;
    if (active.closest('[role="dialog"]')) return false;
    if (active.closest('[role="group"]')) return false;
    const tag = active.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || active.isContentEditable) {
      return false;
    }
  }
  // The roving entry point of the first list on the page, falling back to its first row for a list
  // that hasn't established one yet.
  const target =
    document.querySelector<HTMLElement>(`${KEYBOARD_ROW_SELECTOR}[tabindex="0"]`)
    ?? document.querySelector<HTMLElement>(KEYBOARD_ROW_SELECTOR);
  if (!target) return false;
  target.focus();
  return true;
}

/**
 * Props for one row of a {@link KeyboardList}. Spread onto the row's outermost element.
 *
 * `onActivate` runs on click, Enter and Space — the same three gestures a native button answers to.
 * Space is `preventDefault`ed so activating a row doesn't also scroll the page.
 */
export function useKeyboardListRow(
  index: number,
  onActivate: () => void,
  options: {
    label?: string;
    disabled?: boolean;
    /**
     * ARIA role for the row. Defaults to `button`, which is right for a card. Pass `null` for a
     * `<tr>` or `<td>`: `role="button"` there would override the implicit row/cell role and take the
     * table structure away from a screen reader, which is the part that makes a matrix readable.
     */
    role?: 'button' | null;
  } = {},
) {
  const context = useContext(KeyboardListContext);
  const { label, disabled = false, role = 'button' } = options;
  const isActive = context ? context.activeIndex === index : index === 0;

  const ref = useCallback(
    (element: HTMLElement | null) => context?.register(index, element),
    [context, index],
  );

  return {
    ref,
    [KEYBOARD_ROW_ATTR]: '',
    role: role ?? undefined,
    // Roving tabindex: exactly one row is reachable by Tab, the rest by arrow keys.
    tabIndex: disabled ? -1 : isActive ? 0 : -1,
    'aria-label': label,
    'aria-disabled': disabled || undefined,
    onClick: disabled ? undefined : onActivate,
    // Clicking or tabbing to a row makes it the arrow-key origin, so the two stay in step.
    onFocus: () => context?.setActiveIndex(index),
    onKeyDown: (event: ReactKeyboardEvent<HTMLElement>) => {
      if (disabled) return;
      // Let a nested control answer for itself — Enter on the Jira link should follow the link.
      if (event.target !== event.currentTarget) return;
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        onActivate();
      }
    },
  };
}
