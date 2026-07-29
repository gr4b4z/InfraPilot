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

/** Selector for any keyboard-list row. */
export const KEYBOARD_ROW_SELECTOR = `[${KEYBOARD_ROW_ATTR}]`;

/**
 * The row the user is currently on, or null. Read by the row-action shortcuts, which operate on
 * whatever is focused rather than tracking list state themselves.
 */
export function activeKeyboardRow(): HTMLElement | null {
  const active = document.activeElement;
  if (!(active instanceof HTMLElement)) return null;
  return active.closest<HTMLElement>(KEYBOARD_ROW_SELECTOR);
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
