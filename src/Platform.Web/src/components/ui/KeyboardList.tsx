import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type ReactNode,
} from 'react';
import {
  KEYBOARD_LIST_ATTR,
  KEYBOARD_ROW_ATTR,
  KEYBOARD_ROW_SELECTOR,
  KeyboardListContext,
  type KeyboardListContextValue,
} from '@/hooks/keyboardList';
import { isTypingTarget } from '@/lib/keys';

/** Anything inside a row that would otherwise become its own tab stop. */
const NESTED_FOCUSABLE =
  'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]';

/**
 * Keyboard navigation for the app's list surfaces.
 *
 * These lists are the entry point to every workflow here — you find a work item, a promotion or a
 * deployment in a list and open it — and they were built as `div`/`tr`/`td` with an `onClick`, which
 * made them unreachable without a mouse. This wraps them in the standard roving-tabindex pattern:
 * the list is a single tab stop, and the arrow keys (or `j`/`k`) move between rows inside it.
 *
 * Roving tabindex rather than making every row tabbable: a 60-row queue would otherwise put 60 stops
 * between the filters and the pagination, which is technically accessible and miserable to use.
 *
 * Controls *inside* a row are taken out of the tab order too, so Tab really does mean "next region".
 * A 25-row promotions list carries a tracker link, a work-item chip and a "View" affordance per row;
 * left tabbable those turned one region into ~30 stops, which is the thing that makes Tab useless for
 * getting anywhere. Everything they did is still reachable: Enter opens the row, `o` opens its tracker
 * reference, `a` opens its assign picker, and a chip pointing at some other record is reachable by
 * opening the row and arrowing the list inside it.
 *
 * ```tsx
 * <KeyboardList count={items.length} ariaLabel="Promotions">
 *   {items.map((item, i) => <Row key={item.id} index={i} … />)}
 * </KeyboardList>
 *
 * // inside Row — from '@/hooks/keyboardList'
 * const rowProps = useKeyboardListRow(index, () => navigate(path));
 * return <div {...rowProps}>…</div>;
 * ```
 */
export function KeyboardList({
  children,
  count,
  ariaLabel,
  columns = 1,
  /** Element to render. `tbody` for tables, `div` otherwise. */
  as: Tag = 'div',
  className,
  autoFocus = true,
  sweepNestedTabStops = true,
}: {
  children: ReactNode;
  /** Number of rows. Bounds navigation and keeps the cursor inside a list that shrank. */
  count: number;
  ariaLabel: string;
  columns?: number;
  as?: 'div' | 'tbody';
  className?: string;
  /**
   * Put focus on the first row once the list has one. Set false for a list that isn't the point of
   * its page — a secondary panel shouldn't grab the caret from the primary one.
   */
  autoFocus?: boolean;
  /**
   * Take the rows' own links and buttons out of the tab order. Right for a navigation list, where a
   * row is a destination and its links are shortcuts into the same place — that is what keeps Tab
   * meaning "next region".
   *
   * Set false where the rows *are* the content rather than links to it: the services view of a
   * release note is a handful of blocks whose work-item and pull-request links are the point of the
   * page, and sweeping those away would take the only keyboard route to them.
   */
  sweepNestedTabStops?: boolean;
}) {
  const [storedIndex, setStoredIndex] = useState(0);
  const elements = useRef(new Map<number, HTMLElement>());
  const hasAutoFocused = useRef(false);
  const container = useRef<HTMLElement>(null);

  // Clamped on read rather than corrected in an effect. A filter change can shrink the list under
  // the cursor, and deriving the valid index avoids both the extra render pass and the moment where
  // the stored index points at a row that no longer exists.
  const activeIndex = count === 0 ? 0 : Math.min(storedIndex, count - 1);

  const register = useCallback((index: number, element: HTMLElement | null) => {
    if (element) elements.current.set(index, element);
    else elements.current.delete(index);
  }, []);

  const focusIndex = useCallback((index: number) => {
    const clamped = Math.max(0, Math.min(index, count - 1));
    setStoredIndex(clamped);
    elements.current.get(clamped)?.focus();
  }, [count]);

  // Land on the first row as soon as the list has one, so the arrow keys work on arrival instead of
  // needing a Tab first.
  //
  // The guard is a blocklist, not an allowlist. It used to only fire when focus was still on <body>
  // or the main region, which missed the most ordinary way of arriving: clicking "Promotions" in the
  // sidebar leaves focus on that nav link, so the list came up with the caret still in the nav and
  // the arrow keys doing nothing. Now anything that isn't a place we must not interrupt is fair game.
  //
  // What we must not interrupt:
  //   - a text field, or the shortcut would fight typing;
  //   - an open dialog or popover, which owns the keyboard while it is up;
  //   - another keyboard list, so two lists on one page don't fight over the caret.
  // Plus: once per mount, so refetches and filter changes don't yank the caret back to the top, and
  // `preventScroll`, since jumping the viewport on arrival reads as a glitch.
  useEffect(() => {
    if (!autoFocus || hasAutoFocused.current || count === 0) return;
    const active = document.activeElement;
    if (active instanceof HTMLElement) {
      if (isTypingTarget(active)) return;
      if (active.closest('[role="dialog"]')) return;
      if (active.closest(KEYBOARD_ROW_SELECTOR)) return;
    }
    const first = elements.current.get(0);
    if (!first) return;
    hasAutoFocused.current = true;
    first.focus({ preventScroll: true });
  }, [autoFocus, count]);

  // Keep the rows' own contents out of the tab order — see the note on Tab above. Runs after every
  // render with no dependency array, because rows arrive and change as data loads and filters apply,
  // and a stop that reappears on a refetch would be just as disruptive as never removing it.
  //
  // Done here rather than at each call site so no row component has to remember: the rule is a
  // property of being inside a keyboard list, not of any particular row's markup.
  useEffect(() => {
    if (!sweepNestedTabStops) return;
    const root = container.current;
    if (!root) return;
    for (const el of root.querySelectorAll<HTMLElement>(NESTED_FOCUSABLE)) {
      // The row itself is the tab stop; only its descendants get pulled out.
      if (el.hasAttribute(KEYBOARD_ROW_ATTR)) continue;
      el.tabIndex = -1;
    }
  });

  /**
   * Hands off to the next (or previous) list on the page when this one runs out.
   *
   * A page can hold several lists — My tasks stacks promotions above work items — and stopping dead
   * at the last row makes the second list look unreachable by keyboard. Arrowing past the end now
   * continues into the next list's first row, and arrowing above the first row lands on the previous
   * list's last row, so one continuous run of `↓` walks the whole page.
   *
   * Document order, not the React tree, because that is the order the reader sees. Lists with no rows
   * are skipped rather than swallowing the keystroke. No wrap-around at the two extremes: rolling from
   * the bottom of the page back to the top loses your place with no way to tell it happened.
   */
  const chainTo = (direction: 1 | -1): boolean => {
    const root = container.current;
    if (!root) return false;
    const lists = [...document.querySelectorAll<HTMLElement>(`[${KEYBOARD_LIST_ATTR}]`)];
    const here = lists.indexOf(root);
    if (here === -1) return false;
    const onwards = direction === 1 ? lists.slice(here + 1) : lists.slice(0, here).reverse();
    for (const list of onwards) {
      const rows = list.querySelectorAll<HTMLElement>(KEYBOARD_ROW_SELECTOR);
      if (rows.length === 0) continue;
      // Entering from above lands on the first row; entering from below, the last — so the cursor
      // keeps travelling in the direction it was already going.
      (direction === 1 ? rows[0] : rows[rows.length - 1]).focus();
      return true;
    }
    return false;
  };

  const onKeyDown = (event: ReactKeyboardEvent<HTMLElement>) => {
    // Only handle keys aimed at a row. A keystroke inside a row's own text input or link is that
    // control's business.
    const target = event.target as HTMLElement;
    if (!target.closest(KEYBOARD_ROW_SELECTOR)) return;
    if (event.ctrlKey || event.metaKey || event.altKey) return;

    /**
     * `chain` says whether running off the end should continue into a neighbouring list. Only the
     * vertical moves do: Left/Right cross environments inside the deployment matrix, and Home/End
     * mean "the ends of *this* list", so neither should leave it.
     */
    const step = (delta: number, chain = false) => {
      event.preventDefault();
      const next = activeIndex + delta;
      if (chain && next < 0 && chainTo(-1)) return;
      if (chain && next > count - 1 && chainTo(1)) return;
      focusIndex(next);
    };

    switch (event.key) {
      case 'ArrowDown':
      case 'j':
        return step(columns, true);
      case 'ArrowUp':
      case 'k':
        return step(-columns, true);
      case 'ArrowRight':
        // Only a grid navigates horizontally; in a plain list Left/Right belong to the caret.
        if (columns > 1) return step(1);
        return;
      case 'ArrowLeft':
        if (columns > 1) return step(-1);
        return;
      case 'Home':
        return step(-activeIndex);
      case 'End':
        return step(count - 1 - activeIndex);
      default:
        return;
    }
  };

  const value = useMemo<KeyboardListContextValue>(
    () => ({ activeIndex, setActiveIndex: setStoredIndex, register, columns }),
    [activeIndex, register, columns],
  );

  return (
    <KeyboardListContext.Provider value={value}>
      <Tag
        ref={container as React.Ref<HTMLDivElement & HTMLTableSectionElement>}
        {...{ [KEYBOARD_LIST_ATTR]: '' }}
        onKeyDown={onKeyDown}
        aria-label={ariaLabel}
        className={className}
      >
        {children}
      </Tag>
    </KeyboardListContext.Provider>
  );
}
