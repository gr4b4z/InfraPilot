import {
  useCallback,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type ReactNode,
} from 'react';
import {
  KEYBOARD_ROW_SELECTOR,
  KeyboardListContext,
  type KeyboardListContextValue,
} from '@/hooks/keyboardList';

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
 * One deliberate compromise. Rows contain their own links (the Jira chip, the "View" affordance) and
 * those stay tabbable, so Tab still walks into a row's contents. Making them untabbable would have
 * been the tidier ARIA story but would take away a keyboard route that already worked. Arrow keys
 * skip straight between rows, and the `o` shortcut opens the focused row's reference, so the fast
 * path doesn't depend on tabbing through them.
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
}: {
  children: ReactNode;
  /** Number of rows. Bounds navigation and keeps the cursor inside a list that shrank. */
  count: number;
  ariaLabel: string;
  columns?: number;
  as?: 'div' | 'tbody';
  className?: string;
}) {
  const [storedIndex, setStoredIndex] = useState(0);
  const elements = useRef(new Map<number, HTMLElement>());

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

  const onKeyDown = (event: ReactKeyboardEvent<HTMLElement>) => {
    // Only handle keys aimed at a row. A keystroke inside a row's own text input or link is that
    // control's business.
    const target = event.target as HTMLElement;
    if (!target.closest(KEYBOARD_ROW_SELECTOR)) return;
    if (event.ctrlKey || event.metaKey || event.altKey) return;

    const step = (delta: number) => {
      event.preventDefault();
      focusIndex(activeIndex + delta);
    };

    switch (event.key) {
      case 'ArrowDown':
      case 'j':
        return step(columns);
      case 'ArrowUp':
      case 'k':
        return step(-columns);
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
      <Tag onKeyDown={onKeyDown} aria-label={ariaLabel} className={className}>
        {children}
      </Tag>
    </KeyboardListContext.Provider>
  );
}
